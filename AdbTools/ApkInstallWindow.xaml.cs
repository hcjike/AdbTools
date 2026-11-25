using AdbTools.bean;
using Microsoft.Win32;
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
        private const int MaxFileCount = Globals.DropLimits.MaxFileCount;
        private const int MaxApkItems = Globals.DropLimits.MaxApkCount;

        private static readonly Brush SuccessSummaryBrush = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        private static readonly Brush FailureSummaryBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        private static readonly Brush SkipSummaryBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
        private static readonly Brush MutedSummaryBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));

        private readonly ObservableCollection<ApkInstallItem> _items = new ObservableCollection<ApkInstallItem>();
        private readonly ICollectionView _view;
        private readonly Device _device;
        private readonly string _adbPath;
        private int _deviceSdk;
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

        /// <summary>已添加的 APK 全路径（不区分大小写），用于按钮添加与拖放去重。</summary>
        private readonly HashSet<string> _knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _dragFileValid;
        private string _truncatedHint;

        public ApkInstallWindow(IEnumerable<string> paths, Device device, string adbPath)
        {
            InitializeComponent();

            _device = device;
            _adbPath = adbPath;
            _deviceSdk = device == null ? 0 : device.SdkVersion;
            deviceText.Text = device == null ? string.Empty : device.ShowDeviceName;
            UpdateDeviceSdkChip();
            AddPaths(paths);

            _view = CollectionViewSource.GetDefaultView(_items);
            apkList.ItemsSource = _view;
            SyncListSelectionFromModel();

            UpdateSummary();
            UpdateButtons();
        }

        public Device DeviceInfo { get { return _device; } }

        /// <summary>安装过程中用户是否请求停止。</summary>
        public bool CancelRequested { get; private set; }

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

            // 安装成功的项自动排除出选择，避免再次安装时重复安装；失败/跳过项仍保留选中便于重试。
            foreach (ApkInstallItem item in _items)
            {
                if (item.InstallState == ApkInstallState.Success) item.IsSelected = false;
                // SDK 不兼容项始终保持不可选
                item.SelectionEnabled = !item.SdkIncompatible;
            }
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void StartParsing(IEnumerable<ApkInstallItem> newItems)
        {
            ApkInstallItem[] array = newItems.ToArray();
            if (array.Length == 0)
            {
                MarkParseDone();
                return;
            }

            Interlocked.Add(ref _pendingParses, array.Length);
            foreach (ApkInstallItem item in array)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    _parseSemaphore.Wait();
                    try
                    {
                        if (_closed) return;

                        string label;
                        string packageName;
                        int minSdkVersion;
                        string versionName;
                        ApkInfoReader.Read(item.FilePath, out label, out packageName, out minSdkVersion, out versionName);

                        // 图标只读到内存，不做缓存、不落盘；失败时保持占位图
                        ImageSource icon = ApkIconMemoryReader.Read(item.FilePath);

                        if (!_closed)
                        {
                            Dispatcher.BeginInvoke(new Action(() => ApplyParsed(item, label, packageName, minSdkVersion, versionName, icon)));
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

        private void ApplyParsed(ApkInstallItem item, string label, string packageName,
            int minSdkVersion, string versionName, ImageSource icon)
        {
            if (_closed) return;

            if (!string.IsNullOrWhiteSpace(label)) item.Label = label.Trim();
            item.PackageName = string.IsNullOrWhiteSpace(packageName) ? null : packageName.Trim();
            item.MinSdkVersion = minSdkVersion;
            item.VersionName = string.IsNullOrWhiteSpace(versionName) ? null : versionName.Trim();
            item.Icon = icon;
            item.ParseState = string.IsNullOrWhiteSpace(packageName) ? "未解析" : "完成";

            // SDK 要求高于设备：标记不兼容、给出原因，并禁止勾选
            if (_deviceSdk > 0 && minSdkVersion > _deviceSdk)
            {
                item.SdkErrorText = "设备系统版本过低：应用要求最低 SDK " + minSdkVersion
                    + "，当前设备 SDK " + _deviceSdk + "，无法安装";
                item.SdkIncompatible = true;
                item.IsSelected = false;
                item.SelectionEnabled = false;
                item.InstallState = ApkInstallState.Unselected;
                if (apkList.SelectedItems.Contains(item)) apkList.SelectedItems.Remove(item);
            }
        }

        /// <summary>刷新设备行的 SDK 徽标；未读到 SDK 时隐藏。</summary>
        private void UpdateDeviceSdkChip()
        {
            deviceSdkChip.Visibility = _deviceSdk > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_deviceSdk > 0)
            {
                deviceSdkText.Text = "SDK " + _deviceSdk;
            }
        }

        private void MarkParseDone()
        {
            if (_closed) return;

            _parsingDone = true;
            UpdateSummary();
            UpdateButtons();
        }

        /// <summary>按全路径去重后添加 APK；返回实际新增数量。</summary>
        private int AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return 0;

            List<ApkInstallItem> added = new List<ApkInstallItem>();
            _truncatedHint = null;
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                // 真正的开销在解析 APK（两次打开 zip、读资源表、解码图标），单独限制条目数
                if (_items.Count >= MaxApkItems)
                {
                    _truncatedHint = "仅添加前 " + MaxApkItems + " 个 APK，其余未加入";
                    break;
                }
                if (!_knownPaths.Add(path)) continue;

                ApkInstallItem item = new ApkInstallItem(path, FormatSize(path));
                item.PropertyChanged += Item_PropertyChanged;
                _items.Add(item);
                added.Add(item);
            }

            if (added.Count == 0)
            {
                if (_truncatedHint != null) UpdateSummary();
                return 0;
            }

            // 新增项也要解析，解析完成前暂时禁用开始安装；退出安装结束态
            _finished = false;
            _parsingDone = false;
            // 把数据模型的默认选中同步到列表，显示选中边框
            // 构造函数阶段 ItemsSource 尚未绑定，留到构造末尾统一同步
            if (apkList.ItemsSource != null)
            {
                _syncingSelection = true;
                foreach (ApkInstallItem item in added.Where(i => i.IsSelected))
                {
                    if (!apkList.SelectedItems.Contains(item)) apkList.SelectedItems.Add(item);
                }
                _syncingSelection = false;
            }
            UpdateSummary();
            UpdateButtons();
            StartParsing(added);
            return added.Count;
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
                    // 兜底：不兼容项永远不允许被选中
                    if (item.IsSelected && item.SdkIncompatible)
                    {
                        item.IsSelected = false;
                        return;
                    }
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

        /// <summary>列表为空时显示引导提示，隐藏列表本身。</summary>
        private void UpdateEmptyHint()
        {
            bool empty = _items.Count == 0;
            emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            apkList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateSummary()
        {
            UpdateEmptyHint();

            summaryText.Inlines.Clear();
            summaryText.Inlines.Add(new Run("共 " + _items.Count + " 项 · 已选 " + SelectedCount + " 项")
            {
                Foreground = MutedSummaryBrush
            });
            if (!string.IsNullOrEmpty(_truncatedHint))
            {
                summaryText.Inlines.Add(new Run(" · " + _truncatedHint) { Foreground = SkipSummaryBrush });
            }

            // 结果栏仅安装完成后显示
            if (_finished)
            {
                resultBar.Visibility = Visibility.Visible;
                resultText.Inlines.Clear();
                resultText.Inlines.Add(new Run("安装结束：") { Foreground = MutedSummaryBrush });
                AddResultRun("成功 " + _lastSuccessCount, SuccessSummaryBrush);
                resultText.Inlines.Add(new Run("，") { Foreground = MutedSummaryBrush });
                if (_lastFailCount > 0) AddResultLink("失败 " + _lastFailCount);
                else AddResultRun("失败 " + _lastFailCount, FailureSummaryBrush);
                resultText.Inlines.Add(new Run("，") { Foreground = MutedSummaryBrush });
                AddResultRun("跳过 " + _lastSkipCount, SkipSummaryBrush);
            }
            else
            {
                resultBar.Visibility = Visibility.Collapsed;
            }

        }

        private void AddResultRun(string text, Brush brush)
        {
            resultText.Inlines.Add(new Run(text)
            {
                Foreground = brush,
                FontWeight = FontWeights.SemiBold
            });
        }

        private void AddResultLink(string text)
        {
            var link = new Hyperlink(new Run(text))
            {
                Foreground = FailureSummaryBrush,
                FontWeight = FontWeights.SemiBold,
                TextDecorations = null
            };
            link.Click += FailureCount_Click;
            resultText.Inlines.Add(link);
        }

        private void FailureCount_Click(object sender, RoutedEventArgs e)
        {
            ShowFailureDetails();
        }

        private void UpdateButtons()
        {
            bool editable = !_busy;
            int selectableCount = _items.Count(i => !i.SdkIncompatible);
            bool allSelected = selectableCount > 0 && SelectedCount == selectableCount;
            selectAllButton.Content = allSelected ? "取消全选" : "全选";
            selectAllButton.IsEnabled = editable;
            invertButton.IsEnabled = editable;
            addButton.IsEnabled = editable;
            removeButton.IsEnabled = editable && SelectedCount > 0;
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

                // Run 等文本内容元素不是 Visual，不能用 VisualTreeHelper 往上找
                element = element is Visual
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
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

            foreach (ApkInstallItem item in _view)
            {
                if (!item.SdkIncompatible) item.IsSelected = true;
            }
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

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            List<ApkInstallItem> toRemove = _items.Where(i => i.IsSelected).ToList();
            if (toRemove.Count == 0)
            {
                MessageBox.Show(this, "请先勾选要删除的 APK。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult result = MessageBox.Show(this,
                "确定从安装列表中移除选中的 " + toRemove.Count + " 个 APK 吗？",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            foreach (ApkInstallItem item in toRemove)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                _knownPaths.Remove(item.FilePath);
                _items.Remove(item);
                if (apkList.SelectedItems.Contains(item)) apkList.SelectedItems.Remove(item);
            }

            UpdateSummary();
            UpdateButtons();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            ShowAddApkDialog();
        }

        private void ShowAddApkDialog()
        {
            if (_busy) return;

            if (_items.Count >= MaxApkItems)
            {
                MessageBox.Show(this, "安装列表已满（" + MaxApkItems + " 个 APK），请先移除部分再添加。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "APK 文件 (*.apk;*.apk.*)|*.apk;*.apk.*",
                FilterIndex = 1,
                Multiselect = true,
                RestoreDirectory = true
            };

            if (dialog.ShowDialog() != true) return;

            int added = AddPaths(dialog.FileNames);
            if (added == 0 && string.IsNullOrEmpty(_truncatedHint))
            {
                int count = dialog.FileNames.Count(p => !string.IsNullOrWhiteSpace(p)
                    && ApkDropHelper.IsApkFile(p) && _knownPaths.Contains(p));
                MessageBox.Show(this, "所选 " + count + " 个 APK 均已在列表中。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ApkList_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var result = ApkDropHelper.Analyze(
                files, MaxFileCount, _knownPaths, _items.Count, MaxApkItems, _busy, "列表",
                (text, valid) =>
                {
                    dropHintText.Text = text;
                    dropHintText.Foreground = valid ? Brushes.LightGreen : Brushes.LightCoral;
                });
            _dragFileValid = result.Valid;
            e.Effects = _dragFileValid ? DragDropEffects.Copy : DragDropEffects.None;
            dropOverlay.Visibility = Visibility.Visible;
            dropHintBorder.Visibility = Visibility.Visible;
            e.Handled = true;
        }

        private void ApkList_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropHint();
            e.Handled = true;
        }

        private void ApkList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = _dragFileValid ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ApkList_Drop(object sender, DragEventArgs e)
        {
            // 先保存有效性，ResetDropHint 会清除该标志
            bool valid = _dragFileValid && e.Data.GetDataPresent(DataFormats.FileDrop);
            ResetDropHint();
            if (!valid)
            {
                e.Handled = true;
                return;
            }

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            bool dropExceeded;
            AddPaths(ApkDropHelper.CollectApkFiles(files, MaxFileCount, out dropExceeded));
            e.Handled = true;
        }

        private void ResetDropHint()
        {
            dropOverlay.Visibility = Visibility.Collapsed;
            dropHintBorder.Visibility = Visibility.Collapsed;
            dropHintText.Text = string.Empty;
            _dragFileValid = false;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            int selectableCount = _items.Count(i => !i.SdkIncompatible);
            bool select = SelectedCount < selectableCount;
            foreach (ApkInstallItem item in _view)
            {
                if (!item.SdkIncompatible) item.IsSelected = select;
            }
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void Invert_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            foreach (ApkInstallItem item in _view)
            {
                if (!item.SdkIncompatible) item.IsSelected = !item.IsSelected;
            }
            SyncListSelectionFromModel();
            UpdateSummary();
            UpdateButtons();
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !_parsingDone) return;

            ApkInstallItem[] selected = _items.Where(i => i.IsSelected).ToArray();
            if (selected.Length == 0) return;

            // 设备信息异步解析，窗口打开时可能还没拿到 SDK，开始安装前补查一次
            if (_deviceSdk <= 0 && _device != null)
            {
                string command = _adbPath + " -s " + Globals.QuoteSerial(_device.DeviceMark)
                    + " shell getprop ro.build.version.sdk";
                CmdExecutor.ExecuteCommandAndReturnAsync(new[] { command }, result =>
                {
                    if (_closed) return;
                    int sdk = MainWindow.ParseSdkVersion(result);
                    if (sdk > 0)
                    {
                        _deviceSdk = sdk;
                        _device.SdkVersion = sdk;
                        UpdateDeviceSdkChip();
                    }
                    StartInstallChecked(selected);
                }, err => { if (!_closed) StartInstallChecked(selected); return true; }, 15);
                summaryText.Text = "正在读取设备系统版本…";
                return;
            }

            StartInstallChecked(selected);
        }

        /// <summary>按设备 SDK 过滤不兼容的 APK，提示原因后只安装满足最低要求的项。</summary>
        private void StartInstallChecked(ApkInstallItem[] selected)
        {
            if (_closed || _busy) return;

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

            // 兜底：设备 SDK 是开始安装时才补查到的，此时再标记不兼容
            List<ApkInstallItem> incompatible = _deviceSdk > 0
                ? selected.Where(i => i.MinSdkVersion > _deviceSdk && !i.SdkIncompatible).ToList()
                : new List<ApkInstallItem>();
            List<string> incompatibleReasons = new List<string>();
            foreach (ApkInstallItem item in incompatible)
            {
                item.SdkErrorText = "设备系统版本过低：应用要求最低 SDK " + item.MinSdkVersion
                    + "，当前设备 SDK " + _deviceSdk + "，无法安装";
                item.SdkIncompatible = true;
                item.IsSelected = false;
                item.ErrorText = item.SdkErrorText;
                item.InstallState = ApkInstallState.Failed;
                incompatibleReasons.Add(DescribeApp(item) + "：" + item.SdkErrorText);
            }

            // 安装开始后锁定勾选状态，避免安装结果和用户选择错位
            foreach (ApkInstallItem item in _items) item.SelectionEnabled = false;

            _busy = true;
            CancelRequested = false;
            summaryText.Text = "正在安装，请稍候…";
            UpdateButtons();

            if (incompatible.Count > 0)
            {
                MessageBox.Show(this,
                    "以下应用要求的最低系统版本高于当前设备，无法安装：\r\n\r\n"
                    + string.Join("\r\n", incompatibleReasons),
                    "无法安装", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            BeginInstall(selected.Except(incompatible).ToArray(), incompatibleReasons);
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

        private void BeginInstall(ApkInstallItem[] selected, IEnumerable<string> preFailures = null)
        {
            _installItems = selected;
            _installIndex = 0;
            _installSuccesses.Clear();
            _installFailures.Clear();
            if (preFailures != null) _installFailures.AddRange(preFailures);
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
            item.InstallState = ApkInstallState.Installing;
            item.ErrorText = null;
            // 滚动到当前安装项，便于用户看到进度
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed) apkList.ScrollIntoView(item);
            }));

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
