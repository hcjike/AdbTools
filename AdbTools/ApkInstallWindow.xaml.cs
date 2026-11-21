using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;

namespace AdbTools
{
    /// <summary>
    /// APK 批量安装列表：先异步解析应用信息，用户勾选后逐项安装，最后在本窗口显示整体结果。
    /// </summary>
    public partial class ApkInstallWindow : Window
    {
        private const int ParseConcurrency = 4;

        private static readonly Brush SuccessSummaryBrush = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        private static readonly Brush FailureSummaryBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        private static readonly Brush SkipSummaryBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
        private static readonly Brush MutedSummaryBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));

        private readonly ObservableCollection<ApkInstallItem> _items = new ObservableCollection<ApkInstallItem>();
        private readonly ICollectionView _view;
        private readonly Device _device;
        private readonly string _adbPath;
        private readonly SemaphoreSlim _parseSemaphore = new SemaphoreSlim(ParseConcurrency);

        private readonly List<string> _installSuccesses = new List<string>();
        private readonly List<string> _installFailures = new List<string>();
        private ApkInstallItem[] _installItems;
        private int _installIndex;
        private DateTime _installStartedAt;

        private volatile bool _closed;
        private bool _parsingDone;
        private bool _busy;
        private bool _finished;
        private bool _syncingSelection;
        private int _lastSuccessCount;
        private int _lastFailCount;
        private int _lastSkipCount;
        private int _pendingParses;
        private InstallErrorWindow _errorWindow;

        public ApkInstallWindow(IEnumerable<string> paths, Device device, string adbPath)
        {
            InitializeComponent();

            _device = device;
            _adbPath = adbPath;
            deviceText.Text = device == null ? string.Empty : device.ShowDeviceName;

            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                ApkInstallItem item = new ApkInstallItem(path, FormatSize(path));
                item.PropertyChanged += Item_PropertyChanged;
                _items.Add(item);
            }

            _view = CollectionViewSource.GetDefaultView(_items);
            apkList.ItemsSource = _view;
            SyncListSelectionFromModel();

            UpdateSummary();
            UpdateButtons();
            StartParsing();
        }

        public Device DeviceInfo { get { return _device; } }

        /// <summary>安装过程中用户是否请求停止。</summary>
        public bool CancelRequested { get; private set; }

        public void SetProgress(int index, int total)
        {
            if (_closed) return;
            progressText.Text = total > 0 ? "安装中 " + index + "/" + total : string.Empty;
        }

        public void UpdateInstallState(ApkInstallItem item, ApkInstallState state, string error = null)
        {
            if (_closed || item == null) return;

            Action apply = () =>
            {
                item.InstallState = state;
                item.ErrorText = error;
            };

            if (Dispatcher.CheckAccess()) apply();
            else Dispatcher.BeginInvoke(apply);
        }

        public void MarkRemainingSkipped(string reason)
        {
            if (_closed) return;

            foreach (ApkInstallItem item in _items.Where(i => i.IsSelected && i.InstallState == ApkInstallState.Pending))
            {
                item.InstallState = ApkInstallState.Skipped;
                item.ErrorText = reason;
            }
        }

        public void Finish(int success, int fail, int skipped)
        {
            if (_closed) return;

            _busy = false;
            _finished = true;

            _lastSuccessCount = success;
            _lastFailCount = fail;
            _lastSkipCount = skipped;
            progressText.Text = string.Empty;

            // 安装成功的项自动排除出选择，避免再次安装时重复安装；失败/跳过项仍保留选中便于重试。
            foreach (ApkInstallItem item in _items)
            {
                if (item.InstallState == ApkInstallState.Success) item.IsSelected = false;
                item.SelectionEnabled = true;
            }
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void StartParsing()
        {
            _pendingParses = _items.Count;
            if (_pendingParses == 0)
            {
                MarkParseDone();
                return;
            }

            foreach (ApkInstallItem item in _items.ToArray())
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    _parseSemaphore.Wait();
                    try
                    {
                        if (_closed) return;

                        string label;
                        string packageName;
                        ApkInfoReader.Read(item.FilePath, out label, out packageName);

                        // 图标只读到内存，不做缓存、不落盘；失败时保持占位图
                        ImageSource icon = ApkIconMemoryReader.Read(item.FilePath);

                        if (!_closed)
                        {
                            Dispatcher.BeginInvoke(new Action(() => ApplyParsed(item, label, packageName, icon)));
                        }
                    }
                    catch (Exception)
                    {
                        // 单个 APK 解析失败不影响其它项，保持文件名兜底
                    }
                    finally
                    {
                        _parseSemaphore.Release();
                        if (Interlocked.Decrement(ref _pendingParses) == 0 && !_closed)
                        {
                            Dispatcher.BeginInvoke(new Action(MarkParseDone));
                        }
                    }
                });
            }
        }

        private void ApplyParsed(ApkInstallItem item, string label, string packageName, ImageSource icon)
        {
            if (_closed) return;

            if (!string.IsNullOrWhiteSpace(label)) item.Label = label.Trim();
            item.PackageName = string.IsNullOrWhiteSpace(packageName) ? null : packageName.Trim();
            item.Icon = icon;
            item.ParseState = string.IsNullOrWhiteSpace(packageName) ? "未解析" : "完成";
        }

        private void MarkParseDone()
        {
            if (_closed) return;

            _parsingDone = true;
            UpdateSummary();
            UpdateButtons();
        }

        private int SelectedCount
        {
            get { return _items.Count(i => i.IsSelected); }
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closed) return;

            // 仅在首次安装前跟随勾选刷新状态；安装完成后再改勾选，保留原有成功/失败/跳过结果。
            if (e.PropertyName == nameof(ApkInstallItem.IsSelected) && !_busy && !_finished)
            {
                ApkInstallItem item = sender as ApkInstallItem;
                if (item != null)
                {
                    item.InstallState = item.IsSelected
                        ? ApkInstallState.Pending
                        : ApkInstallState.Unselected;
                    if (item.IsSelected) item.ErrorText = null;
                }
            }

            if (e.PropertyName == nameof(ApkInstallItem.IsSelected)
                || e.PropertyName == nameof(ApkInstallItem.InstallState)
                || e.PropertyName == nameof(ApkInstallItem.HasError))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateSummary();
                    UpdateButtons();
                }));
            }
        }

        private void UpdateSummary()
        {
            summaryText.Inlines.Clear();

            if (!_finished)
            {
                summaryText.Inlines.Add(new Run("共 " + _items.Count + " 项")
                {
                    Foreground = MutedSummaryBrush
                });
            }
            else
            {
                summaryText.Inlines.Add(new Run("安装结束：") { Foreground = MutedSummaryBrush });
                AddSummaryRun("成功 " + _lastSuccessCount, SuccessSummaryBrush);
                summaryText.Inlines.Add(new Run("，") { Foreground = MutedSummaryBrush });
                if (_lastFailCount > 0) AddSummaryLink("失败 " + _lastFailCount);
                else AddSummaryRun("失败 " + _lastFailCount, FailureSummaryBrush);
                summaryText.Inlines.Add(new Run("，") { Foreground = MutedSummaryBrush });
                AddSummaryRun("跳过 " + _lastSkipCount, SkipSummaryBrush);
            }

            selectionText.Text = "已选 " + SelectedCount + " 项";
        }

        private void AddSummaryRun(string text, Brush brush)
        {
            summaryText.Inlines.Add(new Run(text)
            {
                Foreground = brush,
                FontWeight = FontWeights.SemiBold
            });
        }

        private void AddSummaryLink(string text)
        {
            var link = new Hyperlink(new Run(text))
            {
                Foreground = FailureSummaryBrush,
                FontWeight = FontWeights.SemiBold,
                TextDecorations = null
            };
            link.Click += FailureCount_Click;
            summaryText.Inlines.Add(link);
        }

        private void FailureCount_Click(object sender, RoutedEventArgs e)
        {
            ShowFailureDetails();
        }

        private void UpdateButtons()
        {
            bool editable = !_busy;
            bool allSelected = _items.Count > 0 && SelectedCount == _items.Count;
            selectAllButton.Content = allSelected ? "取消全选" : "全选";
            selectAllButton.IsEnabled = editable;
            invertButton.IsEnabled = editable;
            startButton.Content = _finished ? "再次安装" : "开始安装";
            startButton.IsEnabled = editable && _parsingDone && SelectedCount > 0;
            cancelButton.Content = _finished ? "关闭" : "取消";
        }

        private void ApkItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 安装中锁定选择：普通单击、Ctrl / Shift 单击都不能改变列表高亮
            if (_busy)
            {
                e.Handled = true;
                return;
            }

            // 普通单击行不改变选择；只有复选框或 Ctrl / Shift 组合键才交给子元素处理
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null) return;

            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void ApkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;

            // 兜底：即使有快捷键绕过 Preview 事件，也立刻把高亮恢复成已锁定选择
            if (_busy)
            {
                SyncListSelectionFromModel();
                return;
            }

            _syncingSelection = true;
            foreach (ApkInstallItem item in e.RemovedItems.OfType<ApkInstallItem>())
            {
                item.IsSelected = false;
            }
            foreach (ApkInstallItem item in e.AddedItems.OfType<ApkInstallItem>())
            {
                item.IsSelected = true;
            }
            _syncingSelection = false;

            UpdateSummary();
            UpdateButtons();
        }

        private void ItemCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CheckBox box = sender as CheckBox;
            if (box == null || !box.IsEnabled) return;

            // 复选框点击不交给列表处理，避免普通单击变成多选
            e.Handled = true;
            box.IsChecked = !box.IsChecked;

            ApkInstallItem item = box.DataContext as ApkInstallItem;
            if (item == null) return;

            _syncingSelection = true;
            if (item.IsSelected)
            {
                if (!apkList.SelectedItems.Contains(item)) apkList.SelectedItems.Add(item);
            }
            else if (apkList.SelectedItems.Contains(item))
            {
                apkList.SelectedItems.Remove(item);
            }
            _syncingSelection = false;

            UpdateSummary();
            UpdateButtons();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_busy)
            {
                // 安装期间禁止键盘改变选择高亮；滚轮滚动不受影响
                if (IsSelectionKey(e.Key)) e.Handled = true;
                return;
            }

            if (e.Key != Key.A || Keyboard.Modifiers != ModifierKeys.Control) return;

            foreach (ApkInstallItem item in _view) item.IsSelected = true;
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
            e.Handled = true;
        }

        private static bool IsSelectionKey(Key key)
        {
            switch (key)
            {
                case Key.A:
                case Key.Space:
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown:
                    return true;
                default:
                    return false;
            }
        }

        private void SyncListSelectionFromModel()
        {
            _syncingSelection = true;
            apkList.SelectedItems.Clear();
            foreach (ApkInstallItem item in _view)
            {
                if (item.IsSelected) apkList.SelectedItems.Add(item);
            }
            _syncingSelection = false;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            bool select = SelectedCount < _items.Count;
            foreach (ApkInstallItem item in _view) item.IsSelected = select;
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void Invert_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            foreach (ApkInstallItem item in _view) item.IsSelected = !item.IsSelected;
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !_parsingDone) return;

            ApkInstallItem[] selected = _items.Where(i => i.IsSelected).ToArray();
            if (selected.Length == 0) return;

            // 再次执行时重置本轮状态，避免沿用上一次的成功/失败结果
            _finished = false;
            _lastSuccessCount = 0;
            _lastFailCount = 0;
            _lastSkipCount = 0;

            foreach (ApkInstallItem item in _items)
            {
                item.ErrorText = null;
                item.InstallState = item.IsSelected
                    ? ApkInstallState.Pending
                    : ApkInstallState.Unselected;
            }

            // 安装开始后锁定勾选状态，避免安装结果和用户选择错位
            foreach (ApkInstallItem item in _items) item.SelectionEnabled = false;

            _busy = true;
            CancelRequested = false;
            summaryText.Text = "正在安装，请稍候…";
            UpdateButtons();

            BeginInstall(selected);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                CancelRequested = true;
                summaryText.Text = "正在停止，当前安装结束后不再继续…";
                return;
            }
            Close();
        }

        private void BeginInstall(ApkInstallItem[] selected)
        {
            _installItems = selected;
            _installIndex = 0;
            _installSuccesses.Clear();
            _installFailures.Clear();
            _installStartedAt = DateTime.Now;
            InstallNext();
        }

        /// <summary>串行安装下一个勾选项；CmdExecutor 回调在 UI 线程，天然保证顺序。</summary>
        private void InstallNext()
        {
            if (_closed) return;

            if (CancelRequested)
            {
                MarkRemainingSkipped("已取消");
                FinishInstall();
                return;
            }

            if (_installItems == null || _installIndex >= _installItems.Length)
            {
                FinishInstall();
                return;
            }

            ApkInstallItem item = _installItems[_installIndex++];
            SetProgress(_installIndex, _installItems.Length);
            item.InstallState = ApkInstallState.Installing;
            item.ErrorText = null;

            string original = item.FilePath;
            string installPath;
            try
            {
                installPath = PrepareInstallFile(original);
            }
            catch (Exception ex)
            {
                string displayName = DescribeApp(item);
                string reason = Globals.DescribeInstallError(ex.Message);
                _installFailures.Add(displayName + "：" + reason);
                item.InstallState = ApkInstallState.Failed;
                item.ErrorText = reason;
                InstallNext();
                return;
            }

            string serialArg = Globals.QuoteSerial(_device == null ? null : _device.DeviceMark);
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { $"{_adbPath} -s {serialArg} install \"{installPath}\"" },
                result =>
                {
                    DeleteTempInstallFile(installPath, original);
                    string displayName = DescribeApp(item);

                    if (Globals.ContainsAny(result, "Success"))
                    {
                        _installSuccesses.Add(displayName);
                        item.InstallState = ApkInstallState.Success;
                        InstallLog.Write("安装成功：{0} / {1}", DeviceName, displayName);
                    }
                    else
                    {
                        string reason = Globals.DescribeInstallError(result);
                        _installFailures.Add(displayName + "：" + reason);
                        item.InstallState = ApkInstallState.Failed;
                        item.ErrorText = reason;
                    }

                    InstallNext();
                },
                err =>
                {
                    DeleteTempInstallFile(installPath, original);
                    string displayName = DescribeApp(item);
                    string reason = Globals.DescribeInstallError(err);

                    _installFailures.Add(displayName + "：" + reason);
                    item.InstallState = ApkInstallState.Failed;
                    item.ErrorText = reason;
                    InstallLog.Write("安装失败：{0} / {1}：{2}", DeviceName, displayName, err);

                    if (Globals.ContainsAny(err, "device offline", "Error: closed"))
                    {
                        MarkRemainingSkipped("设备离线");
                        FinishInstall();
                    }
                    else
                    {
                        InstallNext();
                    }
                    return true;
                }, 600);
        }

        private string DeviceName
        {
            get { return _device == null ? null : _device.ShowDeviceName; }
        }

        private void FinishInstall()
        {
            int success = _installSuccesses.Count;
            int fail = _installFailures.Count;
            int skipped = _items.Count(i => i.InstallState == ApkInstallState.Skipped
                || i.InstallState == ApkInstallState.Unselected);

            Finish(success, fail, skipped);
            ReportInstallSummary();
            _installItems = null;
        }

        private void ReportInstallSummary()
        {
            int ok = _installSuccesses.Count;
            int bad = _installFailures.Count;
            if (ok == 0 && bad == 0) return;

            App.NotifyResult(
                BuildInstallSummary(_installSuccesses, _installFailures, DeviceName, _installStartedAt),
                "install",
                bad > 0 ? BalloonIcon.Warning : BalloonIcon.Info,
                Globals.AppSettings.NOTIFY_INSTALL_RESULT,
                this);

            _installSuccesses.Clear();
            _installFailures.Clear();
        }

        private void ShowFailureDetails()
        {
            if (_busy || _closed) return;

            ApkInstallItem[] failures = _items.Where(i => i.InstallState == ApkInstallState.Failed && i.HasError).ToArray();
            if (failures.Length == 0) return;

            if (_errorWindow == null || !_errorWindow.IsLoaded)
            {
                _errorWindow = new InstallErrorWindow(failures);
                _errorWindow.Closed += ErrorWindow_Closed;
                _errorWindow.Show();
            }
            else
            {
                _errorWindow.Activate();
            }
        }

        private void ErrorWindow_Closed(object sender, EventArgs e)
        {
            _errorWindow = null;
        }

        private static string BuildInstallSummary(IList<string> successes, IList<string> failures,
            string deviceName, DateTime startedAt)
        {
            int ok = successes.Count;
            int bad = failures.Count;

            var summary = new StringBuilder();
            summary.Append("安装完成：成功 ").Append(ok).Append(" 个");
            if (bad > 0) summary.Append("，失败 ").Append(bad).Append(" 个");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(deviceName)) parts.Add("设备 " + deviceName);
            if (startedAt != default(DateTime))
            {
                double seconds = (DateTime.Now - startedAt).TotalSeconds;
                parts.Add(seconds < 60
                    ? "用时 " + Math.Max(1, (int)Math.Round(seconds)) + " 秒"
                    : "用时 " + (seconds / 60).ToString("0.0") + " 分钟");
            }
            if (parts.Count > 0) summary.Append("\r\n").Append(string.Join(" · ", parts.ToArray()));

            const int MaxItems = 4;
            int failureQuota = bad > 0 ? Math.Min(bad, 3) : 0;
            int successQuota = bad > 0 ? (ok > 0 ? Math.Max(1, MaxItems - failureQuota) : 0) : MaxItems;

            int shown = 0;
            foreach (string entry in successes.Take(successQuota))
            {
                summary.Append("\r\n✔ ").Append(entry);
                shown++;
            }
            foreach (string entry in failures.Take(failureQuota))
            {
                summary.Append("\r\n✘ ").Append(entry);
                shown++;
            }
            if (ok + bad > shown)
            {
                summary.Append("\r\n…还有 ").Append(ok + bad - shown).Append(" 项，详见运行日志");
            }
            return summary.ToString();
        }

        private static string DescribeApp(ApkInstallItem item)
        {
            return string.IsNullOrWhiteSpace(item.PackageName)
                ? item.DisplayName
                : item.DisplayName + "（" + item.PackageName + "）";
        }

        private static string PrepareInstallFile(string apkPath)
        {
            if (apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return apkPath;

            string target = Globals.GetCacheFilePath("install-" + Guid.NewGuid().ToString("N") + ".apk");
            File.Copy(apkPath, target, true);
            return target;
        }

        private static void DeleteTempInstallFile(string installPath, string originalPath)
        {
            if (string.Equals(installPath, originalPath, StringComparison.OrdinalIgnoreCase)) return;

            string cacheRoot = Globals.CacheDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!installPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                File.Delete(installPath);
            }
            catch (Exception)
            {
                // 被占用时留给启动时的缓存清理
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
            if (_busy) CancelRequested = true;

            try
            {
                if (_errorWindow != null && _errorWindow.IsLoaded) _errorWindow.Close();
            }
            catch { }
            _errorWindow = null;
        }

        private static string FormatSize(string path)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (size >= 1024L * 1024 * 1024)
                {
                    return (size / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
                }
                if (size >= 1024 * 1024)
                {
                    return (size / 1024.0 / 1024).ToString("0.0") + " MB";
                }
                return Math.Max(1, size / 1024) + " KB";
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
