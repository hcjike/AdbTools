using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdbTools
{
    /// <summary>
    /// 应用列表：一次性拿到设备上的全部应用，按行提供启动、停用/启用、卸载等操作，
    /// 取代原来"先看包名列表、再手动输入包名执行命令"的方式。
    /// </summary>
    public partial class AppListWindow : Window
    {
        private static readonly Brush OkBrush = CreateBrush(0x05, 0x96, 0x69);
        private static readonly Brush ErrorBrush = CreateBrush(0xDC, 0x26, 0x26);
        private static readonly Brush NormalBrush = CreateBrush(0x6B, 0x72, 0x80);

        private readonly ObservableCollection<AppInfo> _apps = new ObservableCollection<AppInfo>();
        private readonly ICollectionView _view;
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer();

        private AppLabelResolver _resolver;
        private string _deviceKey;
        private bool _loading;
        private bool _resolveStoppedByUser;
        private int _filterIndex;

        public string AdbPath { get; set; }

        public Device DeviceInfo { get; set; }

        public AppListWindow()
        {
            InitializeComponent();

            _view = CollectionViewSource.GetDefaultView(_apps);
            _view.Filter = FilterApp;
            appList.ItemsSource = _view;

            _statusTimer.Interval = TimeSpan.FromSeconds(6);
            _statusTimer.Tick += StatusTimer_Tick;

            searchHint.Visibility = Visibility.Visible;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (DeviceInfo != null)
            {
                Title = "应用列表 - " + DeviceInfo.ShowDeviceName;
            }

            LoadApps();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _statusTimer.Stop();
            if (_resolver != null)
            {
                _resolver.Stop();
                _resolver = null;
            }
        }

        // ------------------------------------------------------------ 读取列表

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadApps();
        }

        private void LoadApps()
        {
            if (_loading || DeviceInfo == null)
            {
                return;
            }

            _loading = true;
            SetLoading(true, "正在读取应用列表…");
            SetStatus("正在读取应用列表…", NormalBrush, false);
            progressText.Text = string.Empty;

            // 缓存按设备隔离：先确定设备键（硬件序列号，USB / IP / 无线一致），再读列表
            AppDeviceKey.ResolveAsync(AdbPath, DeviceInfo.SerialArg, DeviceInfo.DeviceMark, key =>
            {
                _deviceKey = key;
                LoadPackageList();
            });
        }

        /// <summary>三条查询分开执行：单条命令更稳，失败时也能各自降级处理。</summary>
        private void LoadPackageList()
        {
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command("shell pm list packages -f") },
                all => LoadSystemFlags(all),
                err =>
                {
                    FinishLoad("读取应用列表失败：" + FirstLine(err));
                    return true;
                });
        }

        private void LoadSystemFlags(string allOutput)
        {
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command("shell pm list packages -s") },
                system => LoadDisabledFlags(allOutput, system),
                err =>
                {
                    // 读不到系统应用标记时按第三方处理，列表仍可用
                    LoadDisabledFlags(allOutput, null);
                    return true;
                });
        }

        private void LoadDisabledFlags(string allOutput, string systemOutput)
        {
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command("shell pm list packages -d") },
                disabled =>
                {
                    BuildApps(allOutput, systemOutput, disabled);
                    FinishLoad(null);
                },
                err =>
                {
                    BuildApps(allOutput, systemOutput, null);
                    FinishLoad("未能读取已停用应用列表，停用状态可能不准确");
                    return true;
                });
        }

        /// <summary>把三次查询结果组装成列表：包名、APK 路径、系统应用、停用状态。</summary>
        private void BuildApps(string allOutput, string systemOutput, string disabledOutput)
        {
            HashSet<string> system = ParsePackageNames(systemOutput);
            HashSet<string> disabled = ParsePackageNames(disabledOutput);

            if (_resolver != null)
            {
                _resolver.Stop();
                _resolver = null;
            }

            // 第三方应用在前：这类才是一般要处理的对象，系统应用数量多且大多不用动
            List<KeyValuePair<string, string>> entries = ParsePackagePaths(allOutput)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => system.Contains(x.Key) ? 1 : 0)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 一次 IN 查询把该设备已缓存的名称、解析状态、服务标记与图标全部取回，避免逐条查库
            Dictionary<string, string> cachedLabels =
                AppLabelStore.GetLabels(_deviceKey, entries.Select(x => x.Key));
            Dictionary<string, bool> cachedServices =
                AppLabelStore.GetServices(_deviceKey, entries.Select(x => x.Key));
            HashSet<string> resolvedNames =
                AppLabelStore.GetResolvedNames(_deviceKey, entries.Select(x => x.Key));
            Dictionary<string, ImageSource> cachedIcons =
                AppIconStore.GetIcons(_deviceKey, entries.Select(x => x.Key));
            AppIconStore.CleanupOrphansOnce();

            _apps.Clear();
            foreach (KeyValuePair<string, string> entry in entries)
            {
                AppInfo app = new AppInfo
                {
                    PackageName = entry.Key,
                    ApkPath = entry.Value,
                    IsSystem = system.Contains(entry.Key),
                    IsDisabled = disabled.Contains(entry.Key)
                };

                string cached;
                if (cachedLabels.TryGetValue(entry.Key, out cached) && !string.IsNullOrEmpty(cached))
                {
                    app.DisplayName = cached;
                    app.FromCache = true;
                }

                // 名称已解析（含"确定无名称"）：下次不再列入待解析队列，避免反复拉 arsc
                if (resolvedNames.Contains(entry.Key))
                {
                    app.NameResolved = true;
                }

                // 系统服务标记按包名复用（包名唯一），命中即立即显示，不必等后台解析 manifest
                bool cachedService;
                if (cachedServices.TryGetValue(entry.Key, out cachedService))
                {
                    app.IsService = cachedService;
                }

                // 已缓存的图标直接用，没有则显示占位，等后台解析补齐
                ImageSource icon;
                if (cachedIcons.TryGetValue(entry.Key, out icon))
                {
                    app.Icon = icon;
                }

                _apps.Add(app);
            }
        }

        private void FinishLoad(string warning)
        {
            _loading = false;
            _resolveStoppedByUser = false;
            SetLoading(false, null);
            _view.Refresh();
            UpdateEmptyHint();
            UpdateCounts();

            if (_apps.Count == 0)
            {
                SetStatus(string.IsNullOrEmpty(warning) ? "没有读到应用列表，请检查设备是否在线" : warning, ErrorBrush);
                return;
            }

            StartResolving();

            if (!string.IsNullOrEmpty(warning))
            {
                SetStatus(warning, ErrorBrush);
            }
            else
            {
                SetStatus("共 " + _apps.Count + " 个应用，正在获取应用名…", NormalBrush);
            }
        }

        /// <summary>解析 “pm list packages -f” 的输出：package:&lt;apk路径&gt;=&lt;包名&gt;。</summary>
        private static List<KeyValuePair<string, string>> ParsePackagePaths(string output)
        {
            List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
            foreach (string line in PackageLines(output))
            {
                string payload = line.Substring("package:".Length).Trim();
                // 路径里也可能带 '='（如 ~~pOVA56k9...== ），所以从右侧切
                int split = payload.LastIndexOf('=');
                if (split <= 0 || split >= payload.Length - 1)
                {
                    continue;
                }

                string path = payload.Substring(0, split).Trim();
                string package = payload.Substring(split + 1).Trim();
                if (path.Length == 0 || package.Length == 0)
                {
                    continue;
                }

                entries.Add(new KeyValuePair<string, string>(package, path));
            }
            return entries;
        }

        /// <summary>解析 “pm list packages -s/-d” 的输出：每行 package:&lt;包名&gt;。</summary>
        private static HashSet<string> ParsePackageNames(string output)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in PackageLines(output))
            {
                string payload = line.Substring("package:".Length).Trim();
                if (payload.Length > 0)
                {
                    names.Add(payload);
                }
            }
            return names;
        }

        /// <summary>只留 package: 开头的行，过滤 cmd.exe 的回显与提示符。</summary>
        private static IEnumerable<string> PackageLines(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                yield break;
            }

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                {
                    yield return line;
                }
            }
        }

        // ------------------------------------------------------------ 应用名解析

        private void StartResolving()
        {
            List<AppInfo> pending = _apps.Where(a => !a.NameResolved).ToList();
            if (pending.Count == 0)
            {
                progressText.Text = string.Empty;
                stopResolveButton.Visibility = Visibility.Collapsed;
                return;
            }

            // 每个应用要读两次 APK，单次几十到几百毫秒，多开几个并发才能在有几百个应用时快速填满
            _resolver = new AppLabelResolver(AdbPath, DeviceInfo.SerialArg, _deviceKey, Dispatcher,
                message => SetStatus(message, NormalBrush), 6);
            _resolver.ItemResolved += Resolver_ItemResolved;
            _resolver.ItemIconResolved += Resolver_IconResolved;
            _resolver.Aborted += Resolver_Aborted;

            progressText.Text = "应用名 0/" + pending.Count;
            stopResolveButton.Visibility = Visibility.Visible;
            _resolver.Start(pending);
        }

        private void Resolver_ItemResolved(AppInfo app, string label, int done, int total)
        {
            if (!string.IsNullOrEmpty(label))
            {
                app.DisplayName = label;
            }

            // 系统服务标记是异步回填的：状态栏计数实时刷新；正在按“系统服务”筛选时补入新项
            if (app.IsService)
            {
                UpdateCounts();
                if (_filterIndex == 4)
                {
                    _view.Refresh();
                }
            }

            if (done >= total)
            {
                progressText.Text = string.Empty;
                stopResolveButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                progressText.Text = "应用名 " + done + "/" + total;
            }
        }

        private void Resolver_IconResolved(AppInfo app)
        {
            ImageSource image = AppIconStore.GetIcon(_deviceKey, app.PackageName);
            if (image != null)
            {
                app.Icon = image;
            }
        }

        private void Resolver_Aborted(string message)
        {
            progressText.Text = string.Empty;
            stopResolveButton.Visibility = Visibility.Collapsed;
            SetStatus(message, ErrorBrush);
        }

        /// <summary>手动停止名称解析：已解析的保留并已写入缓存，未解析的继续显示包名。</summary>
        private void StopResolve_Click(object sender, RoutedEventArgs e)
        {
            if (_resolver == null) return;

            _resolveStoppedByUser = true;
            SuspendResolving();
            progressText.Text = string.Empty;
            SetStatus("已停止获取应用名，未解析的仍显示包名（下次打开会继续）", NormalBrush);
        }

        private void ReResolveName(AppInfo app)
        {
            app.DisplayName = null;
            app.IsBusy = true;

            AppLabelResolver resolver = new AppLabelResolver(AdbPath, DeviceInfo.SerialArg, _deviceKey, Dispatcher,
                message => SetStatus(message, NormalBrush), 1);

            resolver.ItemIconResolved += Resolver_IconResolved;
            resolver.ItemResolved += (target, label, done, total) =>
            {
                target.IsBusy = false;
                if (!string.IsNullOrEmpty(label))
                {
                    target.DisplayName = label;
                    SetStatus("已获取应用名：" + label, OkBrush);
                }
                else
                {
                    SetStatus("未能获取应用名，仍显示包名：" + target.PackageName, ErrorBrush);
                }
            };

            resolver.Start(new[] { app });
        }

        // ------------------------------------------------------------ 行内操作

        private void ToggleApp_Click(object sender, RoutedEventArgs e)
        {
            AppInfo app = GetApp(sender);
            if (app == null) return;

            bool disable = !app.IsDisabled;
            string action = disable ? "停用" : "启用";

            if (disable)
            {
                string tip = app.IsSystem
                    ? "【" + app.Title + "】是系统应用，停用后相关系统功能可能异常。\r\n确定停用吗？"
                    : "确定停用【" + app.Title + "】吗？\r\n停用后可在本列表中重新启用。";
                if (MessageBox.Show(this, tip, "确认停用", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    != MessageBoxResult.OK)
                {
                    return;
                }
            }

            string command = disable
                ? Command("shell pm disable-user --user 0 " + app.PackageName)
                : Command("shell pm enable " + app.PackageName);

            RunAction(app, action, command, CheckPmFailure, result =>
            {
                app.IsDisabled = disable;
                _view.Refresh();
                UpdateCounts();
                SetStatus(action + "成功：" + app.Title, OkBrush);
            });
        }

        private void UninstallApp_Click(object sender, RoutedEventArgs e)
        {
            AppInfo app = GetApp(sender);
            if (app == null) return;

            string tip = app.IsSystem
                ? "确定卸载系统应用【" + app.Title + "】吗？\r\n只会对当前用户卸载，但仍可能影响系统功能。"
                : "确定卸载【" + app.Title + "】吗？\r\n应用的本地数据会一并删除。";
            if (MessageBox.Show(this, tip, "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
            {
                return;
            }

            // 系统应用只能用 --user 0 卸载（等同冻结），第三方应用直接卸载
            string command = app.IsSystem
                ? Command("shell pm uninstall --user 0 " + app.PackageName)
                : Command("uninstall " + app.PackageName);

            RunAction(app, "卸载", command, CheckPmFailure, result =>
            {
                _apps.Remove(app);
                UpdateEmptyHint();
                UpdateCounts();
                SetStatus("已卸载 " + app.Title, OkBrush);
            });
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            AppInfo app = button == null ? null : button.DataContext as AppInfo;
            if (app == null) return;

            Style itemStyle = TryFindResource("UserMenuItemStyle") as Style;
            ContextMenu menu = new ContextMenu();

            menu.Items.Add(CreateMenuItem("清除缓存", itemStyle, () => ClearCache(app)));
            menu.Items.Add(CreateMenuItem("清除数据", itemStyle, () => ClearData(app)));
            menu.Items.Add(CreateMenuItem("强制停止", itemStyle, () => ForceStop(app)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("复制包名", itemStyle, () => CopyText(app.PackageName, "包名")));
            menu.Items.Add(CreateMenuItem("复制 APK 路径", itemStyle, () => CopyText(app.ApkPath, "APK 路径")));
            menu.Items.Add(CreateMenuItem("重新获取应用名", itemStyle, () => ReResolveName(app)));

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ForceStop(AppInfo app)
        {
            RunAction(app, "强制停止", Command("shell am force-stop " + app.PackageName), CheckPmFailure,
                result => SetStatus("已强制停止 " + app.Title, OkBrush));
        }

        /// <summary>
        /// 只清理缓存目录。使用 pm clear --cache-only，账号、登录状态、设置等用户数据都会保留。
        /// --cache-only 需要 Android 11（SDK 30）以上；旧设备可能把该参数静默降级为完整 pm clear
        /// （清掉全部数据且耗时很长），所以执行前先查版本号拦截，避免卡住和误删。
        /// </summary>
        private void ClearCache(AppInfo app)
        {
            if (MessageBox.Show(this,
                    "确定清除【" + app.Title + "】的缓存吗？\r\n只删除缓存文件，账号、登录状态与设置都会保留。",
                    "确认清除缓存", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            // 先确认设备系统版本，避免 --cache-only 在旧设备上被当成完整 clear 执行
            CmdExecutor.ExecuteCommandAndReturnAsync(Command("shell getprop ro.build.version.sdk"), sdkResult =>
            {
                int sdk = ParseSdk(sdkResult);
                if (sdk <= 0)
                {
                    SetStatus("清除缓存已取消：无法确认设备系统版本", ErrorBrush);
                    MessageBox.Show(this, "无法确认设备系统版本，为避免误删全部数据，已取消本次清除缓存。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (sdk < 30)
                {
                    SetStatus("清除缓存失败：需要 Android 11 及以上", ErrorBrush);
                    MessageBox.Show(this, "当前系统不支持仅清理缓存（需要 Android 11 及以上）。\r\n为避免误删全部数据，已取消，未删除任何数据。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RunAction(app, "清除缓存",
                    Command("shell pm clear --cache-only " + app.PackageName),
                    result =>
                    {
                        string failure = CheckPmFailure(result);
                        if (failure != null && failure.IndexOf("Unknown option", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return "当前系统不支持仅清理缓存（需要 Android 11 及以上）";
                        }
                        return failure;
                    },
                    result => SetStatus("已清除缓存：" + app.Title, OkBrush),
                    180, true);
            });
        }

        /// <summary>清除全部数据：会连同缓存一起删除，不可撤销。</summary>
        private void ClearData(AppInfo app)
        {
            if (MessageBox.Show(this,
                    "确定清除【" + app.Title + "】的全部数据吗？\r\n会删除账号、登录状态、设置、缓存等所有内容，且不可撤销。",
                    "确认清除数据", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            RunAction(app, "清除数据", Command("shell pm clear " + app.PackageName), CheckPmFailure,
                result => SetStatus("已清除数据：" + app.Title, OkBrush),
                180, true);
        }

        private void CopyText(string text, string name)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                SetStatus("已复制" + name, OkBrush);
            }
            catch (Exception)
            {
                SetStatus("复制" + name + "失败", ErrorBrush);
            }
        }

        /// <summary>统一的命令执行与结果处理：成功/失败都回到行内状态与状态栏。</summary>
        /// <param name="timeoutSeconds">命令超时时间，清理类操作需要更久</param>
        /// <param name="pauseResolving">清理类操作期间暂停名称解析，避免抢设备 IO 与带宽</param>
        private void RunAction(AppInfo app, string action, string command,
            Func<string, string> checkFailure, Action<string> onSuccess,
            int timeoutSeconds = 60, bool pauseResolving = false)
        {
            // 该行已有命令在执行时忽略重复点击（“更多”菜单可反复打开，容易重复触发）
            if (app.IsBusy)
            {
                return;
            }

            app.IsBusy = true;
            if (pauseResolving)
            {
                SuspendResolving();
                SetStatus("正在" + action + "：" + app.Title + "，应用数据较多时可能需要一两分钟，请勿重复点击",
                    NormalBrush, false);
            }

            Action finish = () =>
            {
                app.IsBusy = false;
                if (pauseResolving) ResumeResolving();
            };

            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { command },
                result =>
                {
                    finish();

                    string failure = checkFailure == null ? null : checkFailure(result);
                    if (failure != null)
                    {
                        SetStatus(action + "失败：" + failure, ErrorBrush);
                        MessageBox.Show(this, "【" + app.Title + "】" + action + "失败：\r\n" + failure,
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (onSuccess != null) onSuccess(result);
                },
                err =>
                {
                    finish();

                    // 清理类命令在设备端可能长时间不返回，本地超时并不代表没执行
                    if (err != null && err.IndexOf("命令执行超时", StringComparison.Ordinal) >= 0)
                    {
                        SetStatus(action + "耗时较长，设备端可能仍在处理：" + app.Title, ErrorBrush);
                        return true;
                    }

                    string message = FirstLine(err);
                    SetStatus(action + "失败：" + message, ErrorBrush);
                    MessageBox.Show(this, "【" + app.Title + "】" + action + "失败：\r\n" + message,
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                },
                timeoutSeconds);
        }

        /// <summary>
        /// 暂停应用名解析。清理命令要扫描并删除大量数据，
        /// 解析器同时在后台拉几百个 APK 会明显拖慢它，也容易让用户以为卡死。
        /// </summary>
        private void SuspendResolving()
        {
            stopResolveButton.Visibility = Visibility.Collapsed;
            if (_resolver == null) return;
            _resolver.Stop();
            _resolver = null;
        }

        /// <summary>操作结束后恢复解析：只补齐还没拿到名称的项。</summary>
        private void ResumeResolving()
        {
            if (_apps.Count == 0 || _resolveStoppedByUser) return;

            if (_apps.Any(a => string.IsNullOrEmpty(a.DisplayName)))
            {
                StartResolving();
            }
            else
            {
                progressText.Text = string.Empty;
            }
        }

        // ------------------------------------------------------------ 辅助

        private string Command(string tail)
        {
            return AdbPath + " -s " + DeviceInfo.SerialArg + " " + tail;
        }

        private static AppInfo GetApp(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as AppInfo;
        }

        private static MenuItem CreateMenuItem(string header, Style style, Action action)
        {
            MenuItem item = new MenuItem { Header = header };
            if (style != null) item.Style = style;
            item.Click += (s, e) => action();
            return item;
        }

        /// <summary>从 cmd 回显里解析 SDK 版本号（取第一个能解析为整数的行）。</summary>
        private static int ParseSdk(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return 0;
            }

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int value;
                if (int.TryParse(line.Trim(), out value))
                {
                    return value;
                }
            }

            return 0;
        }

        /// <summary>识别 pm/am 命令在 stdout 里返回的失败信息（它们不走 stderr）。</summary>
        private static string CheckPmFailure(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return null;

            foreach (string raw in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.StartsWith("Failure", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Exception", StringComparison.OrdinalIgnoreCase)
                    || (line.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return line.Length > 200 ? line.Substring(0, 200) : line;
                }
            }

            return null;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "未知错误";
            }

            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                return line.Length > 200 ? line.Substring(0, 200) : line;
            }

            return "未知错误";
        }

        // ------------------------------------------------------------ 过滤与状态

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            RefreshView();
        }

        private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem item = filterBox.SelectedItem as ComboBoxItem;
            int value;
            _filterIndex = item != null && item.Tag != null && int.TryParse(item.Tag.ToString(), out value) ? value : 0;
            RefreshView();
        }

        private void RefreshView()
        {
            if (_view == null) return;
            _view.Refresh();
            UpdateEmptyHint();
            UpdateCounts();
        }

        private bool FilterApp(object item)
        {
            AppInfo app = item as AppInfo;
            if (app == null) return false;

            if (_filterIndex == 1 && app.IsSystem) return false;
            if (_filterIndex == 2 && !app.IsSystem) return false;
            if (_filterIndex == 3 && !app.IsDisabled) return false;
            if (_filterIndex == 4 && !app.IsService) return false;

            string key = searchBox == null ? null : searchBox.Text;
            if (string.IsNullOrWhiteSpace(key)) return true;

            key = key.Trim();
            if (app.PackageName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return !string.IsNullOrEmpty(app.DisplayName)
                && app.DisplayName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateEmptyHint()
        {
            bool empty = _view == null || _view.IsEmpty;
            emptyHint.Visibility = empty && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCounts()
        {
            if (_apps.Count == 0)
            {
                statusText.Text = "尚未读取到应用";
                statusText.Foreground = NormalBrush;
                return;
            }

            int system = 0;
            int disabled = 0;
            int service = 0;
            foreach (AppInfo app in _apps)
            {
                if (app.IsSystem) system++;
                if (app.IsDisabled) disabled++;
                if (app.IsService) service++;
            }

            string text = string.Format("共 {0} 个应用 · 第三方 {1} · 系统 {2} · 系统服务 {3} · 已停用 {4}",
                _apps.Count, _apps.Count - system, system, service, disabled);

            int visible = 0;
            foreach (object item in _view)
            {
                visible++;
            }
            if (visible != _apps.Count)
            {
                text += " · 当前显示 " + visible;
            }

            statusText.Text = text;
            statusText.Foreground = NormalBrush;
        }

        private void SetStatus(string text, Brush brush, bool temporary = true)
        {
            statusText.Text = text;
            statusText.Foreground = brush;

            _statusTimer.Stop();
            if (temporary)
            {
                _statusTimer.Start();
            }
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            _statusTimer.Stop();
            UpdateCounts();
        }

        private void SetLoading(bool loading, string text)
        {
            loadingMask.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(text)) loadingText.Text = text;
            refreshButton.IsEnabled = !loading;
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
