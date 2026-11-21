using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
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
        private bool _deviceKeyUsable;
        private int _deviceKeyAttempts;
        private int _listAttempts;
        private bool _loading;
        private bool _resolveStoppedByUser;
        private int _filterIndex;
        private int _debugLoadGeneration;
        private bool _batchBusy;
        private bool _batchCancelled;
        private bool _syncingSelection;

        private enum AppAction
        {
            Toggle,
            Disable,
            Enable,
            Restore,
            Uninstall,
            ForceStop,
            ClearData
        }

        private sealed class AppActionPlan
        {
            public AppInfo App;
            public AppAction Action;
            public string Name;
            public string Command;
            public Func<string, string> CheckFailure;
            public Action<AppInfo> OnSuccess;
            public int TimeoutSeconds;
            public bool PauseResolving;
        }

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
            _batchCancelled = true;
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

            // 已拿到硬件序列号时直接复用，省一次 adb 调用；
            // 回退成连接序列号的情况不固化，下次刷新会重新尝试拿硬件序列号
            if (_deviceKeyUsable && !string.IsNullOrEmpty(_deviceKey))
            {
                LoadPackageList();
                return;
            }

            ResolveDeviceKey();
        }

        /// <summary>
        /// 取设备键。设备列表刷新时已顺带读回硬件序列号，可直接使用；
        /// 没有时再发命令，adb 冷启动偶尔拿不到输出会退化成连接序列号，
        /// 这种情况延迟重试一次，避免缓存键漂移、列表也读不出来。
        /// </summary>
        private void ResolveDeviceKey()
        {
            string cached = DeviceInfo == null ? null : DeviceInfo.HardwareKey;
            if (!string.IsNullOrEmpty(cached))
            {
                ApplyDeviceKey(cached, true);
                return;
            }

            AppDeviceKey.ResolveAsync(AdbPath, DeviceInfo.SerialArg, DeviceInfo.DeviceMark,
                key => ApplyDeviceKey(key, false));
        }

        /// <summary>写入设备键并开始加载列表。fromDeviceList 表示值已在设备列表阶段取到，无需重试。</summary>
        private void ApplyDeviceKey(string key, bool fromDeviceList)
        {
            _deviceKey = key;
            _deviceKeyUsable = fromDeviceList || key != AppDeviceKey.Normalize(DeviceInfo.DeviceMark);

            if (!_deviceKeyUsable && _deviceKeyAttempts++ < 1)
            {
                RetryLater(ResolveDeviceKey);
                return;
            }

            _deviceKeyAttempts = 0;
            LoadPackageList();
        }

        /// <summary>延迟 400ms 重试（adb 刚拉起时首条命令容易拿不到输出）。</summary>
        private void RetryLater(Action action)
        {
            DispatcherTimer retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            retry.Tick += (s, e) =>
            {
                retry.Stop();
                action();
            };
            retry.Start();
        }

        /// <summary>
        /// 三条查询分开执行：单条命令更稳，失败时也能各自降级。
        /// -u 表示连同"已卸载但安装文件还在"的应用一起列出；老系统不支持时自动退回。
        /// </summary>
        private void LoadPackageList(bool includeUninstalled = true)
        {
            string tail = includeUninstalled
                ? "shell pm list packages -f -u"
                : "shell pm list packages -f";

            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command(tail) },
                all =>
                {
                    _listAttempts = 0;
                    LoadSystemFlags(all);
                },
                err =>
                {
                    if (includeUninstalled)
                    {
                        // 少数系统不支持 -u：退回只列已安装的应用
                        RetryLater(() => LoadPackageList(false));
                        return true;
                    }

                    // adb 刚拉起时首条命令可能拿不到输出：重试一次再报错
                    if (_listAttempts++ < 1)
                    {
                        RetryLater(() => LoadPackageList(false));
                        return true;
                    }

                    _listAttempts = 0;
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
                disabled => LoadInstalledPackages(allOutput, systemOutput, disabled, null),
                err =>
                {
                    LoadInstalledPackages(allOutput, systemOutput, null,
                        "未能读取已停用应用列表，停用状态可能不准确");
                    return true;
                });
        }

        /// <summary>
        /// 取"仅已安装"的包名集合：上面的 -f -u 会带出已卸载的包，需要用这个集合区分。
        /// 读不到时按"全部已安装"处理，列表仍可用，只是不显示已卸载状态。
        /// </summary>
        private void LoadInstalledPackages(string allOutput, string systemOutput, string disabledOutput,
            string warning)
        {
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command("shell pm list packages") },
                installed =>
                {
                    BuildApps(allOutput, systemOutput, disabledOutput, installed,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    FinishLoad(warning);
                    StartDebugLoad();
                },
                err =>
                {
                    BuildApps(allOutput, systemOutput, disabledOutput, null,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    FinishLoad(warning);
                    StartDebugLoad();
                    return true;
                });
        }

        /// <summary>
        /// Debug 标记改为列表显示后再异步回填：设备端先 grep 掉无关输出（约 3MB → 15KB），
        /// 列表不必等 dumpsys 跑完；序号用于丢弃连续刷新时的旧请求结果。
        /// 不使用缓存，每次刷新都重新读取；失败时只是标签不显示，列表不受影响。
        /// </summary>
        private void StartDebugLoad()
        {
            int generation = ++_debugLoadGeneration;
            // 整段必须加双引号，否则 cmd.exe 会把管道当成本地管道（Windows 没有 grep）
            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { Command("shell \"dumpsys package | grep -E 'Package \\[|DEBUGGABLE'\"") },
                output =>
                {
                    // 个别设备的 toybox 不支持 grep -E：退回全量输出，本地解析一样可用
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        CmdExecutor.ExecuteCommandAndReturnAsync(
                            new[] { Command("shell dumpsys package") },
                            full => ApplyDebugFlags(full, generation),
                            err => { return true; });
                        return;
                    }
                    ApplyDebugFlags(output, generation);
                },
                err => { return true; });
        }

        private void ApplyDebugFlags(string output, int generation)
        {
            if (generation != _debugLoadGeneration) return;

            HashSet<string> debugPackages = ParseDebugPackages(output);
            foreach (AppInfo app in _apps)
            {
                app.IsDebug = debugPackages.Contains(app.PackageName);
            }
            _view.Refresh();
        }

        /// <summary>解析 dumpsys package：Package [包名] 开一个包块，块内 flags 含 DEBUGGABLE 即打标。</summary>
        private static HashSet<string> ParseDebugPackages(string output)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(output)) return result;

            const string marker = "Package [";
            string current = null;
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                int open = line.IndexOf(marker, StringComparison.Ordinal);
                if (open >= 0)
                {
                    int close = line.IndexOf(']', open + marker.Length);
                    current = close > open + marker.Length
                        ? line.Substring(open + marker.Length, close - open - marker.Length).Trim()
                        : null;
                    continue;
                }

                if (current != null && line.IndexOf("DEBUGGABLE", StringComparison.Ordinal) >= 0)
                {
                    result.Add(current);
                }
            }
            return result;
        }

        /// <summary>把查询结果组装成列表：包名、APK 路径、系统应用、停用状态、是否已卸载、Debug 标记。</summary>
        private void BuildApps(string allOutput, string systemOutput, string disabledOutput,
            string installedOutput, HashSet<string> debugPackages)
        {
            HashSet<string> system = ParsePackageNames(systemOutput);
            HashSet<string> disabled = ParsePackageNames(disabledOutput);

            // 读不到已安装集合时视为全部已安装，避免误标"已卸载"
            HashSet<string> installed = string.IsNullOrEmpty(installedOutput)
                ? null
                : ParsePackageNames(installedOutput);

            if (_resolver != null)
            {
                _resolver.Stop();
                _resolver = null;
            }

            // 排序：第三方在前（常处理），其次系统应用，已卸载的排最后
            List<KeyValuePair<string, string>> entries = ParsePackagePaths(allOutput)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => installed != null && !installed.Contains(x.Key) ? 2 : 0)
                .ThenBy(x => system.Contains(x.Key) ? 1 : 0)
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
                // pm list packages -s 只列"已安装"的系统应用，卸载后会缺失；
                // APK 位于系统分区同样是预装应用的证据，两者取或
                bool isSystem = system.Contains(entry.Key) || IsSystemPartitionPath(entry.Value);

                // 出现在 -u 列表里、但不在"已安装"集合中：说明已从手机上移除
                bool isUninstalled = installed != null && !installed.Contains(entry.Key);

                // 只有系统预装的安装文件还在，install-existing 才能装回来；
                // 第三方应用是完整卸载（APK 已删除），列出来也只能恢复失败，直接跳过
                if (isUninstalled && !isSystem)
                {
                    continue;
                }

                AppInfo app = new AppInfo
                {
                    PackageName = entry.Key,
                    ApkPath = entry.Value,
                    IsSystem = isSystem,
                    IsDisabled = disabled.Contains(entry.Key),
                    IsUninstalled = isUninstalled,
                    IsDebug = debugPackages != null && debugPackages.Contains(entry.Key)
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

        /// <summary>
        /// APK 是否位于系统只读分区：这类文件不随卸载被删除，install-existing 能装回来；
        /// 第三方应用装在 /data/app，完整卸载后文件已删，恢复会失败。
        /// </summary>
        private static bool IsSystemPartitionPath(string apkPath)
        {
            if (string.IsNullOrEmpty(apkPath))
            {
                return false;
            }

            string[] roots =
            {
                "/system/", "/system_ext/", "/product/", "/vendor/",
                "/odm/", "/oem/", "/apex/", "/my_"
            };

            foreach (string root in roots)
            {
                if (apkPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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
            // 已卸载的应用不参与自动解析：先恢复才有意义，避免白拉几百 MB 资源
            List<AppInfo> pending = _apps.Where(a => !a.NameResolved && !a.IsUninstalled).ToList();
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
            ReResolveNames(new[] { app });
        }

        private void ReResolveNames(IEnumerable<AppInfo> apps)
        {
            AppInfo[] targets = apps.Where(a => a != null && !a.IsBusy).Distinct().ToArray();
            if (targets.Length == 0) return;

            foreach (AppInfo app in targets)
            {
                app.DisplayName = null;
                app.IsBusy = true;
            }

            int concurrency = Math.Min(6, Math.Max(1, targets.Length));
            AppLabelResolver resolver = new AppLabelResolver(AdbPath, DeviceInfo.SerialArg, _deviceKey, Dispatcher,
                message => SetStatus(message, NormalBrush), concurrency);

            resolver.ItemIconResolved += Resolver_IconResolved;
            resolver.ItemResolved += (target, label, done, total) =>
            {
                target.IsBusy = false;
                if (!string.IsNullOrEmpty(label))
                {
                    target.DisplayName = label;
                    SetStatus("已获取应用名：" + label + "（" + done + "/" + total + "）", OkBrush);
                }
                else
                {
                    SetStatus("未能获取应用名，仍显示包名：" + target.PackageName + "（" + done + "/" + total + "）", ErrorBrush);
                }
            };

            resolver.Start(targets);
        }

        // ------------------------------------------------------------ 行内操作

        /// <summary>
        /// 统一的操作确认：系统应用 / 系统服务会先提示风险并使用警告图标，
        /// 普通应用只显示操作说明。返回 true 表示用户确认继续。
        /// </summary>
        private bool ConfirmAction(AppInfo app, string title, string normal, string risk,
            MessageBoxImage normalIcon = MessageBoxImage.Question)
        {
            bool sensitive = app != null && (app.IsSystem || app.IsService);
            MessageBoxImage icon = sensitive ? MessageBoxImage.Warning : normalIcon;

            return MessageBox.Show(this, BuildConfirmText(app, normal, risk), title,
                MessageBoxButton.OKCancel, icon) == MessageBoxResult.OK;
        }

        /// <summary>
        /// 构造确认文案：系统应用 / 系统服务先说明身份与风险，再写操作本身；
        /// 普通应用只有操作说明。拆成静态方法便于单独验证文案。
        /// </summary>
        internal static string BuildConfirmText(AppInfo app, string normal, string risk)
        {
            if (app == null || (!app.IsSystem && !app.IsService))
            {
                return normal ?? string.Empty;
            }

            StringBuilder text = new StringBuilder();
            text.AppendLine(app.IsService
                ? "注意：【" + app.Title + "】是系统服务（没有桌面入口的预装组件）。"
                : "注意：【" + app.Title + "】是系统应用（随系统预装）。");
            text.AppendLine(risk);
            text.AppendLine();
            text.Append(normal);
            return text.ToString();
        }

        private void ToggleApp_Click(object sender, RoutedEventArgs e)
        {
            AppInfo app = GetApp(sender);
            if (app == null) return;

            ExecuteActions(new[] { app }, AppAction.Toggle);
        }

        private void UninstallApp_Click(object sender, RoutedEventArgs e)
        {
            AppInfo app = GetApp(sender);
            if (app == null) return;

            ExecuteActions(new[] { app }, AppAction.Uninstall);
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            AppInfo app = button == null ? null : button.DataContext as AppInfo;
            if (app == null) return;

            Style itemStyle = TryFindResource("UserMenuItemStyle") as Style;
            ContextMenu menu = new ContextMenu();

            menu.Items.Add(CreateMenuItem("清除数据", itemStyle, () => ClearData(app)));
            menu.Items.Add(CreateMenuItem("强制停止", itemStyle, () => ForceStop(app)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("复制包名", itemStyle, () => CopyText(app.PackageName, "包名")));
            menu.Items.Add(CreateMenuItem("复制 APK 路径", itemStyle, () => CopyText(app.ApkPath, "APK 路径")));
            menu.Items.Add(CreateMenuItem("解析名称", itemStyle, () => ReResolveName(app)));

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ForceStop(AppInfo app)
        {
            ExecuteActions(new[] { app }, AppAction.ForceStop);
        }

        /// <summary>清除全部数据：会连同缓存一起删除，不可撤销。</summary>
        private void ClearData(AppInfo app)
        {
            ExecuteActions(new[] { app }, AppAction.ClearData);
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

        /// <summary>单条/多选统一执行入口：顺序执行、逐条更新行状态、最后汇总结果。</summary>
        private void ExecuteActions(IEnumerable<AppInfo> apps, AppAction action)
        {
            if (_batchBusy || apps == null) return;

            AppInfo[] targets = apps.Where(a => a != null && !a.IsBusy).Distinct().ToArray();
            if (targets.Length == 0)
            {
                SetStatus(apps.Any(a => a != null)
                    ? "所选应用正在处理中，请稍后再试"
                    : "请先选择应用", ErrorBrush);
                return;
            }

            if (!ConfirmTargets(targets, action)) return;

            _batchBusy = true;
            _batchCancelled = false;
            SetBatchBusy(true);

            bool pauseResolving = action == AppAction.ClearData;
            if (pauseResolving) SuspendResolving();

            var failures = new List<string>();
            int successCount = 0;
            int skippedCount = 0;
            string actionName = ActionName(action);
            progressText.Text = actionName + " 0/" + targets.Length;

            Action finish = () =>
            {
                _batchBusy = false;
                SetBatchBusy(false);
                if (pauseResolving) ResumeResolving();

                _view.Refresh();
                UpdateEmptyHint();
                UpdateCounts();
                UpdateSelectionText();
                progressText.Text = string.Empty;

                string summary = actionName + "完成：成功 " + successCount
                    + "，失败 " + failures.Count
                    + (skippedCount > 0 ? "，跳过 " + skippedCount : "");
                if (failures.Count > 0)
                {
                    summary += "。失败：" + string.Join("；", failures);
                    SetStatus(summary, ErrorBrush, false);
                    if (!_batchCancelled)
                    {
                        MessageBox.Show(this, summary, "操作结果",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    SetStatus(summary, OkBrush);
                }
            };

            Action<int> runNext = null;
            runNext = index =>
            {
                if (_batchCancelled || index >= targets.Length)
                {
                    finish();
                    return;
                }

                AppInfo app = targets[index];
                if (app.IsBusy)
                {
                    skippedCount++;
                    runNext(index + 1);
                    return;
                }

                AppActionPlan plan = BuildPlan(app, action);
                if (plan == null)
                {
                    skippedCount++;
                    runNext(index + 1);
                    return;
                }

                app.IsBusy = true;
                progressText.Text = plan.Name + " " + (index + 1) + "/" + targets.Length;

                CmdExecutor.ExecuteCommandAndReturnAsync(new[] { plan.Command },
                    result =>
                    {
                        app.IsBusy = false;
                        string failure = plan.CheckFailure == null ? null : plan.CheckFailure(result);
                        if (failure != null)
                        {
                            failures.Add(app.Title + "：" + failure);
                        }
                        else
                        {
                            successCount++;
                            if (plan.OnSuccess != null) plan.OnSuccess(app);
                        }
                        runNext(index + 1);
                    },
                    err =>
                    {
                        app.IsBusy = false;
                        if (err != null && err.IndexOf("命令执行超时", StringComparison.Ordinal) >= 0)
                        {
                            failures.Add(app.Title + "：命令超时，设备端可能仍在处理");
                        }
                        else
                        {
                            failures.Add(app.Title + "：" + FirstLine(err));
                        }
                        runNext(index + 1);
                        return true;
                    },
                    plan.TimeoutSeconds);
            };

            runNext(0);
        }

        private AppActionPlan BuildPlan(AppInfo app, AppAction action)
        {
            if ((action == AppAction.Disable || action == AppAction.Enable) && app.IsUninstalled)
            {
                return null;
            }

            AppAction effective = action == AppAction.Toggle && app.IsUninstalled
                ? AppAction.Restore
                : action;

            switch (effective)
            {
                case AppAction.Toggle:
                case AppAction.Disable:
                case AppAction.Enable:
                {
                    bool disable = action == AppAction.Disable
                        || (action == AppAction.Toggle && !app.IsDisabled);
                    return new AppActionPlan
                    {
                        App = app,
                        Action = effective,
                        Name = disable ? "停用" : "启用",
                        Command = disable
                            ? Command("shell pm disable-user --user 0 " + app.PackageName)
                            : Command("shell pm enable " + app.PackageName),
                        CheckFailure = CheckPmFailure,
                        TimeoutSeconds = 60,
                        OnSuccess = a => a.IsDisabled = disable
                    };
                }
                case AppAction.Restore:
                    return new AppActionPlan
                    {
                        App = app,
                        Action = effective,
                        Name = "恢复",
                        Command = Command("shell pm install-existing --user 0 " + app.PackageName),
                        CheckFailure = RestoreFailure,
                        TimeoutSeconds = 60,
                        OnSuccess = a =>
                        {
                            a.IsUninstalled = false;
                            a.IsDisabled = false;
                        }
                    };
                case AppAction.Uninstall:
                    return new AppActionPlan
                    {
                        App = app,
                        Action = effective,
                        Name = "卸载",
                        Command = app.IsSystem
                            ? Command("shell pm uninstall --user 0 " + app.PackageName)
                            : Command("uninstall " + app.PackageName),
                        CheckFailure = CheckPmFailure,
                        TimeoutSeconds = 60,
                        OnSuccess = a => _apps.Remove(a)
                    };
                case AppAction.ForceStop:
                    return new AppActionPlan
                    {
                        App = app,
                        Action = effective,
                        Name = "强制停止",
                        Command = Command("shell am force-stop " + app.PackageName),
                        CheckFailure = CheckPmFailure,
                        TimeoutSeconds = 60
                    };
                case AppAction.ClearData:
                    return new AppActionPlan
                    {
                        App = app,
                        Action = effective,
                        Name = "清除数据",
                        Command = Command("shell pm clear " + app.PackageName),
                        CheckFailure = CheckPmFailure,
                        TimeoutSeconds = 180,
                        PauseResolving = true
                    };
            }
            return null;
        }

        private static string RestoreFailure(string result)
        {
            string failure = CheckPmFailure(result);
            if (failure == null) return null;

            if (failure.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "当前系统不支持恢复命令，请用应用商店重新安装";
            }
            if (failure.IndexOf("doesn't exist", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("no such", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "系统里找不到该应用的安装文件，无法恢复，请重新安装";
            }
            return failure;
        }

        private static string ActionName(AppAction action)
        {
            switch (action)
            {
                case AppAction.Disable: return "停用";
                case AppAction.Enable: return "启用";
                case AppAction.Restore: return "恢复";
                case AppAction.Uninstall: return "卸载";
                case AppAction.ForceStop: return "强制停止";
                case AppAction.ClearData: return "清除数据";
                default: return "停用/启用";
            }
        }

        private bool ConfirmTargets(AppInfo[] targets, AppAction action)
        {
            if (targets.Length == 1) return ConfirmSingle(targets[0], action);

            string name = ActionName(action);
            bool sensitive = targets.Any(a => a.IsSystem || a.IsService);
            string text = "确认对选中的 " + targets.Length + " 个应用执行「" + name + "」？\r\n";
            text += sensitive
                ? "其中包含系统应用或系统服务，请确认已了解风险。"
                : "操作将按顺序逐个执行，完成后统一汇总结果。";
            if (action == AppAction.Uninstall || action == AppAction.ClearData)
            {
                text += "\r\n该操作不可撤销。";
            }

            return MessageBox.Show(this, text, name, MessageBoxButton.OKCancel,
                sensitive ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        private bool ConfirmSingle(AppInfo app, AppAction action)
        {
            if (action == AppAction.Toggle && app.IsUninstalled) action = AppAction.Restore;

            switch (action)
            {
                case AppAction.Disable:
                    return ConfirmAction(app, "确认停用",
                        "停用后应用图标将被隐藏且无法启动，用户数据仍保留，可随时重新启用。",
                        "停用系统组件可能导致依赖它的功能异常、桌面或系统界面异常，严重时需要刷机恢复。");
                case AppAction.Enable:
                    return ConfirmAction(app, "确认启用",
                        "启用后应用将恢复可用。",
                        "启用系统组件一般无风险，但可能使之前被有意屏蔽的功能重新生效。");
                case AppAction.Restore:
                    return ConfirmAction(app, "确认恢复",
                        "将重新安装该应用，安装后可以正常使用。",
                        "恢复系统组件一般没有风险，恢复后会重新出现在桌面上。");
                case AppAction.Uninstall:
                    return ConfirmAction(app, "确认卸载",
                        app.IsSystem
                            ? "从手机上移除，系统自带的安装文件仍保留在系统分区。\r\n" +
                              "刷新列表后，可在「已卸载应用」里点「恢复」重新装回来，但账号与数据已被清除。\r\n" +
                              "若只想暂时隐藏并保留数据，建议改用「停用」。"
                            : "将卸载该应用并删除本地数据，不可撤销。",
                        "卸载系统组件可能导致系统功能缺失或桌面异常，之后需要重新安装才能恢复。",
                        MessageBoxImage.Warning);
                case AppAction.ForceStop:
                    return ConfirmAction(app, "确认强制停止",
                        "将结束该应用的进程，正在进行的任务会被中断。",
                        "强制停止系统组件可能导致相关功能短暂异常，部分组件会自动重启。");
                case AppAction.ClearData:
                    return ConfirmAction(app, "确认清除数据",
                        "会删除该应用的账号、登录状态、设置与缓存，不可撤销。",
                        "清除系统组件的数据可能导致系统设置丢失或组件首次启动异常。",
                        MessageBoxImage.Warning);
                default:
                    bool disable = !app.IsDisabled;
                    return ConfirmAction(app,
                        disable ? "确认停用" : "确认启用",
                        disable
                            ? "停用后应用图标将被隐藏且无法启动，用户数据仍保留，可随时重新启用。"
                            : "启用后应用将恢复可用。",
                        disable
                            ? "停用系统组件可能导致依赖它的功能异常、桌面或系统界面异常，严重时需要刷机恢复。"
                            : "启用系统组件一般无风险，但可能使之前被有意屏蔽的功能重新生效。");
            }
        }

        private void SetBatchBusy(bool busy)
        {
            stopBatchButton.IsEnabled = true;
            stopBatchButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            foreach (UIElement child in selectionButtonPanel.Children)
            {
                child.IsEnabled = !busy;
            }
            foreach (UIElement child in batchButtonPanel.Children)
            {
                child.IsEnabled = !busy;
            }
            refreshButton.IsEnabled = !busy && !_loading;
        }

        private AppInfo[] GetSelectedApps()
        {
            return _apps.Where(a => a != null && a.IsSelected).ToArray();
        }

        private void UpdateSelectionText()
        {
            selectionText.Text = "已选 " + GetSelectedApps().Length + " 项";
        }

        private void Selection_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSelectionText();
        }

        private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;

            _syncingSelection = true;
            foreach (AppInfo app in e.RemovedItems.OfType<AppInfo>())
            {
                app.IsSelected = false;
            }
            foreach (AppInfo app in e.AddedItems.OfType<AppInfo>())
            {
                app.IsSelected = true;
            }
            _syncingSelection = false;
            UpdateSelectionText();
        }

        private void SyncListSelectionFromModel()
        {
            _syncingSelection = true;
            appList.SelectedItems.Clear();
            foreach (AppInfo app in _view)
            {
                if (app.IsSelected) appList.SelectedItems.Add(app);
            }
            _syncingSelection = false;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_batchBusy) return;
            foreach (AppInfo app in _view) app.IsSelected = true;
            SyncListSelectionFromModel();
            UpdateSelectionText();
        }

        private void InvertSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_batchBusy) return;
            foreach (AppInfo app in _view) app.IsSelected = !app.IsSelected;
            SyncListSelectionFromModel();
            UpdateSelectionText();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_batchBusy) return;
            foreach (AppInfo app in _apps) app.IsSelected = false;
            SyncListSelectionFromModel();
            UpdateSelectionText();
        }

        private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            CheckBox box = sender as CheckBox;
            AppInfo app = box == null ? null : box.DataContext as AppInfo;
            if (app == null) return;

            _syncingSelection = true;
            if (app.IsSelected)
            {
                if (!appList.SelectedItems.Contains(app)) appList.SelectedItems.Add(app);
            }
            else if (appList.SelectedItems.Contains(app))
            {
                appList.SelectedItems.Remove(app);
            }
            _syncingSelection = false;
            UpdateSelectionText();
        }

        private void BatchDisable_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActions(GetSelectedApps(), AppAction.Disable);
        }

        private void BatchEnable_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActions(GetSelectedApps(), AppAction.Enable);
        }

        private void BatchUninstall_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActions(GetSelectedApps(), AppAction.Uninstall);
        }

        private void BatchForceStop_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActions(GetSelectedApps(), AppAction.ForceStop);
        }

        private void BatchClearData_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActions(GetSelectedApps(), AppAction.ClearData);
        }

        private void BatchReResolve_Click(object sender, RoutedEventArgs e)
        {
            if (_batchBusy) return;

            AppInfo[] targets = GetSelectedApps();
            if (targets.Length == 0)
            {
                SetStatus("请先选择应用", ErrorBrush);
                return;
            }

            ReResolveNames(targets);
            SetStatus("正在解析 " + targets.Length + " 个应用名…", NormalBrush, false);
        }

        private void StopBatch_Click(object sender, RoutedEventArgs e)
        {
            if (!_batchBusy) return;

            _batchCancelled = true;
            stopBatchButton.IsEnabled = false;
            SetStatus("正在停止操作，当前命令执行完后不再继续…", NormalBrush, false);
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
            if (_filterIndex == 5 && !app.IsUninstalled) return false;
            if (_filterIndex == 6 && !app.IsDebug) return false;
            if (_filterIndex == 7 && app.IsDebug) return false;

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
            int uninstalled = 0;
            foreach (AppInfo app in _apps)
            {
                if (app.IsSystem) system++;
                if (app.IsDisabled) disabled++;
                if (app.IsService) service++;
                if (app.IsUninstalled) uninstalled++;
            }

            string text = string.Format("共 {0} 个应用 · 第三方 {1} · 系统 {2} · 系统服务 {3} · 已停用 {4} · 已卸载 {5}",
                _apps.Count, _apps.Count - system, system, service, disabled, uninstalled);

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
            UpdateSelectionText();
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
