using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdbTools.bean;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 设备文件浏览：目录列表、上传 / 下载文件与文件夹、新建 / 重命名 / 删除。
    /// 同一台设备只保留一个窗口；所有 adb 调用都在后台线程执行。
    /// </summary>
    public partial class FileBrowserWindow : Window
    {
        /// <summary>按设备序列号登记已打开的窗口。</summary>
        private static readonly Dictionary<string, FileBrowserWindow> OpenWindows =
            new Dictionary<string, FileBrowserWindow>(StringComparer.Ordinal);

        private static readonly SolidColorBrush NoticeErrorBrush = CreateBrush(0xDC, 0x26, 0x26);
        private static readonly SolidColorBrush NoticeNormalBrush = CreateBrush(0x37, 0x41, 0x51);

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly DeviceFileService _service;
        private readonly string _deviceName;
        private readonly string _serial;
        private readonly string _adbPath;

        /// <summary>
        /// 应用名 / 图标缓存使用的设备键（硬件序列号，与应用列表窗口一致）。
        /// 键不一致会导致缓存全部未命中，每次都要重新解析 APK。
        /// </summary>
        private readonly string _deviceKey;

        /// <summary>包名目录的名单匹配：包名至少包含一个点，且只含字母数字下划线。</summary>
        private static readonly Regex PackageNamePattern =
            new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);

        /// <summary>后台解析应用名的任务；切换目录 / 关闭窗口时停止。</summary>
        private AppLabelResolver _resolver;

        /// <summary>当前列表里的条目，用于把异步解析出来的应用名、图标回填到对应行。</summary>
        private List<DeviceFileEntry> _visibleEntries = new List<DeviceFileEntry>();

        /// <summary>本次列表里还没解析出应用名的包（后台补齐用）。</summary>
        private List<string> _pendingPackages = new List<string>();

        /// <summary>包名 → APK 路径，供后台解析使用；没有待解析包时为 null。</summary>
        private Dictionary<string, string> _pendingApks;

        private string _currentPath = "/sdcard";
        private string _lastLocalDirectory;
        private bool _busy;
        private volatile bool _cancelRequested;
        private volatile bool _closed;
        private int _itemCount;

        /// <summary>
        /// 正在以哪个应用的身份（run-as）浏览其私有目录；为空表示普通 shell 模式。
        /// 非空时所有读写都由 <see cref="DeviceFileService.RunAsPackage"/> 带上 run-as 前缀。
        /// </summary>
        private string _runAsPackage;

        /// <summary>当前列表显示的是“应用列表”（而不是文件），此时路径栏等操作要区别对待。</summary>
        private bool _inPackageList;

        /// <summary>进入应用列表前的目录：从列表返回时回到这里。</summary>
        private string _packageListReturnPath = "/sdcard";

        /// <summary>
        /// 从应用列表双击进入的应用包名。root 设备不需要 run-as，
        /// 但“返回上级时要回到应用列表”这件事与身份无关，所以单独记一份。
        /// </summary>
        private string _appListEntryPackage;

        /// <summary>
        /// 当前设备的 adb shell 是否已经是 root（adb root 过的设备、部分模拟器）。
        /// 这种设备所有应用目录都能直接读，不必用 run-as，也不需要提示需要 root。
        /// </summary>
        private bool _rootShell;

        /// <summary>应用列表是否显示全部应用（默认只看可访问的，本窗口内记住选择）。</summary>
        private bool _showAllPackages;

        /// <summary>最近一次读取的应用条目（全部，含不可访问），切换筛选时不必重新探测。</summary>
        private List<DeviceFileEntry> _allPackageEntries = new List<DeviceFileEntry>();

        /// <summary>应用列表状态栏的基础文案，选中项变化时在它后面追加“已选 N 项”。</summary>
        private string _packageStatusBase;

        /// <summary>
        /// 应用列表在路径栏显示的文案（不是真实路径）。
        /// 强调“私有数据”：这类目录在普通文件浏览里是打不开的，只有 debug 包能借 run-as 进入。
        /// </summary>
        private const string AppListPathText = "应用私有数据（第三方可调试应用）";

        // Ctrl + 左键划过多选：是否处于按下状态、是否已真正开始滑动、起点
        private bool _ctrlDragSelecting;
        private bool _ctrlDragActive;
        private Point _dragStartPoint;

        /// <summary>本次目录的原始条目（未排序），点列头时基于它重新排。</summary>
        private List<DeviceFileEntry> _baseEntries = new List<DeviceFileEntry>();

        /// <summary>当前排序列：name / type / size / time / permissions。</summary>
        private string _sortKey = "name";

        /// <summary>当前是否升序。</summary>
        private bool _sortAscending = true;

        /// <summary>列头原始标题，用于切换排序时追加 ▲ / ▼。</summary>
        private readonly Dictionary<System.Windows.Controls.GridViewColumn, string> _headerTitles =
            new Dictionary<System.Windows.Controls.GridViewColumn, string>();

        public FileBrowserWindow(string adbPath, string serial, string deviceName, string deviceKey = null)
        {
            InitializeComponent();

            _deviceName = string.IsNullOrEmpty(deviceName) ? serial : deviceName;
            _serial = serial;
            _adbPath = (adbPath ?? string.Empty).Trim('"');
            _deviceKey = AppDeviceKey.Normalize(
                string.IsNullOrEmpty(deviceKey) ? serial : deviceKey);
            _service = new DeviceFileService(adbPath, serial);
            // 标题基于 XAML 中定义的基础标题追加设备名，改 XAML 的 Title 即可生效
            Title = Title + " - " + _deviceName;

            LoadSortPreference();
            InitSorting();

            lock (OpenWindows)
            {
                OpenWindows[serial] = this;
            }
        }

        #region 《窗口生命周期》

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Navigate(_currentPath);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_busy) return;

            MessageBoxResult result = MessageBox.Show(this,
                "正在传输文件，确定要取消并关闭窗口吗？", "提示",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancelRequested = true;
            _service.Cancel();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
            StopPackageResolver();

            lock (OpenWindows)
            {
                FileBrowserWindow existing;
                if (OpenWindows.TryGetValue(_serial, out existing) && ReferenceEquals(existing, this))
                {
                    OpenWindows.Remove(_serial);
                }
            }
        }

        /// <summary>该设备已有窗口就还原并带到前台。</summary>
        internal static bool TryActivate(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return false;

            FileBrowserWindow window;
            lock (OpenWindows)
            {
                if (!OpenWindows.TryGetValue(serial, out window)) return false;
                if (window._closed || !window.IsLoaded)
                {
                    OpenWindows.Remove(serial);
                    return false;
                }
            }

            try
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                window.Activate();
            }
            catch { }
            return true;
        }

        /// <summary>主界面退出时关闭所有文件浏览窗口。</summary>
        internal static void CloseAll()
        {
            List<FileBrowserWindow> windows;
            lock (OpenWindows)
            {
                windows = new List<FileBrowserWindow>(OpenWindows.Values);
                OpenWindows.Clear();
            }

            foreach (FileBrowserWindow window in windows)
            {
                try { window.Close(); } catch { }
            }
        }

        #endregion

        #region 《目录浏览》

        private void Navigate(string path, string selectName = null)
        {
            if (_busy) return;

            string target = DeviceFileService.Normalize(path);

            // 在应用私有目录模式下输入了该应用目录之外的路径：退出应用模式，按普通 shell 处理
            if (!string.IsNullOrEmpty(_runAsPackage) && !IsInsideAppData(target))
            {
                ExitAppData();
            }
            // root 下没有 run-as 身份，但离开了这个应用目录也要忘掉“从应用列表进来”的记录
            if (!string.IsNullOrEmpty(_appListEntryPackage) && !IsInsideAppListEntry(target))
            {
                _appListEntryPackage = null;
            }
            _inPackageList = false;
            packageFilterButton.Visibility = Visibility.Collapsed;
            fileList.ContextMenu = null;
            SetFileOpsEnabled(true);

            HideListNotice();
            SetStatus("正在读取 " + target + " …");
            StopPackageResolver();

            var thread = new Thread(() =>
            {
                string error;
                List<DeviceFileEntry> entries = _service.List(target, out error);

                // 目录名是包名时补上应用名与图标（缓存优先，未命中的留给后台解析）
                PreparePackageInfo(entries);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _currentPath = target;
                    pathBox.Text = target;
                    UpdateUpButton();
                    _baseEntries = entries;
                    SortAndBind();
                    StartPackageResolver();

                    if (!string.IsNullOrEmpty(error))
                    {
                        // 权限不足、目录不存在这类问题直接盖在列表区域中央，比只在状态栏提示醒目
                        ShowListNotice(MapErrorTitle(error), BuildErrorDetail(error, target), true,
                            target != "/");
                        SetStatus(error);
                    }
                    else if (entries.Count == 0)
                    {
                        ShowListNotice("空目录", "当前目录下没有任何文件或文件夹：\r\n" + target, false, target != "/");
                        SetStatus("空目录");
                    }
                    else
                    {
                        HideListNotice();
                        UpdateCountStatus();
                    }

                    if (!string.IsNullOrEmpty(selectName))
                    {
                        DeviceFileEntry match = entries.FirstOrDefault(
                            o => string.Equals(o.Name, selectName, StringComparison.Ordinal));
                        if (match != null)
                        {
                            fileList.SelectedItem = match;
                            fileList.ScrollIntoView(match);
                        }
                    }
                }));
            })
            {
                IsBackground = true,
                Name = "file-list"
            };
            thread.Start();
        }

        private void refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_inPackageList)
            {
                ShowPackageList(true);
                return;
            }
            Navigate(_currentPath);
        }

        private void root_Click(object sender, RoutedEventArgs e)
        {
            LeaveSpecialModes();
            Navigate("/");
        }

        private void sdcard_Click(object sender, RoutedEventArgs e)
        {
            LeaveSpecialModes();
            Navigate("/sdcard");
        }

        /// <summary>
        /// /data/local/tmp：属 shell 用户，非 root 下唯一能自由读写的系统分区目录，
        /// 常用于放临时脚本、apk 与被 root 保护的文件的过渡副本。
        /// </summary>
        private void tmp_Click(object sender, RoutedEventArgs e)
        {
            LeaveSpecialModes();
            Navigate("/data/local/tmp");
        }

        private void up_Click(object sender, RoutedEventArgs e)
        {
            GoUp();
        }

        /// <summary>
        /// 返回上一级目录：应用列表 → 进入前的目录；
        /// 应用私有目录根 → 回到应用列表；根目录 / 没有上级，直接忽略。
        /// </summary>
        private void GoUp()
        {
            // 已在根目录：这个按钮只表示“返回上级”，没有上级就什么也不做
            if (!_inPackageList
                && string.Equals(DeviceFileService.Normalize(_currentPath), "/", StringComparison.Ordinal))
            {
                return;
            }

            if (_inPackageList)
            {
                Navigate(_packageListReturnPath);
                return;
            }

            // root 设备从应用列表进入时没有 run-as 身份，但仍应在应用根回到应用列表，
            // 否则会普通上溯到 /data/data。
            if (string.IsNullOrEmpty(_runAsPackage) && !string.IsNullOrEmpty(_appListEntryPackage))
            {
                if (IsAppListEntryRoot(_currentPath))
                {
                    ShowPackageList(false);
                    return;
                }

                string parent = DeviceFileService.ParentPath(_currentPath);
                if (!IsInsideAppListEntry(parent)) _appListEntryPackage = null;
                Navigate(parent);
                return;
            }

            if (!string.IsNullOrEmpty(_runAsPackage))
            {
                if (IsAppDataRoot(_currentPath))
                {
                    // 从应用目录返回：直接用上次的应用列表，不再重新探测
                    ShowPackageList(false);
                    return;
                }

                string parent = DeviceFileService.ParentPath(_currentPath);
                if (!IsInsideAppData(parent))
                {
                    ShowPackageList(false);
                    return;
                }
                Navigate(parent);
                return;
            }

            Navigate(DeviceFileService.ParentPath(_currentPath));
        }

        /// <summary>
        /// 返回上级按钮只在确实有上级时可用：根目录 / 下禁用，避免点它变成原地刷新；
        /// </summary>
        private void UpdateUpButton()
        {
            if (upButton == null) return;

            bool atRoot = !_inPackageList
                && string.Equals(DeviceFileService.Normalize(_currentPath), "/", StringComparison.Ordinal);
            upButton.IsEnabled = !atRoot;
            upButton.ToolTip = atRoot
                ? "已在根目录 /，没有上级目录"
                : "返回上一级目录";
        }

        private void go_Click(object sender, RoutedEventArgs e)
        {
            if (_inPackageList)
            {
                // 主动点“前往”：重新读取应用列表
                ShowPackageList(true);
                return;
            }
            Navigate(pathBox.Text);
        }


        private void fileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // MouseDoubleClick 对右键双击也会触发：只允许左键双击进入目录/应用数据
            if (e.ChangedButton != MouseButton.Left) return;

            DeviceFileEntry entry = fileList.SelectedItem as DeviceFileEntry;
            if (entry == null) return;

            // 应用列表里的一行：双击进入该应用的私有目录
            if (entry.Package != null)
            {
                // 非 debug 包在 root 设备上同样可以进（CanOpen 已在构建列表时算好）
                if (entry.CanOpen)
                {
                    EnterAppData(entry.Package);
                }
                else
                {
                    SetStatus("该应用是非debug应用，无法以应用身份查看其私有数据");
                }
                return;
            }

            if (_inPackageList) return;

            if (entry.IsDirectory)
            {
                Navigate(entry.FullPath);
                return;
            }

            // 符号链接可能是目录（如 /sdcard -> /storage/self/primary）：
            // 列目录时已经解析过目标类型，能直接决定进入还是下载；没解析出来才现场再问一次
            if (entry.IsLink)
            {
                if (entry.LinkTargetIsDirectory == true)
                {
                    Navigate(entry.FullPath);
                }
                else if (entry.LinkTargetIsDirectory == false)
                {
                    DownloadEntries(new List<DeviceFileEntry> { entry });
                }
                else
                {
                    OpenLink(entry);
                }
                return;
            }

            DownloadEntries(new List<DeviceFileEntry> { entry });
        }

        /// <summary>解析符号链接指向：目录则进入，文件则下载。</summary>
        private void OpenLink(DeviceFileEntry entry)
        {
            if (_busy) return;

            _busy = true;
            SetStatus("正在解析链接：" + entry.Name + " …");

            var thread = new Thread(() =>
            {
                bool isDirectory = _service.IsDirectory(entry.FullPath);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;

                    if (isDirectory)
                    {
                        Navigate(entry.FullPath);
                    }
                    else
                    {
                        DownloadEntries(new List<DeviceFileEntry> { entry });
                    }
                }));
            })
            {
                IsBackground = true,
                Name = "file-link"
            };
            thread.Start();
        }

        /// <summary>
        /// 窗口级快捷键：不依赖焦点在哪个控件上。
        /// 路径输入框获得焦点时只接管 Enter（跳转），其余按键保留给输入框编辑。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (pathBox.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Enter)
                {
                    Navigate(pathBox.Text);
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.F5)
            {
                if (_inPackageList) ShowPackageList(true);
                else Navigate(_currentPath);
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                GoUp();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                if (!_inPackageList) DeleteSelected();
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------- Ctrl + 左键划过多选

        private void fileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 只支持按住 Ctrl 的划过选择；点在滚动条上交给滚动条
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0
                || IsOverScrollBar(e.OriginalSource as DependencyObject))
            {
                _ctrlDragSelecting = false;
                return;
            }

            _ctrlDragSelecting = true;
            _ctrlDragActive = false;
            _dragStartPoint = e.GetPosition(fileList);
        }

        private void fileList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_ctrlDragSelecting || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            // 中途松开 Ctrl 视为结束
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                _ctrlDragSelecting = false;
                _ctrlDragActive = false;
                return;
            }

            Point point = e.GetPosition(fileList);

            // 移动超过阈值才开始选中，避免 Ctrl + 单击时误触
            if (!_ctrlDragActive)
            {
                if (Math.Abs(point.X - _dragStartPoint.X) < 5 && Math.Abs(point.Y - _dragStartPoint.Y) < 5)
                {
                    return;
                }

                _ctrlDragActive = true;
            }

            System.Windows.Controls.ListViewItem item = GetItemAt(point);
            if (item != null && !item.IsSelected)
            {
                item.IsSelected = true;
            }

            e.Handled = true;
        }

        private void fileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _ctrlDragSelecting = false;
            _ctrlDragActive = false;
        }

        /// <summary>取坐标点下的列表项（不在任何项上时返回 null）。</summary>
        private System.Windows.Controls.ListViewItem GetItemAt(Point point)
        {
            HitTestResult hit = VisualTreeHelper.HitTest(fileList, point);
            if (hit == null) return null;

            return System.Windows.Controls.ItemsControl.ContainerFromElement(fileList, hit.VisualHit)
                as System.Windows.Controls.ListViewItem;
        }

        private static bool IsOverScrollBar(DependencyObject source)
        {
            while (source != null)
            {
                if (source is System.Windows.Controls.Primitives.ScrollBar) return true;
                try
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        #endregion

        #region 《上传 / 下载》

        /// <summary>拖拽本地文件/文件夹到列表：直接上传到当前目录。</summary>
        private void fileList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!_busy && !_inPackageList && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void fileList_Drop(object sender, DragEventArgs e)
        {
            if (_busy || _inPackageList) return;

            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            var items = new List<TransferItem>();
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        items.Add(new TransferItem(true, path,
                            DeviceFileService.JoinDevicePath(_currentPath, new DirectoryInfo(path).Name)));
                    }
                    else if (File.Exists(path))
                    {
                        items.Add(new TransferItem(true, path,
                            DeviceFileService.JoinDevicePath(_currentPath, Path.GetFileName(path))));
                    }
                }
                catch
                {
                    // 单个路径异常不影响其它条目
                }
            }

            if (items.Count > 0) RunTransfer("上传", items);
            e.Handled = true;
        }

        private void uploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _inPackageList) return;

            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            var items = new List<TransferItem>();
            foreach (string file in dialog.FileNames)
            {
                items.Add(new TransferItem(true, file,
                    DeviceFileService.JoinDevicePath(_currentPath, Path.GetFileName(file))));
            }
            RunTransfer("上传", items);
        }

        private void uploadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _inPackageList) return;

            string folder = PickFolder("选择要上传的文件夹", null);
            if (folder == null) return;

            string name = new DirectoryInfo(folder).Name;
            var items = new List<TransferItem>
            {
                new TransferItem(true, folder, DeviceFileService.JoinDevicePath(_currentPath, name))
            };
            RunTransfer("上传", items);
        }

        private void download_Click(object sender, RoutedEventArgs e)
        {
            if (_inPackageList)
            {
                SetStatus("应用列表里不能下载，请先双击进入某个应用的数据目录");
                return;
            }

            List<DeviceFileEntry> selected = SelectedEntries();
            if (selected.Count == 0)
            {
                SetStatus("请先选择要下载的文件或文件夹");
                return;
            }
            DownloadEntries(selected);
        }

        private void DownloadEntries(List<DeviceFileEntry> entries)
        {
            if (_busy || _inPackageList || entries == null || entries.Count == 0) return;

            // 应用列表里的“应用条目”不是真实路径，不能当文件下载
            entries = entries.Where(o => o.Package == null).ToList();
            if (entries.Count == 0)
            {
                SetStatus("应用列表里不能下载，请先双击进入某个应用的数据目录");
                return;
            }

            // 应用私有目录里的文件夹由 DeviceFileService 递归逐个文件取回（run-as 不能用 pull）
            string target = PickFolder("选择下载到哪个文件夹", _lastLocalDirectory);
            if (target == null) return;
            _lastLocalDirectory = target;

            var items = new List<TransferItem>();
            foreach (DeviceFileEntry entry in entries)
            {
                items.Add(new TransferItem(false, Path.Combine(target, entry.Name), entry.FullPath));
            }
            RunTransfer("下载", items);
        }

        private void RunTransfer(string action, List<TransferItem> items)
        {
            if (items == null || items.Count == 0) return;

            _busy = true;
            _cancelRequested = false;
            SetBusyUi(true, action + "中 0/" + items.Count);

            var thread = new Thread(() =>
            {
                int done = 0;
                var failed = new List<string>();

                // 应用私有目录的整目录下载是逐文件取回的，把内部进度也打到状态栏
                _service.Progress = text => UpdateStatus(action + "：" + text);
                try
                {
                    foreach (TransferItem item in items)
                    {
                        if (_cancelRequested) break;

                        UpdateStatus(action + "中 " + (done + 1) + "/" + items.Count + "：" + ShortName(item));

                        string message;
                        if (!_service.Transfer(item.Upload, item.LocalPath, item.DevicePath, out message))
                        {
                            if (_cancelRequested) break;
                            failed.Add(ShortName(item) + "：" + message);
                        }
                        done++;
                    }
                }
                finally
                {
                    _service.Progress = null;
                }

                bool cancelled = _cancelRequested;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;
                    _cancelRequested = false;
                    SetBusyUi(false, null);

                    string summary = cancelled
                        ? action + "已取消（" + done + "/" + items.Count + "）"
                        : action + "完成 " + done + "/" + items.Count;
                    if (failed.Count > 0) summary += "，失败 " + failed.Count + " 项";
                    SetStatus(summary);

                    if (failed.Count > 0)
                    {
                        MessageBox.Show(this,
                            "以下项目" + action + "失败：\r\n\r\n" + string.Join("\r\n", failed.Take(10)),
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    // 上传会改变当前目录内容，下载不影响；统一刷新一次最简单。
                    // 应用列表不是真实目录，刷新会把窗口带到上一个目录，这里必须跳过。
                    if (!_inPackageList) Navigate(_currentPath);
                }));
            })
            {
                IsBackground = true,
                Name = "file-transfer"
            };
            thread.Start();
        }

        private void cancelTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) return;

            _cancelRequested = true;
            _service.Cancel();
            SetStatus("正在取消…");
        }

        #endregion

        #region 《新建 / 重命名 / 删除》

        private void newFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _inPackageList) return;

            var input = new InputWindow { Title = "新建文件夹", Owner = this };
            if (input.ShowDialog() != true) return;

            string name = (input.Result ?? string.Empty).Trim();
            if (!IsValidName(name)) return;

            string path = DeviceFileService.JoinDevicePath(_currentPath, name);
            RunOperation("新建文件夹",
                () =>
                {
                    string error;
                    return _service.Mkdir(path, out error) ? null : error;
                },
                () => Navigate(_currentPath, name));
        }

        private void rename_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _inPackageList) return;

            DeviceFileEntry entry = fileList.SelectedItem as DeviceFileEntry;
            if (entry == null || entry.Package != null)
            {
                SetStatus("请先选择一个要重命名的项目");
                return;
            }

            var input = new InputWindow { Title = "重命名", Owner = this };
            input.content.Text = entry.Name;
            input.content.SelectAll();
            if (input.ShowDialog() != true) return;

            string newName = (input.Result ?? string.Empty).Trim();
            if (newName == entry.Name) return;
            if (!IsValidName(newName)) return;

            RunOperation("重命名",
                () =>
                {
                    string error;
                    return _service.Rename(entry.FullPath, newName, out error) ? null : error;
                },
                () => Navigate(_currentPath, newName));
        }

        private void delete_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelected();
        }

        private void DeleteSelected()
        {
            if (_busy || _inPackageList) return;

            List<DeviceFileEntry> selected = SelectedEntries();
            if (selected.Count == 0)
            {
                SetStatus("请先选择要删除的项目");
                return;
            }

            string names = string.Join("\r\n", selected.Take(10).Select(o => o.Name));
            if (selected.Count > 10) names += "\r\n…（共 " + selected.Count + " 项）";

            // 应用私有目录（run-as 或 root 直接进入）都要额外提醒风险
            bool inAppData = !string.IsNullOrEmpty(_runAsPackage)
                || _currentPath.StartsWith("/data/data/", StringComparison.Ordinal);

            string warning = inAppData
                ? "注意：这是应用私有数据，删除后可能导致该应用崩溃或数据丢失。"
                : "删除后无法恢复。";

            MessageBoxResult result = MessageBox.Show(this,
                "确定要删除以下项目吗？\r\n\r\n" + names + "\r\n\r\n" + warning,
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            RunOperation("删除",
                () =>
                {
                    foreach (DeviceFileEntry entry in selected)
                    {
                        string error;
                        if (!_service.Delete(entry.FullPath, out error))
                        {
                            return entry.Name + "：" + error;
                        }
                    }
                    return null;
                },
                null);
        }

        private void RunOperation(string action, Func<string> operation, Action onSuccess)
        {
            if (_busy) return;

            _busy = true;
            SetBusyUi(true, action + "中…");

            var thread = new Thread(() =>
            {
                string error = operation();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;
                    SetBusyUi(false, null);

                    if (!string.IsNullOrEmpty(error))
                    {
                        SetStatus(action + "失败：" + error);
                        MessageBox.Show(this, action + "失败：\r\n" + error, "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (onSuccess != null) onSuccess();
                    else Navigate(_currentPath);
                }));
            })
            {
                IsBackground = true,
                Name = "file-op"
            };
            thread.Start();
        }

        private bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                SetStatus("名称不能为空");
                return false;
            }
            if (name.IndexOf('/') >= 0 || name == "." || name == "..")
            {
                SetStatus("名称不能包含 / ，也不能是 . 或 ..");
                return false;
            }
            return true;
        }

        #endregion

        #region 《应用私有数据（run-as）》

        private void appData_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (!_inPackageList && string.IsNullOrEmpty(_runAsPackage))
            {
                _packageListReturnPath = _currentPath;
            }
            // 主动点按钮：重新读取（应用有安装/卸载时能刷新）
            ShowPackageList(true);
        }

        /// <summary>
        /// 显示应用列表。<paramref name="reload"/> = false 时直接用上次的结果
        /// （从应用目录返回时用：不必重新探测，几乎瞬间回到列表）；
        /// 点「应用数据」按钮、刷新、前往等主动操作传 true。
        /// </summary>
        private void ShowPackageList(bool reload)
        {
            if (_busy) return;

            // 已有缓存且不是主动刷新：立刻回到列表，不再重复探测（约 0.6 秒）
            if (!reload && _allPackageEntries.Count > 0)
            {
                _inPackageList = true;
                ExitAppData();
                _appListEntryPackage = null;
                pathBox.Text = AppListPathText;
                UpdateUpButton();
                ApplyPackageFilter();
                return;
            }

            _busy = true;
            _inPackageList = true;
            ExitAppData();
            _appListEntryPackage = null;
            UpdateUpButton();
            StopPackageResolver();
            HideListNotice();
            pathBox.Text = AppListPathText;
            fileList.ItemsSource = null;
            packageFilterButton.Visibility = Visibility.Collapsed;
            SetStatus("正在读取应用列表并检测可访问性…");

            var thread = new Thread(() =>
            {
                string error;
                List<DeviceFileEntry> entries;
                try
                {
                    entries = LoadPackageEntries(out error);
                }
                catch (Exception ex)
                {
                    // 后台线程里的异常不能放到外面：会直接结束进程
                    entries = new List<DeviceFileEntry>();
                    error = "读取应用列表失败：" + ex.Message;
                }

                // 补上应用名与图标（缓存优先，未命中的留给后台解析）
                PreparePackageInfo(entries);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;

                    if (!string.IsNullOrEmpty(error))
                    {
                        _allPackageEntries = new List<DeviceFileEntry>();
                        _baseEntries = new List<DeviceFileEntry>();
                        _visibleEntries = new List<DeviceFileEntry>();
                        _itemCount = 0;
                        fileList.ItemsSource = null;
                        packageFilterButton.Visibility = Visibility.Collapsed;
                        ShowListNotice("读取应用列表失败", error, true, false);
                        SetStatus(error);
                        return;
                    }

                    _allPackageEntries = entries;
                    ApplyPackageFilter();
                }));
            })
            {
                IsBackground = true,
                Name = "app-package-list"
            };
            thread.Start();
        }

        /// <summary>
        /// 按「只看可访问 / 显示全部」重新绑定应用列表并更新状态栏。
        /// 默认只看可访问（debug 包，root 设备则是全部），被隐藏的数量在状态栏与按钮上提示。
        /// </summary>
        private void ApplyPackageFilter()
        {
            if (_closed) return;

            // 应用列表不是真实目录，上传/下载/新建/重命名/删除都不可用
            SetFileOpsEnabled(false);
            fileList.ContextMenu = null;

            int total = _allPackageEntries.Count;
            int accessible = _allPackageEntries.Count(o => o.CanOpen);

            if (total == 0)
            {
                _itemCount = 0;
                _baseEntries = new List<DeviceFileEntry>();
                _visibleEntries = new List<DeviceFileEntry>();
                fileList.ItemsSource = null;
                packageFilterButton.Visibility = Visibility.Collapsed;
                ShowListNotice("没有第三方应用",
                    "当前设备没有安装第三方应用。\r\n\r\n"
                    + "这里只列出第三方应用的私有数据目录，系统预装应用不会出现在列表中。",
                    false, false);
                _packageStatusBase = "没有第三方应用";
                statusText.Text = _packageStatusBase;
                return;
            }

            // 没有可访问的应用时先给空态，仍保留“显示全部”入口
            if (accessible == 0 && !_showAllPackages)
            {
                _itemCount = 0;
                _baseEntries = new List<DeviceFileEntry>();
                _visibleEntries = new List<DeviceFileEntry>();
                fileList.ItemsSource = null;
                packageFilterButton.Content = "显示全部（" + total + "）";
                packageFilterButton.Visibility = Visibility.Visible;
                ShowListNotice("没有可调试（debug）应用",
                    "当前设备的第三方应用都不是 debug 包。\r\n\r\n"
                    + "安装 debug 版本后再试，或点下方的“显示全部”查看全部应用。\r\n"
                    + "（这里只列出第三方应用的私有数据目录，系统预装应用不会出现。）",
                    false, false);
                _packageStatusBase = "没有可调试（debug）应用";
                statusText.Text = _packageStatusBase;
                return;
            }

            List<DeviceFileEntry> visible = _showAllPackages
                ? new List<DeviceFileEntry>(_allPackageEntries)
                : _allPackageEntries.Where(o => o.CanOpen).ToList();

            _baseEntries = visible;
            SortAndBind();
            StartPackageResolver();

            bool hasHidden = accessible < total;
            packageFilterButton.Visibility = hasHidden ? Visibility.Visible : Visibility.Collapsed;
            if (hasHidden)
            {
                packageFilterButton.Content = _showAllPackages
                    ? "只看可访问"
                    : "显示全部（" + (total - accessible) + "）";
            }

            HideListNotice();

            string text;
            if (_rootShell)
            {
                // root 设备所有应用目录都能直接读，不需要区分 debug
                text = "共 " + total + " 个第三方应用 · 当前为 root，全部可访问";
            }
            else if (_showAllPackages)
            {
                text = "共 " + total + " 个第三方应用 · 可访问 " + accessible + " 个，其余为非debug应用";
            }
            else
            {
                text = "共 " + accessible + " 个可访问应用 · 已隐藏 " + (total - accessible) + " 个非debug应用";
            }
            text += " · 第三方应用的私有目录（普通浏览无法查看） · 双击进入";

            _packageStatusBase = text;
            statusText.Text = text;
        }

        private void packageFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_allPackageEntries.Count == 0) return;

            _showAllPackages = !_showAllPackages;
            ApplyPackageFilter();
        }

        /// <summary>
        /// 读取第三方应用并批量探测 run-as 是否可用（设备端一条命令完成）。
        /// 返回「可访问优先、再按包名排序」的条目；<paramref name="error"/> 非空表示整体失败。
        /// </summary>
        private List<DeviceFileEntry> LoadPackageEntries(out string error)
        {
            // shell 已经是 root 的设备（adb root / 模拟器）：所有应用目录都能直接读
            _rootShell = _service.IsShellRoot();

            List<string> packages = _service.ListThirdPartyPackages(out error);

            // 一次批量探测：设备端循环执行 run-as，返回 包名 → uid 行
            string probeError;
            Dictionary<string, string> accessible = _service.ProbeRunAsBatch(packages, out probeError);
            if (string.IsNullOrEmpty(error) && packages.Count > 0 && accessible.Count == 0
                && !string.IsNullOrEmpty(probeError))
            {
                error = probeError;
            }

            var entries = new List<DeviceFileEntry>();
            foreach (string package in packages)
            {
                string uid;
                bool canRunAs = accessible.TryGetValue(package, out uid);
                bool canOpen = canRunAs || _rootShell;

                // 两列分工：类型列说明“能不能打开”，权限列说明“以什么身份/为什么打不开”。
                // root 设备上非 debug 包同样可打开，不会因为标注而进不去。
                entries.Add(new DeviceFileEntry
                {
                    Name = package,
                    Package = package,
                    CanRunAs = canRunAs,
                    CanOpen = canOpen,
                    IsDirectory = true,      // 体积列按目录显示 “-”
                    IconOverride = FileIcons.GetPackageIcon(canOpen),
                    TypeTextOverride = canOpen ? "可访问" : "不可访问",
                    Permissions = canRunAs ? ShortUid(uid) : (_rootShell ? "root" : "非debug应用")
                });
            }

            // 可访问的排在前面，其余按包名排序
            entries.Sort((a, b) =>
            {
                if (a.CanRunAs != b.CanRunAs) return a.CanRunAs ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }

        /// <summary>“uid=10184(u0_a184)” → “u0_a184”（权限列放不下整串）。</summary>
        private static string ShortUid(string value)
        {
            int open = value.IndexOf('(');
            int close = value.IndexOf(')', open + 1);
            if (open > 0 && close > open) return value.Substring(open + 1, close - open - 1);
            return value;
        }

        /// <summary>
        /// 进入某个应用的私有目录（/data/data/包名）。
        /// shell 已是 root 时用普通身份直接进（root 反而不能用 run-as 读别的应用），
        /// 否则用 run-as 以该应用身份进。
        /// </summary>
        private void EnterAppData(string package)
        {
            if (string.IsNullOrEmpty(package) || _busy) return;

            _inPackageList = false;
            _appListEntryPackage = package;

            if (_rootShell)
            {
                ExitAppData();
            }
            else
            {
                _runAsPackage = package;
                _service.RunAsPackage = package;
            }

            Navigate("/data/data/" + package);
        }

        /// <summary>退出应用私有目录模式（回到普通 shell 身份）。</summary>
        private void ExitAppData()
        {
            _runAsPackage = null;
            _service.RunAsPackage = null;
        }

        /// <summary>从应用列表 / 应用目录回到普通浏览（根目录、SD 卡、临时目录等按钮）。</summary>
        private void LeaveSpecialModes()
        {
            _inPackageList = false;
            _appListEntryPackage = null;
            ExitAppData();
        }

        private bool IsAppDataRoot(string path)
        {
            if (string.IsNullOrEmpty(_runAsPackage)) return false;
            return string.Equals(DeviceFileService.Normalize(path),
                "/data/data/" + _runAsPackage, StringComparison.Ordinal);
        }

        private bool IsInsideAppData(string path)
        {
            if (string.IsNullOrEmpty(_runAsPackage)) return false;

            string root = "/data/data/" + _runAsPackage;
            string value = DeviceFileService.Normalize(path);
            return string.Equals(value, root, StringComparison.Ordinal)
                || value.StartsWith(root + "/", StringComparison.Ordinal);
        }

        /// <summary>当前路径是否正好是从应用列表进入的那个应用目录根。</summary>
        private bool IsAppListEntryRoot(string path)
        {
            if (string.IsNullOrEmpty(_appListEntryPackage)) return false;
            return string.Equals(DeviceFileService.Normalize(path),
                "/data/data/" + _appListEntryPackage, StringComparison.Ordinal);
        }

        /// <summary>当前路径是否还在从应用列表进入的那个应用目录内。</summary>
        private bool IsInsideAppListEntry(string path)
        {
            if (string.IsNullOrEmpty(_appListEntryPackage)) return false;

            string root = "/data/data/" + _appListEntryPackage;
            string value = DeviceFileService.Normalize(path);
            return string.Equals(value, root, StringComparison.Ordinal)
                || value.StartsWith(root + "/", StringComparison.Ordinal);
        }

        #endregion

        #region 《目录名为包名时补应用名与图标》

        /// <summary>
        /// 目录名恰好是包名时（/data/data、/sdcard/Android/data 等，路径前缀无关），
        /// 批量读取应用名与图标缓存填到条目上；没解析出来的包名留在待办里由后台补齐。
        /// 缓存不可用时保持"包名 + 文件夹图标"兜底，不抛错。
        /// </summary>
        private void PreparePackageInfo(List<DeviceFileEntry> entries)
        {
            _pendingPackages = new List<string>();
            _pendingApks = null;

            var candidates = new List<DeviceFileEntry>();
            foreach (DeviceFileEntry entry in entries)
            {
                if (entry.IsDirectory && IsPackageName(entry.Name)) candidates.Add(entry);
            }
            if (candidates.Count == 0) return;

            List<string> names = candidates.Select(o => o.Name).ToList();

            Dictionary<string, string> labels;
            Dictionary<string, ImageSource> icons;
            HashSet<string> resolvedEmpty;
            try
            {
                labels = AppLabelStore.GetLabels(_deviceKey, names);
                icons = AppIconStore.GetIcons(_deviceKey, names);
                resolvedEmpty = AppLabelStore.GetResolvedNames(_deviceKey, names);
            }
            catch
            {
                return;   // 缓存库不可用：保持包名 + 文件夹图标
            }

            foreach (DeviceFileEntry entry in candidates)
            {
                string label;
                if (labels.TryGetValue(entry.Name, out label) && !string.IsNullOrEmpty(label))
                {
                    entry.AppLabel = label;
                }

                ImageSource icon;
                if (icons.TryGetValue(entry.Name, out icon) && icon != null)
                {
                    entry.IconOverride = icon;
                }

                // 已知"确定没有名字"的不再重试，避免每次进目录都拉一遍 APK
                if (string.IsNullOrEmpty(entry.AppLabel) && !resolvedEmpty.Contains(entry.Name))
                {
                    _pendingPackages.Add(entry.Name);
                }
            }

            if (_pendingPackages.Count > 0)
            {
                _pendingApks = _service.ListPackageApkPaths(out _);
            }
        }

        /// <summary>
        /// 后台解析应用名与图标（复用应用列表的 AppLabelResolver，结果同样写入缓存库）。
        /// 解析完成的回调在 UI 线程，直接回填到对应行。
        /// </summary>
        private void StartPackageResolver(string forcePackage = null)
        {
            StopPackageResolver();
            if (_pendingApks == null || _pendingApks.Count == 0) return;

            // 平时只解析缓存未命中的包；右键「重新获取」时只强制解析选中的那一个包，
            // 解析器把从设备取到的新名称/图标直接覆盖写回缓存，不删除旧数据。
            List<string> names = forcePackage != null
                ? new List<string> { forcePackage }
                : _pendingPackages;
            if (names.Count == 0) return;

            var apps = new List<AppInfo>();
            foreach (string package in names)
            {
                string apkPath;
                if (!_pendingApks.TryGetValue(package, out apkPath) || string.IsNullOrEmpty(apkPath)) continue;
                apps.Add(new AppInfo { PackageName = package, ApkPath = apkPath });
            }
            if (apps.Count == 0) return;

            _resolver = new AppLabelResolver(_adbPath, _serial, _deviceKey, Dispatcher,
                message => { }, 4, forcePackage != null);

            _resolver.ItemResolved += (app, label, version, done, total) =>
            {
                if (!string.IsNullOrEmpty(label)) ApplyLabelToEntries(app.PackageName, label);

                // 全部解析完后恢复“共 N 项 / 可访问 N 个”的状态栏，不要停在进度上
                if (done >= total) UpdateCountStatus();
                else SetStatus("正在解析应用名 " + done + "/" + total);
            };

            _resolver.ItemIconResolved += app =>
            {
                ImageSource icon = AppIconStore.GetIcon(_deviceKey, app.PackageName);
                if (icon != null) ApplyIconToEntries(app.PackageName, icon);
            };

            _resolver.Start(apps);
            SetStatus("正在解析应用名 0/" + apps.Count);
        }

        private void StopPackageResolver()
        {
            AppLabelResolver resolver = _resolver;
            _resolver = null;
            if (resolver == null) return;

            try { resolver.Stop(); } catch { }
        }

        private void ApplyLabelToEntries(string package, string label)
        {
            foreach (DeviceFileEntry entry in AllKnownEntries())
            {
                if (string.Equals(entry.Name, package, StringComparison.Ordinal))
                {
                    entry.AppLabel = label;
                }
            }
        }

        private void ApplyIconToEntries(string package, ImageSource icon)
        {
            foreach (DeviceFileEntry entry in AllKnownEntries())
            {
                if (string.Equals(entry.Name, package, StringComparison.Ordinal))
                {
                    entry.IconOverride = icon;
                }
            }
        }

        /// <summary>当前可见条目 + 应用列表的全部条目（“显示全部”切换后也能拿到解析结果）。</summary>
        private IEnumerable<DeviceFileEntry> AllKnownEntries()
        {
            foreach (DeviceFileEntry entry in _visibleEntries) yield return entry;

            if (!ReferenceEquals(_visibleEntries, _allPackageEntries))
            {
                foreach (DeviceFileEntry entry in _allPackageEntries) yield return entry;
            }
        }

        /// <summary>目录名是否像包名：至少包含一个点，仅字母数字下划线。</summary>
        private static bool IsPackageName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length <= 128 && PackageNamePattern.IsMatch(name);
        }

        #endregion

        #region 《列头排序》

        /// <summary>记下列头原始标题并接管列头点击（同一个列再点一次切换升降序）。</summary>
        private void InitSorting()
        {
            var gridView = fileList.View as System.Windows.Controls.GridView;
            if (gridView != null)
            {
                foreach (System.Windows.Controls.GridViewColumn column in gridView.Columns)
                {
                    string title = column.Header as string;
                    if (!string.IsNullOrEmpty(title)) _headerTitles[column] = title;
                }
            }

            fileList.AddHandler(System.Windows.Controls.GridViewColumnHeader.ClickEvent,
                new RoutedEventHandler(Header_Click));

            UpdateHeaderArrows();
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            var header = e.OriginalSource as System.Windows.Controls.GridViewColumnHeader;
            if (header == null) return;

            string key = SortKeyOf(header.Content as string);
            if (key == null) return;   // 填充列、非排序列

            if (string.Equals(_sortKey, key, StringComparison.Ordinal))
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortKey = key;
                _sortAscending = true;
            }

            UpdateHeaderArrows();
            SortAndBind();
            SaveSortPreference();
            SetStatus(_itemCount + " 项 · 按" + SortTitle(_sortKey) + (_sortAscending ? "升序" : "降序"));
            e.Handled = true;
        }

        /// <summary>读取上次的排序列与方向：配置缺失或值非法时回到“名称升序”。</summary>
        private void LoadSortPreference()
        {
            try
            {
                string key = Globals.AppSettings.FILE_SORT_KEY;
                if (IsSortKey(key)) _sortKey = key;
                _sortAscending = Globals.AppSettings.FILE_SORT_ASC;
            }
            catch
            {
                // 配置不可用时按默认排序，不影响使用
            }
        }

        /// <summary>把当前排序写回配置，下次打开文件管理直接沿用。</summary>
        private void SaveSortPreference()
        {
            try
            {
                Globals.AppSettings.FILE_SORT_KEY = _sortKey;
                Globals.AppSettings.FILE_SORT_ASC = _sortAscending;
            }
            catch
            {
                // 配置写入失败不影响本次排序结果
            }
        }

        private static bool IsSortKey(string key)
        {
            return key == "name" || key == "type" || key == "size"
                || key == "time" || key == "permissions";
        }

        /// <summary>按当前排序规则重排并重新绑定（保持 _visibleEntries 与界面一致）。</summary>
        private void SortAndBind()
        {
            if (_closed) return;

            List<DeviceFileEntry> sorted = ApplySort(_baseEntries);
            _visibleEntries = sorted;
            _itemCount = sorted.Count;
            fileList.ItemsSource = sorted;
        }

        /// <summary>
        /// 右键不改变选中状态：只有点中的那行**本来就是当前单选的项**时才挂「重新获取」菜单。
        /// 右键空白、未选中的行、普通文件/目录或多选时都不显示菜单。
        /// </summary>
        private void fileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;
            ListViewItem item = source == null ? null
                : fileList.ContainerFromElement(source) as ListViewItem;
            UpdateFileContextMenu(item == null ? null : item.DataContext as DeviceFileEntry);
        }

        /// <summary>仅当右键点中的是当前单选的包名文件夹/应用行时挂菜单，其它情况移除菜单。</summary>
        private void UpdateFileContextMenu(DeviceFileEntry clicked)
        {
            DeviceFileEntry selected = fileList.SelectedItem as DeviceFileEntry;
            bool isPackageRow = clicked != null
                && ((_inPackageList && !string.IsNullOrEmpty(clicked.Package))
                    || (!_inPackageList && clicked.IsDirectory && IsPackageName(clicked.Name)));
            bool singlePackageFolder = clicked != null
                && ReferenceEquals(clicked, selected)
                && fileList.SelectedItems.Count == 1
                && isPackageRow;
            fileList.ContextMenu = singlePackageFolder
                ? FindResource("PackageFolderContextMenu") as ContextMenu
                : null;
        }

        /// <summary>
        /// 只重新解析选中的那一个包名：不重列目录、不清列表、不重载应用列表。
        /// 解析器从设备取最新名称和图标后覆盖写回缓存，并通过事件只回填对应行。
        /// </summary>
        private void reloadPackageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            DeviceFileEntry entry = fileList.SelectedItem as DeviceFileEntry;
            if (entry == null) return;

            string package = _inPackageList ? entry.Package : entry.Name;
            if (string.IsNullOrEmpty(package)) return;

            SetStatus("正在从设备重新获取 “" + package + "” 的应用数据…");

            var thread = new Thread(() =>
            {
                // 缓存命中时 _pendingApks 可能为空；只补取 APK 路径，不动列表数据
                if (_pendingApks == null || !_pendingApks.ContainsKey(package))
                {
                    Dictionary<string, string> paths = _service.ListPackageApkPaths(out _);
                    if (_pendingApks == null) _pendingApks = paths;
                    else foreach (KeyValuePair<string, string> pair in paths) _pendingApks[pair.Key] = pair.Value;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    StartPackageResolver(package);
                }));
            })
            {
                IsBackground = true,
                Name = "refresh-one-package"
            };
            thread.Start();
        }

        private List<DeviceFileEntry> ApplySort(List<DeviceFileEntry> entries)
        {
            var sorted = entries == null ? new List<DeviceFileEntry>() : new List<DeviceFileEntry>(entries);
            if (sorted.Count > 1) sorted.Sort(CompareEntries);
            return sorted;
        }

        /// <summary>文件夹始终排在前面（与资源管理器习惯一致），其余按当前列比较。</summary>
        private int CompareEntries(DeviceFileEntry a, DeviceFileEntry b)
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

            int result = CompareByKey(a, b);
            if (result == 0)
            {
                result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            }
            return _sortAscending ? result : -result;
        }

        private int CompareByKey(DeviceFileEntry a, DeviceFileEntry b)
        {
            switch (_sortKey)
            {
                case "type":
                    return string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase);
                case "size":
                    return a.Size.CompareTo(b.Size);
                case "time":
                    return a.Modified.CompareTo(b.Modified);
                case "permissions":
                    return string.Compare(a.Permissions ?? string.Empty, b.Permissions ?? string.Empty,
                        StringComparison.Ordinal);
                default:
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        /// <summary>列头标题（可能带 ▲/▼）→ 排序键；不参与排序的列返回 null。</summary>
        private static string SortKeyOf(string title)
        {
            if (title == null) return null;

            string text = title.Replace("▲", string.Empty).Replace("▼", string.Empty).Trim();
            switch (text)
            {
                case "名称": return "name";
                case "类型": return "type";
                case "大小": return "size";
                case "修改时间": return "time";
                case "权限": return "permissions";
                default: return null;
            }
        }

        private static string SortTitle(string key)
        {
            switch (key)
            {
                case "type": return "类型";
                case "size": return "大小";
                case "time": return "修改时间";
                case "permissions": return "权限";
                default: return "名称";
            }
        }

        private void UpdateHeaderArrows()
        {
            foreach (KeyValuePair<System.Windows.Controls.GridViewColumn, string> pair in _headerTitles)
            {
                bool active = string.Equals(SortKeyOf(pair.Value), _sortKey, StringComparison.Ordinal);
                pair.Key.Header = active ? pair.Value + (_sortAscending ? " ▲" : " ▼") : pair.Value;
            }
        }

        #endregion

        #region 《辅助》

        private List<DeviceFileEntry> SelectedEntries()
        {
            return fileList.SelectedItems.Cast<DeviceFileEntry>().ToList();
        }

        /// <summary>在列表区域中央显示提示（空目录 / 无权限 / 读取失败）。</summary>
        private void ShowListNotice(string title, string detail, bool isError, bool showBack)
        {
            noticeTitle.Text = title;
            noticeTitle.Foreground = isError ? NoticeErrorBrush : NoticeNormalBrush;
            noticeDetail.Text = detail;
            noticeBackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
            listNotice.Visibility = Visibility.Visible;
        }

        private void HideListNotice()
        {
            listNotice.Visibility = Visibility.Collapsed;
        }

        /// <summary>把服务层返回的原因归纳成一句话标题。</summary>
        private static string MapErrorTitle(string error)
        {
            if (error.IndexOf("run-as", StringComparison.OrdinalIgnoreCase) >= 0) return "应用数据不可访问";
            if (error.IndexOf("权限", StringComparison.Ordinal) >= 0) return "没有访问权限";
            if (error.IndexOf("不存在", StringComparison.Ordinal) >= 0) return "目录不存在或已被删除";
            if (error.IndexOf("超时", StringComparison.Ordinal) >= 0) return "设备响应超时";
            if (error.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0
                && error.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "设备已断开";
            }
            return "读取目录失败";
        }

        /// <summary>错误详情：路径 + 原因 + 针对性建议。</summary>
        private string BuildErrorDetail(string error, string path)
        {
            string hint = string.Empty;
            if (!string.IsNullOrEmpty(_runAsPackage)
                && error.IndexOf("权限", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n当前以应用 " + _runAsPackage + " 的身份访问，只能读写它自己的数据目录；"
                     + "如果是别的应用的目录，请返回应用列表重新选择。";
            }
            else if (_rootShell && error.IndexOf("权限", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n当前 shell 已是 root，仍有个别目录受 SELinux 保护，无法读取。";
            }
            else if (error.IndexOf("权限", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n该目录属于系统或应用私有数据，普通 shell 用户无法访问；"
                     + "可返回上级目录，或改用 /sdcard 等有权限的位置。\r\n"
                     + "调试包（debug 应用）的数据可用工具栏的“应用数据”入口查看。";
            }
            else if (error.IndexOf("不存在", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n目录可能已被移动或删除，可返回上级后重新进入。";
            }

            return "路径：" + path + "\r\n原因：" + error + hint;
        }

        private void noticeBack_Click(object sender, RoutedEventArgs e)
        {
            GoUp();
        }

        private void noticeRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_inPackageList) ShowPackageList(true);
            else Navigate(_currentPath);
        }

        private string PickFolder(string title, string initial)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                {
                    dialog.SelectedPath = initial;
                }
                return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        /// <summary>
        /// 应用列表里没有真实设备路径，文件类操作都不适用，直接禁用对应按钮；
        /// 进入任何真实目录后恢复。
        /// </summary>
        private void SetFileOpsEnabled(bool enabled)
        {
            newFolderButton.IsEnabled = enabled;
            uploadFileButton.IsEnabled = enabled;
            uploadFolderButton.IsEnabled = enabled;
            downloadButton.IsEnabled = enabled;
            renameButton.IsEnabled = enabled;
            deleteButton.IsEnabled = enabled;
        }

        private void SetBusyUi(bool busy, string text)
        {
            transferBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (text != null) SetStatus(text);
        }

        private void SetStatus(string text)
        {
            if (_closed) return;
            statusText.Text = text;
        }

        /// <summary>状态栏常驻信息：共 N 项，有选中时追加已选数量。</summary>
        private void UpdateCountStatus()
        {
            if (_closed) return;
            int selected = fileList.SelectedItems == null ? 0 : fileList.SelectedItems.Count;

            string text;
            if (_inPackageList && !string.IsNullOrEmpty(_packageStatusBase))
            {
                text = _packageStatusBase;
            }
            else
            {
                text = "共 " + _itemCount + " 项";
            }

            if (selected > 0) text += " · 已选 " + selected + " 项";
            statusText.Text = text;
        }

        private void fileList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_closed) return;
            UpdateCountStatus();
        }

        /// <summary>传输过程中频繁刷新状态文本，避免跨线程操作控件。</summary>
        private void UpdateStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(text)));
        }

        private static string ShortName(TransferItem item)
        {
            try
            {
                return Path.GetFileName(item.LocalPath.TrimEnd('\\', '/'));
            }
            catch
            {
                return item.LocalPath;
            }
        }

        /// <summary>一次待传输的条目：上传时本地是源，下载时本地是目标。</summary>
        private sealed class TransferItem
        {
            public bool Upload { get; private set; }
            public string LocalPath { get; private set; }
            public string DevicePath { get; private set; }

            public TransferItem(bool upload, string localPath, string devicePath)
            {
                Upload = upload;
                LocalPath = localPath;
                DevicePath = devicePath;
            }
        }

        #endregion
    }
}
