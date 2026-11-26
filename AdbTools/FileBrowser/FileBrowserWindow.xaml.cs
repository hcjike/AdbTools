using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        /// <summary>解包目录/安装包的后缀：.apk、.apks，以及重名下载产生的 .apk.1 / .apk.2。</summary>
        private static readonly Regex ApkSuffixPattern =
            new Regex(@"\.apk(s)?(\.\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>复制产生的后缀：<c>name (1)</c>、<c>name (2)</c>。</summary>
        private static readonly Regex CopySuffixPattern =
            new Regex(@"\s*\(\d+\)$", RegexOptions.Compiled);

        /// <summary>后台解析应用名的任务；切换目录 / 关闭窗口时停止。</summary>
        private AppLabelResolver _resolver;

        /// <summary>操作结果提示的自动隐藏计时器。</summary>
        private readonly DispatcherTimer _actionTipTimer = new DispatcherTimer();

        /// <summary>拖拽提示的兜底巡检：DragLeave 漏发时靠它把提示收掉。</summary>
        private readonly DragHintWatchdog _dropHintWatch;

        /// <summary>当前列表里的条目，用于把异步解析出来的应用名、图标回填到对应行。</summary>
        private List<DeviceFileEntry> _visibleEntries = new List<DeviceFileEntry>();

        /// <summary>本次列表里还没解析出应用名的包（后台补齐用）。</summary>
        private List<string> _pendingPackages = new List<string>();

        /// <summary>包名 → APK 路径，供后台解析使用；没有待解析包时为 null。</summary>
        private Dictionary<string, string> _pendingApks;

        private string _currentPath = "/sdcard";
        private string _lastLocalDirectory;
        private bool _busy;
        private bool _dragValid;

        /// <summary>本批次统一使用的冲突处理方式（勾选「为所有冲突执行此操作」后生效）。</summary>
        private ConflictAction? _conflictAll;
        private int _lastProgressTick;
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

        /// <summary>
        /// 最近一次右键点中的条目，null 表示点在列表空白处。
        /// 右键不改动已选中的多选行，所以菜单里的操作对象必须取这份记录，
        /// 不能用 SelectedItem（否则会误操作别的行）。
        /// </summary>
        private DeviceFileEntry _contextEntry;

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

            _actionTipTimer.Interval = TimeSpan.FromSeconds(5);
            _actionTipTimer.Tick += (s, e) =>
            {
                _actionTipTimer.Stop();
                actionTip.Visibility = Visibility.Collapsed;
            };

            _dropHintWatch = new DragHintWatchdog(this,
                () => dropHintBorder.Visibility == Visibility.Visible, HideDropHint);

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
            InitColumnWidths();

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

            if (!Dialogs.Confirm(
                "正在传输文件，确定要取消并关闭窗口吗？", "提示", this,
                "关闭", danger: true, icon: MessageBoxImage.Warning))
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
            _actionTipTimer.Stop();
            _dropHintWatch.Stop();
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
                        ShowListNotice("空目录", "当前目录下没有任何文件或文件夹：\r\n" + target,
                            false, target != "/", true);
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

            OpenEntry(fileList.SelectedItem as DeviceFileEntry);
        }

        /// <summary>右键菜单的「打开」：与双击同一套语义。</summary>
        private void contextOpen_Click(object sender, RoutedEventArgs e)
        {
            OpenEntry(_contextEntry ?? fileList.SelectedItem as DeviceFileEntry);
        }

        /// <summary>
        /// 打开一个条目：应用列表行进入其私有目录，目录进入，文件（含链接）下载。
        /// 双击与右键菜单都走这里，保证两种入口行为一致。
        /// </summary>
        private void OpenEntry(DeviceFileEntry entry)
        {
            if (entry == null || _busy) return;

            // 应用列表里的一行：进入该应用的私有目录
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
            return IsInsideVisual<System.Windows.Controls.Primitives.ScrollBar>(source);
        }

        /// <summary>沿视觉树向上查找是否落在某个控件内部（列头、滚动条等）。</summary>
        private static bool IsInsideVisual<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T) return true;
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

        /// <summary>
        /// 列头与滚动条上不弹右键菜单：这两个位置的右键不属于列表内容，
        /// 菜单里的操作没有明确目标，交给滚动/排序本身更符合预期。
        /// </summary>
        private void fileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;
            if (IsOverScrollBar(source) || IsInsideVisual<GridViewColumnHeader>(source))
            {
                e.Handled = true;
            }
        }

        #endregion

        #region 《上传 / 下载》

        /// <summary>拖入时统计文件夹/文件数量并提示，同时判定是否允许放下。</summary>
        private void fileList_DragEnter(object sender, DragEventArgs e)
        {
            EvaluateDrop(e.Data);
            e.Effects = _dragValid ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>拖拽过程中保持提示与光标一致（DragEnter 未触发时兜底再算一次）。</summary>
        private void fileList_DragOver(object sender, DragEventArgs e)
        {
            if (!_dragValid) EvaluateDrop(e.Data);
            e.Effects = _dragValid ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// 拖拽离开。列表项之间移动也会冒泡 DragLeave，
        /// 所以只有指针真正离开列表范围时才隐藏提示，避免闪烁。
        /// </summary>
        private void fileList_DragLeave(object sender, DragEventArgs e)
        {
            Point p = e.GetPosition(fileList);
            if (p.X >= 0 && p.Y >= 0 && p.X <= fileList.ActualWidth && p.Y <= fileList.ActualHeight)
            {
                return;
            }
            HideDropHint();
        }

        /// <summary>分析拖入的内容：统计文件夹/文件数量，并给出可拖拽与否的提示。</summary>
        private void EvaluateDrop(IDataObject data)
        {
            string[] paths = (data != null && data.GetDataPresent(DataFormats.FileDrop))
                ? data.GetData(DataFormats.FileDrop) as string[]
                : null;

            if (paths == null || paths.Length == 0)
            {
                _dragValid = false;
                ShowDropHint("只能拖入本地文件或文件夹", false);
                return;
            }

            if (_inPackageList)
            {
                _dragValid = false;
                ShowDropHint("应用列表里不能上传，请先进入某个目录", false);
                return;
            }

            if (_busy)
            {
                _dragValid = false;
                ShowDropHint("正在传输，请完成后再拖入", false);
                return;
            }

            int folders = 0;
            int files = 0;
            int unsupported = 0;
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    unsupported++;
                }
                else if (Directory.Exists(path))
                {
                    folders++;
                }
                else if (File.Exists(path))
                {
                    files++;
                }
                else
                {
                    unsupported++;
                }
            }

            if (folders == 0 && files == 0)
            {
                _dragValid = false;
                ShowDropHint("未检测到可上传的文件或文件夹", false);
                return;
            }

            _dragValid = true;

            string text;
            if (folders > 0 && files > 0)
            {
                text = "松手上传 " + folders + " 个文件夹、" + files + " 个文件";
            }
            else if (folders > 0)
            {
                text = "松手上传 " + folders + " 个文件夹";
            }
            else
            {
                text = "松手上传 " + files + " 个文件";
            }

            if (unsupported > 0)
            {
                text += "（" + unsupported + " 项不受支持，将忽略）";
            }

            ShowDropHint(text, true);
        }

        private void ShowDropHint(string text, bool valid)
        {
            dropHintText.Text = text;
            dropHintText.Foreground = valid ? Brushes.LightGreen : Brushes.LightCoral;
            dropHintBorder.Visibility = Visibility.Visible;

            // 提示显示期间巡检鼠标状态，防止漏发的 DragLeave 让提示一直挂着
            _dropHintWatch.Start();
        }

        private void HideDropHint()
        {
            _dropHintWatch.Stop();
            dropHintBorder.Visibility = Visibility.Collapsed;
            _dragValid = false;
        }

        private void fileList_Drop(object sender, DragEventArgs e)
        {
            HideDropHint();

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
            List<DeviceFileEntry> selected = SelectedEntries();
            if (selected.Count == 0)
            {
                SetStatus(_inPackageList
                    ? "请先选择要下载的应用（可多选）"
                    : "请先选择要下载的文件或文件夹");
                return;
            }

            // 应用列表里的行没有真实路径：整批按「应用私有数据」处理
            if (_inPackageList || selected.All(o => o.Package != null))
            {
                DownloadAppData(selected);
                return;
            }

            DownloadEntries(selected);
        }

        /// <summary>
        /// 下载应用私有数据：每个应用对应一个本地目录（目标/包名），逐包串行取回。
        /// run-as 身份是单值，同一时刻只能以一个应用的身份读；root 设备则由普通 shell 直接读。
        /// </summary>
        private void DownloadAppData(List<DeviceFileEntry> apps)
        {
            if (_busy) return;

            List<DeviceFileEntry> openable = apps
                .Where(o => o.CanOpen && !string.IsNullOrEmpty(o.Package))
                .ToList();
            int skipped = apps.Count - openable.Count;

            if (openable.Count == 0)
            {
                SetStatus(_rootShell
                    ? "没有可下载的应用"
                    : "选中的应用都不是 debug 包，无法以应用身份读取私有数据");
                return;
            }

            string target = PickFolder("选择把应用数据下载到哪个文件夹", _lastLocalDirectory);
            if (target == null) return;
            _lastLocalDirectory = target;

            // 私有目录是逐文件写入的，本地同名文件会被静默覆盖，开始前统一确认一次
            if (openable.Exists(o => Directory.Exists(Path.Combine(target, o.Package))))
            {
                if (!Dialogs.Confirm(
                    "目标文件夹里已存在同名应用目录，继续将合并写入并覆盖同名文件。\r\n\r\n确定继续吗？",
                    "下载确认", this, "覆盖", danger: true, icon: MessageBoxImage.Warning)) return;
            }

            var items = new List<TransferItem>();
            foreach (DeviceFileEntry entry in openable)
            {
                // root 设备用普通 shell 直接读，不必切换 run-as 身份；
                // 整目录 pull 交给 adb，避免为每个小文件单独起一个 adb 进程
                items.Add(_rootShell
                    ? new TransferItem(false, Path.Combine(target, entry.Package),
                        "/data/data/" + entry.Package, null, true)
                    : new TransferItem(false, Path.Combine(target, entry.Package),
                        "/data/data/" + entry.Package, entry.Package));
            }

            RunTransfer("下载", items,
                skipped > 0 ? "已跳过 " + skipped + " 个不可访问的应用" : null);
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

        private void RunTransfer(string action, List<TransferItem> items, string note = null)
        {
            if (items == null || items.Count == 0) return;

            _busy = true;
            _cancelRequested = false;
            _conflictAll = null;
            // 新一批不沿用上一批的节流时间戳；往回拨 1 秒保证首条进度不被节流掉
            // （TickCount 约 25 天回绕一次，这里用 unchecked 让差值计算照常成立）
            _lastProgressTick = unchecked(Environment.TickCount - 1000);
            SetBusyUi(true, action + "中");
            SetProgressBusy(true);

            // 每个条目整树传输（run-as 身份或 root 下的整目录 pull），不做「先扫描再逐文件」
            bool wholeEntry = _service.IsRunAsMode
                || items.Exists(o => o.RunAsPackage != null || o.TreeTransfer);

            var thread = new Thread(() =>
            {
                var stats = new TransferStats();
                var failed = new List<string>();

                // 批内进度前缀：run-as 下载时逐应用刷新，状态栏同时带上「第几个应用」
                string progressPrefix = action + "中";
                _service.Progress = text => UpdateStatus(progressPrefix + " · " + text);

                // 取消标志与目录缓存统一在这里重置。Transfer 内部不再重置，
                // 否则逐个文件/逐个应用调用时会把用户刚发出的取消请求抹掉。
                _service.BeginBatch();

                if (!wholeEntry)
                {
                    _service.ResolveConflict = info =>
                    {
                        try
                        {
                            return Dispatcher.Invoke(new Func<ConflictAction>(() => AskConflict(info)));
                        }
                        catch (Exception)
                        {
                            // 窗口已关闭或调度器已停止：按跳过处理，不要让后台线程崩掉
                            return ConflictAction.Skip;
                        }
                    };
                }

                try
                {
                    if (wholeEntry)
                    {
                        stats.Total = items.Count;
                        UpdateProgressTotal(stats.Total);

                        foreach (TransferItem item in items)
                        {
                            if (_cancelRequested) break;

                            // 每个应用一个身份：run-as 只能在自己的数据目录内活动
                            if (item.RunAsPackage != null) _service.RunAsPackage = item.RunAsPackage;

                            string label = ShortName(item);
                            progressPrefix = action + "中 · " + label
                                + " (" + (stats.Done + 1) + "/" + stats.Total + ")";
                            UpdateStatus(progressPrefix);

                            string message;
                            if (!_service.Transfer(item.Upload, item.LocalPath, item.DevicePath, out message))
                            {
                                if (_cancelRequested) break;
                                failed.Add(label + "：" + message);
                            }

                            if (_cancelRequested) break;

                            stats.Done++;
                            stats.Failed = failed.Count;
                            ReportProgress(action, label, stats);
                        }
                    }
                    else
                    {
                        // 阶段1：扫描出全部条目（文件 + 子文件夹），进度按条目总数统计。
                        // 文件夹不论是否已存在都算一项（总数与进度保持不变），
                        // 只是「本来就有」的那些在汇总里不算「新建」。
                        UpdateStatus(action + "前统计文件…");
                        var entries = new List<TransferEntry>();
                        foreach (TransferItem item in items)
                        {
                            if (_cancelRequested) break;

                            string scanError;
                            List<TransferEntry> part = _service.Scan(
                                item.Upload, item.LocalPath, item.DevicePath, out scanError);

                            if (!string.IsNullOrEmpty(scanError)) failed.Add(ShortName(item) + "：" + scanError);
                            entries.AddRange(part);
                        }

                        stats.Total = entries.Count;
                        stats.TotalFiles = entries.Count(o => !o.IsDirectory);
                        stats.TotalDirs = stats.Total - stats.TotalFiles;
                        stats.Failed = failed.Count;
                        UpdateProgressTotal(stats.Total);

                        // 阶段2：逐个条目传，文件前判断冲突
                        foreach (TransferEntry entry in entries)
                        {
                            if (_cancelRequested) break;

                            string entryName = Path.GetFileName(entry.LocalPath);

                            string message;
                            TransferOutcome outcome = _service.TransferFile(entry, out message);

                            // 文件夹只建目录：成功或失败都算已处理，失败项单独计数
                            if (entry.IsDirectory)
                            {
                                if (_cancelRequested) break;

                                if (outcome == TransferOutcome.Failed)
                                {
                                    failed.Add(entryName + "：" + message);
                                    stats.Failed = failed.Count;
                                }
                                else if (entry.TargetExisted)
                                {
                                    // 目标处本来就有这个文件夹：算一项已处理，但不算「新建」
                                    stats.ExistedDirectories++;
                                }
                                else
                                {
                                    stats.AddedDirectories++;
                                }

                                stats.Done++;
                                stats.DoneDirs++;
                                ReportProgress(action, entryName, stats);
                                continue;
                            }

                            // 用户点了取消（弹窗里的「取消」或状态栏「取消传输」）：
                            // 这一项不算跳过，直接中止整批
                            if (_cancelRequested) break;

                            stats.Done++;
                            stats.DoneFiles++;
                            if (outcome == TransferOutcome.Skipped) stats.Skipped++;
                            else if (outcome == TransferOutcome.Overwritten) stats.Overwritten++;
                            else if (outcome == TransferOutcome.KeptBoth) stats.KeptBoth++;
                            else if (outcome == TransferOutcome.Success) stats.Added++;
                            else if (outcome == TransferOutcome.Failed)
                            {
                                if (_cancelRequested) break;
                                failed.Add(entryName + "：" + message);
                            }

                            stats.Failed = failed.Count;
                            ReportProgress(action, entryName, stats);
                        }
                    }
                }
                finally
                {
                    _service.Progress = null;
                    _service.ResolveConflict = null;
                    // 批量下载应用数据期间临时切过身份，结束后回到当前浏览状态
                    if (items.Exists(o => o.RunAsPackage != null)) _service.RunAsPackage = _runAsPackage;
                }

                bool cancelled = _cancelRequested;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;
                    _cancelRequested = false;
                    _conflictAll = null;
                    SetBusyUi(false, null);
                    SetProgressBusy(false);

                    // 汇总把文件和文件夹分开给进度：两类单位不同，混在一起时
                    // 「151/161」与「新建 86」看起来会对不上
                    string progress;
                    if (stats.TotalFiles + stats.TotalDirs > 0)
                    {
                        var parts = new List<string>();
                        if (stats.TotalFiles > 0)
                            parts.Add("文件 " + stats.DoneFiles + "/" + stats.TotalFiles);
                        if (stats.TotalDirs > 0)
                            parts.Add("文件夹 " + stats.DoneDirs + "/" + stats.TotalDirs);
                        progress = string.Join("，", parts);
                    }
                    else
                    {
                        // 应用私有数据整目录传输：按应用 / 顶层条目计数
                        progress = stats.Total > 0 ? stats.Done + "/" + stats.Total : "无可传项目";
                    }

                    string summary = (cancelled ? action + "已取消" : action + "完成")
                        + "（" + progress + "）";
                    if (!string.IsNullOrEmpty(note)) summary += "，" + note;

                    // 文件用「新增」，文件夹用「新建」：目标处本来就有文件夹时只提示已存在，
                    // 不能再把它说成「新建」，否则和总数里的文件夹数对不上。
                    var added = new List<string>();
                    if (stats.Added > 0) added.Add("新增 " + stats.Added + " 个文件");
                    if (stats.AddedDirectories > 0) added.Add("新建 " + stats.AddedDirectories + " 个文件夹");
                    if (added.Count > 0) summary += "，" + string.Join("、", added);
                    if (stats.ExistedDirectories > 0)
                        summary += "，" + stats.ExistedDirectories + " 个文件夹已存在";
                    if (stats.Overwritten > 0) summary += "，已覆盖 " + stats.Overwritten + " 个文件";
                    if (stats.KeptBoth > 0) summary += "，已重命名 " + stats.KeptBoth + " 个文件";
                    if (stats.Skipped > 0) summary += "，已跳过 " + stats.Skipped + " 个文件";
                    if (stats.Failed > 0) summary += "，失败 " + stats.Failed + " 项";
                    SetStatus(summary);

                    if (failed.Count > 0)
                    {
                        Dialogs.Show(
                            "以下项目" + action + "失败：\r\n\r\n" + string.Join("\r\n", failed.Take(10)),
                            "提示", MessageBoxImage.Warning, this);
                    }
                    else
                    {
                        ShowActionTip(summary, true);
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

        /// <summary>冲突询问（UI 线程）。本批已选择「全部应用」时不再弹窗。</summary>
        private ConflictAction AskConflict(ConflictInfo info)
        {
            if (_closed) return ConflictAction.Skip;
            if (_conflictAll.HasValue) return _conflictAll.Value;

            ConflictDialog dialog = new ConflictDialog(info) { Owner = this };
            bool? result = dialog.ShowDialog();

            if (_closed) return ConflictAction.Skip;

            if (dialog.Cancelled || result != true)
            {
                _cancelRequested = true;
                _service.Cancel();
                return ConflictAction.Skip;
            }

            if (dialog.ApplyToAll) _conflictAll = dialog.Action;
            return dialog.Action;
        }

        /// <summary>进度条切到不确定态（扫描、run-as 整目录传输时用）。</summary>
        private void SetProgressBusy(bool busy)
        {
            transferBar.IsIndeterminate = busy;

            if (busy)
            {
                transferBar.Maximum = 1;
                transferBar.Value = 0;
                transferProgressText.Text = "统计中…";

                // 不确定态用整体透明度呼吸，比在模板里写 Storyboard 更稳
                transferBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.5,
                    TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }
            else
            {
                StopProgressPulse();
            }
        }

        /// <summary>结束呼吸动画并恢复正常不透明度。</summary>
        private void StopProgressPulse()
        {
            transferBar.BeginAnimation(OpacityProperty, null);
            transferBar.Opacity = 1;
        }

        private void UpdateProgressTotal(int total)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    transferBar.IsIndeterminate = total <= 0;
                    transferBar.Maximum = total <= 0 ? 1 : total;
                    transferBar.Value = 0;
                    transferProgressText.Text = total <= 0 ? "无可传项目" : "0/" + total;
                    if (total > 0) StopProgressPulse();
                }));
            }
            catch (Exception)
            {
                // 窗口已关闭
            }
        }

        /// <summary>刷新「已完成/总数」进度；按 80ms 节流，避免大量小文件刷爆 UI。</summary>
        private void ReportProgress(string action, string name, TransferStats stats)
        {
            int now = Environment.TickCount;
            if (stats.Done < stats.Total && now - _lastProgressTick < 80) return;
            _lastProgressTick = now;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    transferBar.IsIndeterminate = false;
                    transferBar.Maximum = stats.Total <= 0 ? 1 : stats.Total;
                    transferBar.Value = Math.Min(stats.Done, stats.Total);
                    transferProgressText.Text = stats.Done + "/" + stats.Total;

                    // 过程中只给基础提示：当前项目 + 已完成/总数。
                    // 新增/覆盖/跳过这类分类数量逐项跳变没意义，等整批结束时在汇总里一次性给出。
                    string text = action + "中";
                    if (!string.IsNullOrEmpty(name)) text += " · " + name;
                    if (stats.Total > 0) text += "    " + stats.Done + "/" + stats.Total;
                    SetStatus(text);
                }));
            }
            catch (Exception)
            {
                // 窗口已关闭
            }
        }

        /// <summary>一批传输的计数：Done 为已处理条目数（含文件夹），其余是其中各类结果的数量。</summary>
        private struct TransferStats
        {
            /// <summary>已处理的条目数（文件 + 文件夹，含覆盖/重命名/跳过/失败）。</summary>
            public int Done;

            /// <summary>本批条目总数（文件 + 文件夹）。</summary>
            public int Total;

            /// <summary>扫描出来的文件数 / 文件夹数，用于把汇总拆成两类各自的进度。</summary>
            public int TotalFiles;
            public int TotalDirs;

            /// <summary>已处理的文件数 / 文件夹数。</summary>
            public int DoneFiles;
            public int DoneDirs;

            /// <summary>文件里目标原本没有同名项、直接写入的数量。</summary>
            public int Added;

            /// <summary>真正新建的文件夹数量（目标处已存在的已剔除，与文件的新增分开统计）。</summary>
            public int AddedDirectories;

            /// <summary>目标处本来就有、未重复创建的文件夹数量（仍计入总数与进度）。</summary>
            public int ExistedDirectories;

            public int Overwritten;
            public int KeptBoth;
            public int Skipped;

            /// <summary>失败项数（文件 + 文件夹）。</summary>
            public int Failed;
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

            var input = new InputWindow
            {
                Title = "新建文件夹",
                Owner = this,
                Hint = "在 " + _currentPath + " 下新建文件夹",
                HintHighlight = _currentPath
            };
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

            var input = new InputWindow
            {
                Title = "重命名",
                Owner = this,
                Hint = "原名：" + entry.Name,
                HintHighlight = entry.Name
            };
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

            if (!Dialogs.Confirm(
                "确定要删除以下项目吗？\r\n\r\n" + names + "\r\n\r\n" + warning,
                "删除确认", this, "删除", danger: true, icon: MessageBoxImage.Warning)) return;

            RunOperation("删除",
                progress =>
                {
                    int done = 0;
                    foreach (DeviceFileEntry entry in selected)
                    {
                        if (_cancelRequested) break;

                        string error;
                        if (!_service.Delete(entry.FullPath, out error))
                        {
                            return entry.Name + "：" + error;
                        }

                        done++;
                        progress(done);
                    }
                    return null;
                },
                selected.Count,
                null);
        }

        private void RunOperation(string action, Func<string> operation, Action onSuccess)
        {
            RunOperation(action, progress => operation(), 0, onSuccess);
        }

        /// <summary>
        /// 执行一次文件操作。<paramref name="total"/> 大于 0 时显示逐项进度，
        /// 操作内部通过 progress 回调上报已完成数量（回调内部会切回 UI 线程）。
        /// </summary>
        private void RunOperation(string action, Func<Action<int>, string> operation,
            int total, Action onSuccess)
        {
            if (_busy) return;

            _busy = true;
            _cancelRequested = false;
            SetBusyUi(true, action + "中…");

            if (total > 0)
            {
                UpdateProgressTotal(total);
            }
            else
            {
                // 没有可统计的条目：清掉上一次残留的数值，避免显示为已完成
                transferBar.IsIndeterminate = true;
                transferBar.Maximum = 1;
                transferBar.Value = 0;
                transferProgressText.Text = string.Empty;
            }

            var thread = new Thread(() =>
            {
                string error = operation(done => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    transferBar.IsIndeterminate = false;
                    transferBar.Maximum = total;
                    transferBar.Value = Math.Min(done, total);
                    transferProgressText.Text = done + "/" + total;
                    SetStatus(action + "中 · " + done + "/" + total);
                })));

                bool cancelled = _cancelRequested && total > 0;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _busy = false;
                    _cancelRequested = false;
                    SetBusyUi(false, null);

                    if (cancelled)
                    {
                        SetStatus(action + "已取消");
                        ShowActionTip(action + "已取消", false);
                        Navigate(_currentPath);
                        return;
                    }

                    if (!string.IsNullOrEmpty(error))
                    {
                        SetStatus(action + "失败：" + error);
                        Dialogs.Show(action + "失败：\r\n" + error, "提示",
                            MessageBoxImage.Warning, this);
                        return;
                    }

                    ShowActionTip(action + "成功", true);
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

            // 应用列表不是真实目录：上传、新建、重命名、删除都不可用，
            // 但可以多选应用，批量下载它们各自的私有数据目录
            SetFileOpsEnabled(false);
            downloadButton.IsEnabled = true;

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

            // 只有文件夹参与：目录名可能是包名（/data/data、/sdcard/Android/data 等）
            var candidates = new List<DeviceFileEntry>();
            foreach (DeviceFileEntry entry in entries)
            {
                entry.PackageHint = GuessPackageName(entry);
                if (entry.PackageHint != null) candidates.Add(entry);
            }
            if (candidates.Count == 0) return;

            List<string> names = candidates
                .Select(o => o.PackageHint)
                .Distinct(StringComparer.Ordinal)
                .ToList();

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

            // 多个目录可能推断出同一个包名（如 com.foo 与 com.foo (1)），待办按包名去重
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceFileEntry entry in candidates)
            {
                string label;
                if (labels.TryGetValue(entry.PackageHint, out label) && !string.IsNullOrEmpty(label))
                {
                    entry.AppLabel = label;
                }

                ImageSource icon;
                if (icons.TryGetValue(entry.PackageHint, out icon) && icon != null)
                {
                    entry.IconOverride = icon;
                }

                // 已知"确定没有名字"的不再重试，避免每次进目录都拉一遍 APK
                if (string.IsNullOrEmpty(entry.AppLabel) && !resolvedEmpty.Contains(entry.PackageHint))
                {
                    pending.Add(entry.PackageHint);
                }
            }

            if (pending.Count == 0) return;

            _pendingPackages = pending.ToList();
            _pendingApks = _service.ListPackageApkPaths(out _);
        }

        /// <summary>
        /// 后台解析应用名与图标（复用应用列表的 AppLabelResolver，结果同样写入缓存库）。
        /// 解析完成的回调在 UI 线程，直接回填到对应行。
        /// </summary>
        /// <param name="forcePackages">
        /// 非空表示只强制解析这几个包（右键「解析应用」，支持多选）；
        /// 解析器把从设备取到的新名称/图标直接覆盖写回缓存，不删除旧数据。
        /// </param>
        private void StartPackageResolver(IList<string> forcePackages = null)
        {
            StopPackageResolver();
            if (_pendingApks == null || _pendingApks.Count == 0) return;

            // 平时只解析缓存未命中的包；右键「解析应用」时只强制解析选中的那几个包
            bool force = forcePackages != null && forcePackages.Count > 0;
            List<string> names = force ? new List<string>(forcePackages) : _pendingPackages;
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
                message => { }, 4, force);

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
                // 目录/APK 的条目名不一定等于包名，按推断出来的 PackageHint 匹配
                if (string.Equals(entry.PackageHint ?? entry.Name, package, StringComparison.Ordinal))
                {
                    entry.AppLabel = label;
                }
            }
        }

        private void ApplyIconToEntries(string package, ImageSource icon)
        {
            foreach (DeviceFileEntry entry in AllKnownEntries())
            {
                if (string.Equals(entry.PackageHint ?? entry.Name, package, StringComparison.Ordinal))
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
            if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
            if (!PackageNamePattern.IsMatch(name)) return false;

            // 末段必须含字母：排除 v1.2.3、2024.01.02 这类版本号/日期式文件名
            int index = name.LastIndexOf('.');
            for (int i = index + 1; i < name.Length; i++)
            {
                char c = name[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_') return true;
            }
            return false;
        }

        /// <summary>
        /// 从目录名推断包名：先剥掉 .apk、-1、 (1) 这类下载/复制后缀，再按包名规则匹配。
        /// 只处理文件夹——文件不参与识别：安装包未必已安装，靠读 APK 解析会产生大量设备交互，
        /// 安装包的解析交给安装流程。
        /// </summary>
        private static string GuessPackageName(DeviceFileEntry entry)
        {
            if (entry == null || !entry.IsDirectory || string.IsNullOrEmpty(entry.Name)) return null;

            string name = ApkSuffixPattern.Replace(entry.Name, string.Empty);
            return MatchPackageName(name);
        }

        /// <summary>剥掉复制/下载后缀后匹配包名，取能匹配的最长前缀。</summary>
        private static string MatchPackageName(string name)
        {
            if (IsPackageName(name)) return name;

            string trimmed = CopySuffixPattern.Replace(name, string.Empty);
            if (!string.Equals(trimmed, name, StringComparison.Ordinal) && IsPackageName(trimmed)) return trimmed;

            // com.foo.bar-1 / com.foo.bar-copy：'-' 不是合法包名字符，逐段向左剥
            int dash = trimmed.LastIndexOf('-');
            while (dash > 0)
            {
                string head = trimmed.Substring(0, dash);
                if (IsPackageName(head)) return head;
                dash = head.LastIndexOf('-');
            }

            return null;
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

        #region 《列宽按比例锁定》

        /// <summary>
        /// 各列宽度占比：名称、类型、大小、修改时间、权限，总和为 1。
        /// 窗口缩放时按当前可用宽度重新分配；列头拖动已在样式里去掉，
        /// 所以列宽始终由这组比例决定，不会出现各列被拖乱的情况。
        /// </summary>
        private static readonly double[] ColumnWidthRatios = { 0.45, 0.10, 0.12, 0.19, 0.14 };

        private System.Windows.Controls.GridView _fileGridView;
        private ScrollViewer _listScrollViewer;
        private bool _columnWidthsUpdating;

        private void InitColumnWidths()
        {
            _fileGridView = fileList.View as System.Windows.Controls.GridView;
            if (_fileGridView == null) return;

            fileList.SizeChanged += FileList_SizeChanged;
            fileList.Loaded += FileList_Loaded;
            UpdateColumnWidths();
        }

        private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateColumnWidths();
        }

        /// <summary>
        /// 拿到列表内部的 ScrollViewer。只监听 ListView 的 SizeChanged 是不够的：
        /// 换到文件多的目录时竖向滚动条出现，视口宽度会变小，而 ListView 自身尺寸没变。
        /// </summary>
        private void FileList_Loaded(object sender, RoutedEventArgs e)
        {
            _listScrollViewer = FindVisualChild<ScrollViewer>(fileList);
            if (_listScrollViewer != null)
            {
                _listScrollViewer.ScrollChanged += ListScrollViewer_ScrollChanged;
            }

            UpdateColumnWidths();
        }

        private void ListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 只有视口宽度变化（滚动条出现/消失）才需要重算列宽
            if (e.ViewportWidthChange != 0) UpdateColumnWidths();
        }

        /// <summary>按当前视口宽度重算各列宽度，取整后始终小于视口，避免横向滚动条。</summary>
        private void UpdateColumnWidths()
        {
            if (_columnWidthsUpdating || _fileGridView == null) return;

            // 优先用滚动视口宽度（已扣除竖向滚动条和边框），拿不到时退回控件宽度
            double available = _listScrollViewer != null && _listScrollViewer.ViewportWidth > 0
                ? _listScrollViewer.ViewportWidth
                : fileList.ActualWidth - 20;

            // 再留 1px 余量：列宽总和与视口相等时，浮点舍入也会判成“略宽”而冒出横向滚动条
            available -= 1;
            if (available <= 0) return;

            _columnWidthsUpdating = true;
            try
            {
                int count = Math.Min(ColumnWidthRatios.Length, _fileGridView.Columns.Count);
                for (int i = 0; i < count; i++)
                {
                    _fileGridView.Columns[i].Width =
                        Math.Max(48, Math.Floor(available * ColumnWidthRatios[i]));
                }
            }
            finally
            {
                _columnWidthsUpdating = false;
            }
        }

        /// <summary>在视觉树里查找第一个指定类型的子元素（用于取 ListView 内部的 ScrollViewer）。</summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                T typed = child as T;
                if (typed != null) return typed;

                T nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        #endregion

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
        /// 右键按下时修正选中状态，并记录操作目标。
        /// 与资源管理器一致：右键未选中的行 → 只选中它；右键已选中的行 → 保持多选不动；
        /// 右键空白 → 清空选择。菜单里的操作对象始终取 <see cref="_contextEntry"/>。
        /// </summary>
        private void fileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;

            // 列头与滚动条上右键不是针对列表内容：不动选择，也不弹菜单
            if (IsOverScrollBar(source) || IsInsideVisual<GridViewColumnHeader>(source)) return;

            ListViewItem item = source == null
                ? null
                : fileList.ContainerFromElement(source) as ListViewItem;

            if (item == null)
            {
                fileList.UnselectAll();
            }
            else if (!item.IsSelected)
            {
                // 右键未选中的行：改为单选这一行，避免菜单操作到别的行
                // （Extended 模式下设置 SelectedItem 会清空其它选择）
                fileList.SelectedItem = item.DataContext;
            }

            _contextEntry = item == null ? null : item.DataContext as DeviceFileEntry;
        }

        /// <summary>
        /// 右键菜单弹出时按当前选择与所在模式刷新每一项的可见性与可用性。
        /// 应用列表没有真实路径，只保留打开/下载应用数据/解析应用/筛选/刷新。
        /// </summary>
        private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            DeviceFileEntry target = _contextEntry;
            int selectedCount = fileList.SelectedItems == null ? 0 : fileList.SelectedItems.Count;

            bool hasTarget = target != null;
            bool idle = !_busy;
            bool singleSelection = selectedCount == 1;
            bool isAppRow = hasTarget && !string.IsNullOrEmpty(target.Package);
            bool fileMode = !_inPackageList;

            // 打开：目录进入、文件下载、应用行进入其数据目录
            ctxOpenMenuItem.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
            ctxOpenMenuItem.IsEnabled = idle;

            // 下载：应用列表里即「下载选中应用的数据」，可多选；
            // 多选里只要有一个可访问的应用就能下载（其余会被跳过并提示）
            bool downloadable = !isAppRow
                || fileList.SelectedItems.Cast<DeviceFileEntry>().Any(o => o.CanOpen);
            ctxDownloadMenuItem.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
            ctxDownloadMenuItem.IsEnabled = idle && downloadable;
            if (isAppRow)
            {
                ctxDownloadMenuItem.Header = selectedCount > 1
                    ? "下载应用数据（" + selectedCount + " 项）"
                    : "下载应用数据";
            }
            else
            {
                ctxDownloadMenuItem.Header = "下载到电脑";
            }

            // 当前文件夹组：上传与新建落到当前目录，与选中了哪些项无关
            ctxSepFileOps.Visibility = fileMode && hasTarget ? Visibility.Visible : Visibility.Collapsed;
            ctxFolderGroupMenuItem.Visibility = fileMode ? Visibility.Visible : Visibility.Collapsed;
            ctxFolderGroupText.Text = "当前文件夹 · " + DirectoryLabel(_currentPath);
            ctxFolderGroupMenuItem.ToolTip = _currentPath;

            Visibility uploadVisibility = fileMode ? Visibility.Visible : Visibility.Collapsed;
            ctxUploadFileMenuItem.Visibility = uploadVisibility;
            ctxUploadFolderMenuItem.Visibility = uploadVisibility;
            ctxNewFolderMenuItem.Visibility = uploadVisibility;
            ctxUploadFileMenuItem.IsEnabled = idle;
            ctxUploadFolderMenuItem.IsEnabled = idle;
            ctxNewFolderMenuItem.IsEnabled = idle;

            // 选中项组：重命名只支持单选，删除支持多选
            bool selectionGroup = fileMode && hasTarget;
            ctxSepEdit.Visibility = selectionGroup ? Visibility.Visible : Visibility.Collapsed;
            ctxSelectionGroupMenuItem.Visibility = selectionGroup ? Visibility.Visible : Visibility.Collapsed;
            ctxRenameMenuItem.Visibility = uploadVisibility;
            ctxDeleteMenuItem.Visibility = uploadVisibility;
            ctxRenameMenuItem.IsEnabled = idle && singleSelection;
            ctxDeleteMenuItem.IsEnabled = idle;

            if (selectionGroup)
            {
                ctxSelectionGroupText.Text = selectedCount > 1
                    ? "选中项 · " + selectedCount + " 项"
                    : "选中项 · " + target.Name;
                ctxSelectionGroupMenuItem.ToolTip = selectedCount > 1
                    ? "已选 " + selectedCount + " 项"
                    : target.FullPath;
            }

            ctxSepPackage.Visibility = fileMode || hasTarget ? Visibility.Visible : Visibility.Collapsed;

            // 解析应用：选中项里只要有一个应用行/包名目录就可用，执行时只处理这些项
            int resolvableCount = ResolvablePackages().Count;
            ctxResolveAppMenuItem.Visibility = resolvableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ctxResolveAppMenuItem.IsEnabled = idle;
            ctxResolveAppMenuItem.Header = resolvableCount > 1
                ? "解析应用（" + resolvableCount + " 个）"
                : "解析应用";

            // 应用列表里存在不可访问的应用时才有筛选的意义
            bool canFilter = _inPackageList && _allPackageEntries.Exists(o => !o.CanOpen);
            ctxFilterMenuItem.Visibility = canFilter ? Visibility.Visible : Visibility.Collapsed;
            ctxFilterMenuItem.IsEnabled = idle;
            ctxFilterMenuItem.Header = _showAllPackages ? "只看可访问" : "显示全部";

            ctxRefreshMenuItem.IsEnabled = idle;
        }

        /// <summary>取设备路径的末级名称，用于菜单里说明操作目标；根目录显示 “/”。</summary>
        private static string DirectoryLabel(string path)
        {
            string value = DeviceFileService.Normalize(path);
            if (value == "/") return "/";

            int index = value.LastIndexOf('/');
            return index >= 0 ? value.Substring(index + 1) : value;
        }

        /// <summary>
        /// 重新解析选中的应用（支持多选）：不重列目录、不清列表、不重载应用列表。
        /// 选中项里只有应用行和包名目录参与解析，其余条目直接忽略。
        /// </summary>
        private void resolveAppMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            List<string> packages = ResolvablePackages();
            if (packages.Count == 0)
            {
                SetStatus("选中的项目里没有可解析的应用");
                return;
            }

            SetStatus("正在从设备重新获取 " + packages.Count + " 个应用的应用名与图标…");

            var thread = new Thread(() =>
            {
                // 缓存命中时 _pendingApks 可能为空；只补取 APK 路径，不动列表数据
                bool missing = false;
                foreach (string package in packages)
                {
                    if (_pendingApks == null || !_pendingApks.ContainsKey(package))
                    {
                        missing = true;
                        break;
                    }
                }

                Dictionary<string, string> paths = missing ? _service.ListPackageApkPaths(out _) : null;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    if (paths != null)
                    {
                        // APK 路径表在 UI 线程合并，避免与后台解析同时读写
                        if (_pendingApks == null) _pendingApks = paths;
                        else foreach (KeyValuePair<string, string> pair in paths) _pendingApks[pair.Key] = pair.Value;
                    }

                    StartPackageResolver(packages);

                    // 解析器没能启动（拿不到 APK 路径）时给个明确反馈，不要停在“正在获取”
                    if (_resolver == null) SetStatus("无法获取这些应用的信息（设备可能不支持 pm -f 输出）");
                }));
            })
            {
                IsBackground = true,
                Name = "refresh-packages"
            };
            thread.Start();
        }

        /// <summary>
        /// 当前选中项里能解析包名信息的应用：应用列表的应用行，或目录名恰好是包名的文件夹。
        /// 多选时只取满足条件的项，同名包只保留一次。
        /// </summary>
        private List<string> ResolvablePackages()
        {
            var packages = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (DeviceFileEntry entry in SelectedEntries())
            {
                string package = ResolvablePackageOf(entry);
                if (package != null && seen.Add(package)) packages.Add(package);
            }
            return packages;
        }

        private string ResolvablePackageOf(DeviceFileEntry entry)
        {
            if (entry == null) return null;

            // 应用列表里的应用行：包名就在 Package 字段
            if (!string.IsNullOrEmpty(entry.Package)) return entry.Package;

            if (_inPackageList) return null;

            // 目录名推断出来的包名（进入目录时已算好；文件不参与识别）
            return entry.IsDirectory ? entry.PackageHint : null;
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
        /// <param name="showDropHint">
        /// 是否允许直接拖拽上传。只有「空目录」为 true；权限不足、读取失败、应用列表等
        /// 场景必须关掉，否则覆盖层会把不该接收的拖放也接住。
        /// </param>
        private void ShowListNotice(string title, string detail, bool isError, bool showBack,
            bool showDropHint = false)
        {
            noticeTitle.Text = title;
            noticeTitle.Foreground = isError ? NoticeErrorBrush : NoticeNormalBrush;
            noticeDetail.Text = detail;
            noticeDropHint.Visibility = showDropHint ? Visibility.Visible : Visibility.Collapsed;
            listNotice.AllowDrop = showDropHint;
            noticeBackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
            listNotice.Visibility = Visibility.Visible;
        }

        private void HideListNotice()
        {
            listNotice.Visibility = Visibility.Collapsed;
            listNotice.AllowDrop = false;
            HideDropHint();
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
            transferProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) transferProgressText.Text = string.Empty;
            if (text != null) SetStatus(text);
        }

        private void SetStatus(string text)
        {
            if (_closed) return;
            statusText.Text = text;
        }

        /// <summary>在列表下方显示操作结果提示，5 秒后自动隐藏；新的提示覆盖旧的。</summary>
        private void ShowActionTip(string text, bool success)
        {
            if (_closed || string.IsNullOrWhiteSpace(text)) return;

            actionTipText.Text = text;
            actionTipText.Foreground = success
                ? new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC))
                : new SolidColorBrush(Color.FromRgb(0xFE, 0xCA, 0xCA));
            actionTip.Visibility = Visibility.Visible;

            _actionTipTimer.Stop();
            _actionTipTimer.Start();
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
            return ShortName(item.LocalPath);
        }

        private static string ShortName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFileName(path.TrimEnd('\\', '/'));
            }
            catch
            {
                return path;
            }
        }

        /// <summary>一次待传输的条目：上传时本地是源，下载时本地是目标。</summary>
        private sealed class TransferItem
        {
            public bool Upload { get; private set; }
            public string LocalPath { get; private set; }
            public string DevicePath { get; private set; }

            /// <summary>
            /// 这一项要用哪个应用的身份读取；非空表示批量下载应用私有数据，
            /// 传输前会把服务切换到该 run-as 身份。普通传输为 null。
            /// </summary>
            public string RunAsPackage { get; private set; }

            /// <summary>
            /// true 表示让服务自己整目录传输（隐藏内部结构），不走「先扫描再逐文件」的流程。
            /// root 设备批量下载应用数据时用它：目录递归交给 adb pull，避免为每个文件起一个进程。
            /// </summary>
            public bool TreeTransfer { get; private set; }

            public TransferItem(bool upload, string localPath, string devicePath)
                : this(upload, localPath, devicePath, null, false)
            {
            }

            public TransferItem(bool upload, string localPath, string devicePath, string runAsPackage)
                : this(upload, localPath, devicePath, runAsPackage, false)
            {
            }

            public TransferItem(bool upload, string localPath, string devicePath,
                string runAsPackage, bool treeTransfer)
            {
                Upload = upload;
                LocalPath = localPath;
                DevicePath = devicePath;
                RunAsPackage = runAsPackage;
                TreeTransfer = treeTransfer;
            }
        }

        #endregion
    }
}
