using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

        private string _currentPath = "/sdcard";
        private string _lastLocalDirectory;
        private bool _busy;
        private volatile bool _cancelRequested;
        private volatile bool _closed;
        private int _itemCount;

        public FileBrowserWindow(string adbPath, string serial, string deviceName)
        {
            InitializeComponent();

            _deviceName = string.IsNullOrEmpty(deviceName) ? serial : deviceName;
            _serial = serial;
            _service = new DeviceFileService(adbPath, serial);
            // 标题基于 XAML 中定义的基础标题追加设备名，改 XAML 的 Title 即可生效
            Title = Title + " - " + _deviceName;

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
            HideListNotice();
            SetStatus("正在读取 " + target + " …");

            var thread = new Thread(() =>
            {
                string error;
                List<DeviceFileEntry> entries = _service.List(target, out error);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;

                    _itemCount = entries.Count;
                    _currentPath = target;
                    pathBox.Text = target;
                    fileList.ItemsSource = entries;

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
            Navigate(_currentPath);
        }

        private void root_Click(object sender, RoutedEventArgs e)
        {
            Navigate("/");
        }

        private void sdcard_Click(object sender, RoutedEventArgs e)
        {
            Navigate("/sdcard");
        }

        private void up_Click(object sender, RoutedEventArgs e)
        {
            Navigate(DeviceFileService.ParentPath(_currentPath));
        }

        private void go_Click(object sender, RoutedEventArgs e)
        {
            Navigate(pathBox.Text);
        }

        private void pathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Navigate(pathBox.Text);
                e.Handled = true;
            }
        }

        private void fileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DeviceFileEntry entry = fileList.SelectedItem as DeviceFileEntry;
            if (entry == null) return;

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

        private void fileList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                Navigate(_currentPath);
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                Navigate(DeviceFileService.ParentPath(_currentPath));
                e.Handled = true;
            }
        }

        #endregion

        #region 《上传 / 下载》

        private void uploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

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
            if (_busy) return;

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
                SetStatus("请先选择要下载的文件或文件夹");
                return;
            }
            DownloadEntries(selected);
        }

        private void DownloadEntries(List<DeviceFileEntry> entries)
        {
            if (_busy || entries == null || entries.Count == 0) return;

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

                    // 上传会改变当前目录内容，下载不影响；统一刷新一次最简单
                    Navigate(_currentPath);
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
            if (_busy) return;

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
            if (_busy) return;

            DeviceFileEntry entry = fileList.SelectedItem as DeviceFileEntry;
            if (entry == null)
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
            if (_busy) return;

            List<DeviceFileEntry> selected = SelectedEntries();
            if (selected.Count == 0)
            {
                SetStatus("请先选择要删除的项目");
                return;
            }

            string names = string.Join("\r\n", selected.Take(10).Select(o => o.Name));
            if (selected.Count > 10) names += "\r\n…（共 " + selected.Count + " 项）";

            MessageBoxResult result = MessageBox.Show(this,
                "确定要删除以下项目吗？\r\n\r\n" + names + "\r\n\r\n删除后无法恢复。",
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
        private static string BuildErrorDetail(string error, string path)
        {
            string hint = string.Empty;
            if (error.IndexOf("权限", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n该目录属于系统或应用私有数据，普通 shell 用户无法访问；"
                     + "可返回上级目录，或改用 /sdcard 等有权限的位置。";
            }
            else if (error.IndexOf("不存在", StringComparison.Ordinal) >= 0)
            {
                hint = "\r\n\r\n目录可能已被移动或删除，可返回上级后重新进入。";
            }

            return "路径：" + path + "\r\n原因：" + error + hint;
        }

        private void noticeBack_Click(object sender, RoutedEventArgs e)
        {
            Navigate(DeviceFileService.ParentPath(_currentPath));
        }

        private void noticeRefresh_Click(object sender, RoutedEventArgs e)
        {
            Navigate(_currentPath);
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
            string text = "共 " + _itemCount + " 项";
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
