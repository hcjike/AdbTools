using AdbTools.AccessInterface;
using AdbTools.bean;
using AdbTools.FileBrowser;
using AdbTools.RemoteControl;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace AdbTools
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public static RoutedCommand CommandF5 = new RoutedCommand();
        public static RoutedCommand CommandF8 = new RoutedCommand();


        public static string adbPath = "";
        private ObservableCollection<string> deviceAddressHistory;
        private ObservableCollection<Device> deviceAddressList;

        /// <summary>定时刷新设备列表的计时器（间隔为0时不运行）</summary>
        private DispatcherTimer refreshTimer;

        /// <summary>当前定时刷新间隔（秒），0表示关闭</summary>
        private int refreshIntervalSeconds;

        /// <summary>是否有刷新命令正在执行，避免定时刷新与手动刷新叠加</summary>
        private bool refreshRunning;

        /// <summary>定时刷新选项的延迟收起计时器（避免鼠标经过间隙时误收起）</summary>
        private DispatcherTimer hideIntervalTimer;

        /// <summary>关于菜单中仓库入口卡片的延迟收起计时器</summary>
        private DispatcherTimer hideRepoMenuTimer;

        /// <summary>是否正在后台下载更新（避免重复触发下载）</summary>
        private bool updateDownloading;

        /// <summary>更新包下载进度（百分比）</summary>
        private int updateDownloadPercent;

        /// <summary>已打开的应用列表窗口（按设备标识索引，同一设备只允许一个实例）</summary>
        private readonly Dictionary<string, AppListWindow> appListWindows = new Dictionary<string, AppListWindow>();

        /// <summary>设备列表是否已完成首次刷新：首次刷新只建立基线，不弹“设备已连接”通知。</summary>
        private bool deviceListInitialized;

        public MainWindow()
        {
            InitializeComponent();

            deviceAddressHistory = new ObservableCollection<string>();
            deviceAddressList = new ObservableCollection<Device>();

            // 创建 CommandBinding 并绑定到处理程序
            CommandBinding commandF5Binding = new CommandBinding(
                CommandF5,
                CommandF5_Executed,
                CommandF5_CanExecute);

            CommandBinding commandF8Binding = new CommandBinding(
                CommandF8,
                CommandF8_Executed,
                CommandF8_CanExecute);


            this.CommandBindings.Add(commandF5Binding);
            this.CommandBindings.Add(commandF8Binding);

        }

        private void CommandF5_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Timer_Tick(null, null);
        }

        private void CommandF5_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true; // 始终可以执行
        }

        private void CommandF8_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            pairDevice_Click(null, null);
        }

        private void CommandF8_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true; // 始终可以执行
        }

        private void refreshDeviceAddressHistory()
        {
            deviceAddressHistory.Clear();
            foreach (string address in Globals.AppSettings.DEVICE_ADDRESS_HISTORY)
            {
                deviceAddressHistory.Add(address);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Title = $"{this.Title} V{App.Version}";

            // adb相关资源文件
            string adbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
            string adbDll1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdbWinApi.dll");
            string adbDll2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdbWinUsbApi.dll");
            if (!File.Exists(adbFilePath) || !File.Exists(adbDll1) || !File.Exists(adbDll2))
            {
                MessageBox.Show("adb工具集丢失，无法运行！", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
                return;
            }

            // 配置文件中未设置代理时，将默认代理写入配置
            if (!Globals.AppSettings.IS_GITHUB_PROXY)
            {
                Globals.AppSettings.GITHUB_PROXY = Globals.AppSettings.GITHUB_PROXY;
            }

            RequestJson.UpdateCheck(this, updateVersion);

            adbPath = $"\"{adbFilePath}\"";

            // adb 31.0.2+ 默认禁用 mDNS 发现，而扫码配对依赖 mDNS 发现手机广播的配对服务。
            // 仅设置环境变量（仅影响本进程之后启动的 adb server，不触碰已在运行的可用 server，避免破坏原环境）。
            // 旧版 adb 忽略此变量，安全。
            try
            {
                Environment.SetEnvironmentVariable("ADB_MDNS_OPENSCREEN", "1");
            }
            catch
            {
                // 设置失败不影响其它功能
            }

            deviceAddress.Text = Globals.AppSettings.LAST_DEVICE_ADDRESS;
            deviceAddress.ItemsSource = deviceAddressHistory;
            refreshDeviceAddressHistory();

            deviceList.ItemsSource = deviceAddressList;

            // 定时刷新计时器：恢复上次设置的间隔（配置缺失或非法时默认关闭）
            refreshTimer = new DispatcherTimer();
            refreshTimer.Tick += AutoRefresh_Tick;

            // 选项卡片的延迟收起计时器
            hideIntervalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            hideIntervalTimer.Tick += hideIntervalTimer_Tick;

            // 关于菜单中仓库入口卡片的延迟收起计时器
            hideRepoMenuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            hideRepoMenuTimer.Tick += hideRepoMenuTimer_Tick;
            refreshIntervalSeconds = Globals.AppSettings.REFRESH_INTERVAL;
            if (!IsValidRefreshInterval(refreshIntervalSeconds))
            {
                refreshIntervalSeconds = 0;
            }
            ApplyRefreshInterval(false);
            UpdateTopmostUi();

            // 托盘图标：关闭窗口后继续后台运行，右键菜单提供常用操作与退出入口。
            // 放到首帧渲染之后再创建，避免它的初始化（图标解码、菜单构建）拖慢窗口出现。
            Dispatcher.BeginInvoke(new Action(App.InitializeTray), DispatcherPriority.ApplicationIdle);
            App.UpdateTrayStatus(deviceAddressList.Count);

            Timer_Tick(null, null);

            // 调试入口：命令行带 --remote=<序列号> 时直接打开远程控制窗口
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                const string remoteFlag = "--remote=";
                if (arg.StartsWith(remoteFlag, StringComparison.OrdinalIgnoreCase))
                {
                    OpenRemoteControl(arg.Substring(remoteFlag.Length));
                    break;
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 第二个实例通过广播消息唤出已经完全隐藏到托盘的主窗口
            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        /// <summary>处理第二个实例发来的“显示主窗口”广播消息。</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            int showMessage = App.ShowMainWindowMessage;
            if (showMessage != 0 && msg == showMessage)
            {
                App.ShowMainWindow();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 关闭主界面：默认收起到系统托盘继续后台运行（首次给一次气泡提示）；
        /// 设置里关掉“最小化到托盘”后才走退出确认。真正退出统一走托盘菜单。
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (App.ExitRequested) return;

            if (Globals.AppSettings.CLOSE_TO_TRAY)
            {
                e.Cancel = true;
                Hide();

                if (!Globals.AppSettings.TRAY_TIP_SHOWN)
                {
                    Globals.AppSettings.TRAY_TIP_SHOWN = true;
                    App.Notify("程序已最小化到系统托盘，继续在后台刷新设备。\r\n右键托盘图标可退出。");
                }
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                this,
                "确定要退出 Adb 连接工具吗？\n\n已打开的远程控制与应用列表窗口会一并关闭。",
                "退出确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }

        /// <summary>
        /// 主窗口真正关闭后的收尾：应用用的是 OnExplicitShutdown，
        /// 关掉窗口不会自动结束进程，所以在“直接退出程序”模式下要显式走统一退出流程，
        /// 否则关掉窗口后进程和托盘图标还留着，看起来像“关不掉”。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (!App.ExitRequested && !Globals.AppSettings.CLOSE_TO_TRAY)
            {
                App.ExitApplication();
            }
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            refreshTimer?.Stop();

            // 关闭所有远程控制窗口（按序列号登记），释放 adb 取流与解码资源
            RemoteControlWindow.CloseAll();

            // 文件浏览窗口一并关闭（进行中的传输会在窗口关闭时取消）
            FileBrowserWindow.CloseAll();

            // 应用列表是独立窗口，主界面退出时一并关闭，避免后台解析继续跑
            foreach (var window in appListWindows.Values.ToList())
            {
                try { window.Close(); } catch { }
            }
            appListWindows.Clear();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            RefreshDevices(true);
        }

        /// <summary>定时刷新触发（静默刷新，不显示加载动画）</summary>
        private void AutoRefresh_Tick(object sender, EventArgs e)
        {
            RefreshDevices(false);
        }

        /// <summary>
        /// 执行一次设备列表刷新；上一次刷新未结束时跳过，避免命令堆积
        /// </summary>
        /// <param name="showAnimation">是否显示加载动画（定时刷新不显示，避免频繁闪烁）</param>
        private void RefreshDevices(bool showAnimation)
        {
            if (refreshRunning)
            {
                return;
            }
            refreshRunning = true;
            if (showAnimation)
            {
                waitAnimation(true);
            }
            // 带 -l 取 product/model 字段：设备一出现就能显示型号，不必等 getprop 返回
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} devices -l", content =>
            {
                refreshRunning = false;
                RefreshDeviceList(content);
                if (showAnimation)
                {
                    waitAnimation(false);
                }
            });
        }

        /// <summary>从 "product:xx model:yy device:zz" 里取指定前缀的值，取不到返回 null。</summary>
        private static string ExtractField(string fields, string prefix)
        {
            if (string.IsNullOrEmpty(fields))
            {
                return null;
            }

            foreach (string part in fields.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return part.Substring(prefix.Length);
                }
            }
            return null;
        }

        /// <summary>
        /// 根据 adb devices 输出刷新设备列表（保持adb返回顺序，新设备插入到对应位置）
        /// </summary>
        private void RefreshDeviceList(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                // 命令执行失败或被中断时保留当前列表，避免误清空
                // （正常执行时至少有cmd横幅与"List of devices attached"输出，不会为空）
                return;
            }

            int index = 0;
            List<string> deviceMarks = new List<string>();

            // 记下刷新前的设备，用于判断谁上线/下线
            Dictionary<string, string> beforeNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Device exist in deviceAddressList)
            {
                beforeNames[exist.DeviceMark] = exist.ShowDeviceName;
            }

            foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.TrimEnd();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // adb devices -l 的格式：<序列号> <状态> [product:.. model:.. device:.. transport_id:..]
                // Windows 下 adb 用空格分隔（不是 tab），且序列号本身可能含空格（mDNS 连接名），
                // 所以从行尾剥掉 "key:value" 附加字段：剩下的最后一个是状态，之前的都是序列号。
                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                int end = parts.Length - 1;
                while (end >= 0 && parts[end].IndexOf(':') > 0)
                {
                    end--;
                }

                if (end < 1)
                {
                    continue; // 既没有序列号也没有状态，多半是 cmd 回显行
                }

                // 跳过 unauthorized、offline、no permissions 等非就绪状态
                if (!string.Equals(parts[end], "device", StringComparison.Ordinal))
                {
                    continue;
                }

                string address = string.Join(" ", parts, 0, end);
                if (address.Length == 0)
                {
                    continue;
                }

                string props = end + 1 < parts.Length
                    ? string.Join(" ", parts, end + 1, parts.Length - end - 1)
                    : string.Empty;
                string model = ExtractField(props, "model:");
                deviceMarks.Add(address);

                Device exist = deviceAddressList.FirstOrDefault(o => o.DeviceMark == address);
                if (exist == null)
                {
                    deviceAddressList.Insert(index, new Device
                    {
                        IsWifiConnect = Globals.IsValidIPEndPoint(address),
                        DeviceMark = address,
                        // 先用 devices -l 里的型号顶上，后台再用 getprop 补品牌
                        DeviceModel = model,
                        DeviceInfo = string.IsNullOrEmpty(model) ? Device.UnknownName : model,
                        ShowDeviceName = string.IsNullOrEmpty(model) ? Device.UnknownName : model
                    });
                }
                else
                {
                    int oldIndex = deviceAddressList.IndexOf(exist);
                    if (oldIndex != index)
                    {
                        deviceAddressList.Move(oldIndex, index);
                    }
                }
                index++;
            }

            // 移除已不存在的设备
            foreach (Device device in deviceAddressList.Where(o => !deviceMarks.Contains(o.DeviceMark)).ToList())
            {
                deviceAddressList.Remove(device);
            }

            foreach (Device device in deviceAddressList)
            {
                device.Refresh();
            }

            // 空列表时显示提示（HasItems不发送更改通知，需手动同步）
            emptyHint.Visibility = deviceAddressList.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            // 托盘提示与设备上下线通知
            App.UpdateTrayStatus(deviceAddressList.Count);
            NotifyDeviceChanges(beforeNames, deviceMarks);
        }

        /// <summary>
        /// 比对刷新前后的设备列表，把上线/下线用托盘通知提示出来。
        /// 首次刷新只建立基线，避免启动时把已有设备全部报一遍。
        /// </summary>
        private void NotifyDeviceChanges(Dictionary<string, string> beforeNames, List<string> afterMarks)
        {
            if (!deviceListInitialized)
            {
                deviceListInitialized = true;
                return;
            }
            // 三类通知各自受设置项控制：设备上下线、远程控制断开
            bool notifyDevice = Globals.AppSettings.NOTIFY_DEVICE_CHANGE;
            bool notifyRemote = Globals.AppSettings.NOTIFY_REMOTE_DISCONNECT;
            if (!notifyDevice && !notifyRemote) return;

            HashSet<string> now = new HashSet<string>(afterMarks, StringComparer.Ordinal);

            if (notifyDevice)
            {
                foreach (string mark in afterMarks)
                {
                    if (beforeNames.ContainsKey(mark)) continue;

                    Device device = deviceAddressList.FirstOrDefault(d => d.DeviceMark == mark);
                    string name = device != null ? device.DeviceInfo : mark;
                    App.Notify("设备已连接\r\n" + name + "\r\n" + mark);
                }
            }

            // 全部设备一起消失多半是 adb 服务重启或刷新时的瞬时状态，不弹断线提示，避免刷屏误报
            if (afterMarks.Count == 0) return;

            foreach (KeyValuePair<string, string> pair in beforeNames)
            {
                if (now.Contains(pair.Key)) continue;

                string name = string.IsNullOrEmpty(pair.Value) ? pair.Key : pair.Value;
                if (RemoteControlWindow.IsOpenFor(pair.Key))
                {
                    // 掉线打断了正在看的远程控制：优先用专门的提示
                    if (notifyRemote)
                    {
                        App.Notify("远程控制已断开\r\n" + name + "\r\n设备连接已中断", BalloonIcon.Error);
                    }
                    else if (notifyDevice)
                    {
                        App.Notify("设备已断开\r\n" + name + "\r\n" + pair.Key);
                    }
                }
                else if (notifyDevice)
                {
                    App.Notify("设备已断开\r\n" + name + "\r\n" + pair.Key);
                }
            }
        }

        private void connectDevice_Click(object sender, RoutedEventArgs e)
        {
            string address = deviceAddress.Text.Replace("：", ":");
            deviceAddress.Text = address;
            if (!Globals.IsValidIPEndPoint(address))
            {
                MessageBox.Show("设备连接地址输入错误！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} connect {address}", result =>
            {
                if (string.IsNullOrWhiteSpace(result) || Globals.ContainsAny(result, "(10060)", "(10061)", "failed to connect"))
                {
                    waitAnimation(false);
                    MessageBox.Show("设备连接失败！\r\n1.请检查IP和端口输入是否正确；\r\n2.设备与电脑是否完成配对。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Globals.AppSettings.LAST_DEVICE_ADDRESS = address;

                string ip = address.Split(':')[0];
                List<string> history = Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Where(item => !item.StartsWith(ip)).ToList();
                history.Insert(0, address);
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY = history;
                refreshDeviceAddressHistory();
                deviceAddress.Text = Globals.AppSettings.LAST_DEVICE_ADDRESS;

                Timer_Tick(null, null);
                waitAnimation(false);

            }, err =>
            {
                waitAnimation(false);
                MessageBox.Show("设备连接失败！\r\n1.请检查IP和端口输入是否正确；\r\n2.设备与电脑是否完成配对。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            });
        }

        private void pairDevice_Click(object sender, RoutedEventArgs e)
        {
            PairWindow pairWindow = new PairWindow()
            {
                AdbPath = adbPath,
                Address = deviceAddress.Text,
            };
            pairWindow.Owner = this;
            pairWindow.ShowDialog();
            if (pairWindow.PairSucceeded)
            {
                // 配对成功后自动连接设备并刷新列表
                refreshList_Click(null, null);
            }
        }

        private string[] SelectApkFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                // 只认标准 APK 与分批改名文件（xxx.apk.1），不提供"所有文件"
                Filter = "APK 文件 (*.apk;*.apk.*)|*.apk;*.apk.*",
                FilterIndex = 1,
                Multiselect = true,
                RestoreDirectory = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // 获取选中的文件路径
                return openFileDialog.FileNames;
            }
            return null;
        }

        private void installApkItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            string[] paths = SelectApkFile();
            if (paths == null || paths.Length == 0)
            {
                return;
            }
            HandleApkInstallation(new List<string>(paths), 0, device);
        }

        /// <summary>
        /// 远程控制：打开独立窗口实时显示设备画面，并把鼠标/键盘操作注入设备。
        /// Android 7.0（SDK 24）以下不支持 screenrecord 输出到 stdout，直接提示。
        /// </summary>
        private void remoteControlItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            OpenRemoteControl(device.DeviceMark, device.HardwareKey);
        }

        /// <summary>文件管理：打开该设备的文件浏览窗口（同一设备只保留一个）。</summary>
        private void fileManagerItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            OpenFileBrowser(device.DeviceMark, device.ShowDeviceName);
        }

        /// <summary>按设备序列号打开（或激活）文件浏览窗口。</summary>
        private void OpenFileBrowser(string deviceMark, string deviceName)
        {
            if (string.IsNullOrEmpty(deviceMark)) return;

            if (FileBrowserWindow.TryActivate(deviceMark)) return;

            var window = new FileBrowserWindow(adbPath.Trim('"'), deviceMark, deviceName);
            window.Show();
        }

        /// <summary>
        /// 按设备序列号打开（或激活）远程控制窗口。
        /// 同一设备只允许一个窗口：已有活动窗口时直接激活；
        /// 已关闭（含后台清理尚未完成）的窗口视为已关闭，可以正常重新打开。
        /// deviceKey 为硬件序列号，用于保存窗口尺寸；命令行启动时没有设备对象，退回连接序列号。
        /// </summary>
        private void OpenRemoteControl(string deviceMark, string deviceKey = null)
        {
            if (string.IsNullOrEmpty(deviceMark))
            {
                return;
            }

            // 按序列号查已有窗口（登记表在 RemoteControlWindow 内部，线程安全）
            if (RemoteControlWindow.TryActivate(deviceMark))
            {
                return;
            }

            string adbExe = adbPath.Trim('"');

            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} -s {Globals.QuoteSerial(deviceMark)} shell getprop ro.build.version.sdk", result =>
            {
                int sdk = ParseSdkVersion(result);
                if (sdk > 0 && sdk < 24)
                {
                    MessageBox.Show("当前设备系统版本过低（需要 Android 7.0 及以上），暂不支持远程控制。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 查询 SDK 期间可能已经打开了同一设备的窗口，这里再确认一次
                if (RemoteControlWindow.TryActivate(deviceMark))
                {
                    return;
                }

                var window = new RemoteControlWindow(adbExe, deviceMark, "720x1280", 8000000, deviceKey);
                window.Show();
            });
        }

        /// <summary>从 cmd 回显中解析 SDK 版本号（取第一行可解析为整数的内容）。</summary>
        private static int ParseSdkVersion(string output)
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

        private void disconnectDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }

            if (MessageBoxResult.OK != MessageBox.Show($"确定要断开设备【{device.ShowDeviceName}】的连接吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} disconnect {device.SerialArg}", result =>
            {
                Timer_Tick(null, null);
                waitAnimation(false);
            });
        }

        private void disconnectAllDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxResult.OK == MessageBox.Show("确定要【断开所有设备】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                waitAnimation(true);
                CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} disconnect", result =>
                {
                    Timer_Tick(null, null);
                    waitAnimation(false);
                });
            }
        }

        private void deleteHistory_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var contextMenu = (ContextMenu)menuItem.Parent;
            var textBlock = (TextBlock)contextMenu.PlacementTarget;
            if (MessageBoxResult.OK != MessageBox.Show($"确定要删除记录【{textBlock.Text}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Remove(textBlock.Text);
            Globals.AppSettings.DEVICE_ADDRESS_HISTORY = Globals.AppSettings.DEVICE_ADDRESS_HISTORY;
            refreshDeviceAddressHistory();
        }

        private void deleteAllHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxResult.OK == MessageBox.Show("确定要【清空所有历史纪录】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Clear();
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY = Globals.AppSettings.DEVICE_ADDRESS_HISTORY;
                refreshDeviceAddressHistory();
            }
        }

        private void waitAnimation(bool show)
        {
            processBarGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            waitProcessBar.IsShowAnimation = show;
        }

        private void refreshList_Click(object sender, RoutedEventArgs e)
        {
            Timer_Tick(null, null);
        }

        #region 《托盘菜单》

        /// <summary>托盘菜单：刷新设备列表（主窗口隐藏时同样生效）。</summary>
        internal void TrayRefreshDevices()
        {
            RefreshDevices(false);
        }

        /// <summary>托盘菜单使用的设备快照：复制一份，避免菜单操作影响列表本身。</summary>
        internal List<Device> GetTrayDevices()
        {
            return deviceAddressList.ToList();
        }

        internal void TrayOpenRemoteControl(Device device)
        {
            if (device == null) return;
            OpenRemoteControl(device.DeviceMark, device.HardwareKey);
        }

        internal void TrayOpenAppList(Device device)
        {
            OpenAppList(device);
        }

        internal void TrayOpenShell(Device device)
        {
            OpenShell(device);
        }

        internal void TrayOpenFileBrowser(Device device)
        {
            if (device == null) return;
            OpenFileBrowser(device.DeviceMark, device.ShowDeviceName);
        }

        /// <summary>托盘菜单：断开全部设备（菜单点击本身已是一次明确操作，不再二次确认）。</summary>
        internal void TrayDisconnectAll()
        {
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} disconnect", result =>
            {
                RefreshDevices(false);
            });
        }

        /// <summary>托盘菜单：检查更新，发现新版本时弹更新窗口。</summary>
        internal void TrayCheckUpdate()
        {
            // 更新窗口以主窗口为 Owner，先把主窗口显示出来，避免对话框挂在隐藏窗口上
            App.ShowMainWindow();
            RequestJson.UpdateCheck(this, githubReleases => UpdateVersion(githubReleases));
        }

        #endregion

        /// <summary>
        /// 获取当前选中的设备，未选中时提示并返回null
        /// </summary>
        private Device GetSelectedDevice()
        {
            int index = deviceList.SelectedIndex;
            if (index < 0 || index >= deviceAddressList.Count)
            {
                MessageBox.Show("请先选择一个设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return deviceAddressList[index];
        }

        /// <summary>
        /// 对选中设备执行设置类命令（统一确认、执行、成功/失败提示流程）
        /// </summary>
        /// <param name="actionName">操作名称，如“恢复默认DPI”</param>
        /// <param name="commandFactory">根据设备生成要执行的命令</param>
        /// <param name="failTip">失败提示的补充说明</param>
        private void ExecuteDeviceSetting(string actionName, Func<Device, string> commandFactory, string failTip)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            if (MessageBoxResult.OK != MessageBox.Show($"确定要对设备【{device.ShowDeviceName}】执行【{actionName}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { commandFactory(device) },
                result =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{actionName}成功", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                },
                err =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{actionName}失败！\r\n{failTip}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
        }

        private void resetPortItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认TCP端口(5555)", d => $"{adbPath} -s {d.SerialArg} tcpip 5555", "检查设备是否已离线。");
        }

        private void resetPXItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认分辨率", d => $"{adbPath} -s {d.SerialArg} shell wm size reset", "检查设备是否已离线。");
        }

        private void resetDPIItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认DPI", d => $"{adbPath} -s {d.SerialArg} shell wm density reset", "检查设备是否已离线。");
        }

        private void logcatItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            // SerialArg 已带引号，这里必须传原始序列号，否则会变成 ""序列号"" 导致 adb 找不到设备
            new DeviceLogWindow(adbPath, device.DeviceMark, device.DeviceInfo).Show();
        }

        private void logcatDebugItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            new DeviceLogWindow(adbPath, device.DeviceMark, device.DeviceInfo, "DEBUG").Show();
        }

        private void captureCrashItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null) return;

            waitAnimation(true);
            LogCollector.CaptureCrashAsync(adbPath, device.DeviceMark, device.DeviceInfo,
                (ok, error, path) =>
                {
                    waitAnimation(false);
                    if (!ok)
                    {
                        MessageBox.Show("抓取崩溃日志失败：" + error, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    ShowCollectedResult("崩溃日志已保存：", path);
                });
        }

        private void captureBundleItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null) return;

            waitAnimation(true);
            LogCollector.CaptureBundleAsync(adbPath, device.DeviceMark, device.DeviceInfo,
                (ok, error, path) =>
                {
                    waitAnimation(false);
                    if (!ok)
                    {
                        MessageBox.Show("抓取诊断包失败：" + error, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    ShowCollectedResult("诊断包已保存：", path);
                });
        }

        private void openLogDirItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            string dir = device != null
                ? LogCollector.GetDeviceLogDirectory(device.DeviceInfo)
                : Globals.DeviceLogsDirectory;

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowCollectedResult(string title, string path)
        {
            string message = title + "\r\n" + path + "\r\n\r\n是否打开所在位置？";
            if (MessageBoxResult.Yes == MessageBox.Show(message, "完成",
                MessageBoxButton.YesNo, MessageBoxImage.Information))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch
                {
                    // 打开失败不影响文件本身
                }
            }
        }

        private void openShell_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            OpenShell(device);
        }

        /// <summary>为指定设备打开系统命令行 Shell（主界面与托盘菜单共用）。</summary>
        private void OpenShell(Device device)
        {
            if (device == null) return;
            CmdExecutor.ExecuteCommandByShell($"{adbPath} -s {device.SerialArg} shell ");
        }

        #region 《版本更新》

        private void updateVersion_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                GithubReleases githubReleases = (GithubReleases)updateVersion.Tag;
                UpdateVersion(githubReleases);
            }
        }

        private void UpdateVersion(GithubReleases githubReleases)
        {
            if (githubReleases == null)
            {
                return;
            }

            // 升级改为下载 Releases 中的安装程序附件（兼容 AdbToolsInstaller.exe 与 AdbToolsInstaller-1.5.2.exe）
            GithubReleasesAssets asset = githubReleases.Assets?
                .FirstOrDefault(a => RequestJson.IsInstallerAsset(a.Name));
            if (asset == null || string.IsNullOrEmpty(asset.browser_download_url))
            {
                MessageBox.Show("该版本未提供安装程序附件，无法自动更新。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            updateVersion.Visibility = Visibility.Visible;
            updateVersion.Tag = githubReleases;

            // 已在后台下载中：不弹更新窗口，直接提示进度即可
            if (updateDownloading)
            {
                MessageBox.Show($"正在后台下载更新… {updateDownloadPercent}%，请稍候。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            updateVersion.ToolTip = "发现新版本，点击更新";

            // 新版本提示：可立即更新，也可直接打开该版本的 Release 页面查看更新说明
            UpdateWindow dialog = new UpdateWindow(githubReleases.tag_name,
                RequestJson.GetReleasePageUrl(githubReleases.tag_name))
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || !dialog.StartUpdate)
            {
                return;
            }

            // 下载到 %APPDATA%\AdbTools\cache：与其它临时文件统一管理，
            // 每次启动由 Globals.CleanupCacheDirectory 清空
            string targetPath = Globals.GetCacheFilePath(
                "AdbToolsInstaller_" + githubReleases.tag_name + ".exe");

            // 后台下载：不禁用窗口、不改光标、不占用标题，仅在更新图标上提示进度，
            // 避免网络较差时界面看起来像卡死。下载完成后的启动安装流程保持不变。
            updateDownloading = true;
            updateDownloadPercent = 0;
            updateVersion.ToolTip = "正在下载更新… 0%";

            WebClient client = new WebClient();
            client.Headers.Add("User-Agent", "AdbTools");

            client.DownloadProgressChanged += (s, e) =>
            {
                updateDownloadPercent = e.ProgressPercentage;
                updateVersion.ToolTip = "正在下载更新… " + e.ProgressPercentage + "%";
            };

            client.DownloadFileCompleted += (s, e) =>
            {
                client.Dispose();
                updateDownloading = false;
                updateVersion.ToolTip = "发现新版本，点击更新";

                if (e.Error != null)
                {
                    MessageBox.Show("下载更新失败：" + e.Error.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    // 启动安装程序；安装程序会主动结束正在运行的 AdbTools/Update 进程后再覆盖安装
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动安装程序失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 安装更新属于程序主动退出：统一退出流程（释放托盘、清理远程窗口）
                App.ExitApplication();
            };

            try
            {
                client.DownloadFileAsync(new Uri(RequestJson.GetDownloadUrl(asset.browser_download_url)), targetPath);
            }
            catch (Exception ex)
            {
                client.Dispose();
                updateDownloading = false;
                updateVersion.ToolTip = null;
                MessageBox.Show("下载更新失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 《关于菜单及控制》

        private void about_Click(object sender, RoutedEventArgs e)
        {
            // 每次打开都回到默认状态：右侧仓库入口收起
            HideRepoMenuColumn();
            about_popup.IsOpen = true;
        }

        /// <summary>关闭关于菜单时收起右侧仓库入口，下次打开回到默认状态</summary>
        private void about_popup_Closed(object sender, EventArgs e)
        {
            HideRepoMenuColumn();
        }

        /// <summary>鼠标进入「开源仓库」项或右侧仓库卡片时展开仓库入口</summary>
        private void repoMenu_MouseEnter(object sender, MouseEventArgs e)
        {
            hideRepoMenuTimer?.Stop();
            repoMenuColumn.Visibility = Visibility.Visible;
        }

        /// <summary>鼠标离开时延迟判断，避免在菜单与卡片之间的间隙被误收起</summary>
        private void repoMenu_MouseLeave(object sender, MouseEventArgs e)
        {
            hideRepoMenuTimer?.Stop();
            hideRepoMenuTimer?.Start();
        }

        /// <summary>延迟收起：鼠标已不在菜单项和仓库卡片上时才真正收起</summary>
        private void hideRepoMenuTimer_Tick(object sender, EventArgs e)
        {
            hideRepoMenuTimer.Stop();
            if (!repoMenuBtn.IsMouseOver && !repoMenuColumn.IsMouseOver)
            {
                HideRepoMenuColumn();
            }
        }

        /// <summary>收起右侧的仓库入口卡片</summary>
        private void HideRepoMenuColumn()
        {
            hideRepoMenuTimer?.Stop();
            repoMenuColumn.Visibility = Visibility.Collapsed;
        }

        private void projectAddress_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            CmdExecutor.StartExe("https://github.com/hcjike/AdbTools");
        }


        private void projectAddressMirror_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            CmdExecutor.StartExe("https://code.hcjike.com/hcjike/adbtools");
        }

        private void cheackUpdate_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            RequestJson.UpdateCheck(this, githubReleases =>
            {
                UpdateVersion(githubReleases);
            });
        }

        #endregion

        #region 《设置菜单及控制》

        private void setup_Click(object sender, RoutedEventArgs e)
        {
            // 每次打开都回到默认状态：右侧二级菜单全部收起
            HideOptionColumns();
            UpdateNotifyUi();
            UpdateExitModeUi();
            setup_popup.IsOpen = true;
        }

        /// <summary>关闭设置菜单时收起所有二级菜单，下次打开回到默认状态</summary>
        private void setup_popup_Closed(object sender, EventArgs e)
        {
            HideOptionColumns();
        }

        /// <summary>通知选项：多选，点一次切换一次并立即写入配置；二级菜单保持展开便于连续勾选。</summary>
        private void notifyOption_Click(object sender, RoutedEventArgs e)
        {
            Button item = sender as Button;
            if (item == null) return;

            switch (item.Tag as string)
            {
                case "device":
                    Globals.AppSettings.NOTIFY_DEVICE_CHANGE = !Globals.AppSettings.NOTIFY_DEVICE_CHANGE;
                    break;
                case "install":
                    Globals.AppSettings.NOTIFY_INSTALL_RESULT = !Globals.AppSettings.NOTIFY_INSTALL_RESULT;
                    break;
                case "remote":
                    Globals.AppSettings.NOTIFY_REMOTE_DISCONNECT = !Globals.AppSettings.NOTIFY_REMOTE_DISCONNECT;
                    break;
            }
            UpdateNotifyUi();
        }

        /// <summary>关闭窗口时的行为：单选，选完即收起菜单。</summary>
        private void exitMode_Click(object sender, RoutedEventArgs e)
        {
            Button item = sender as Button;
            if (item == null) return;

            Globals.AppSettings.CLOSE_TO_TRAY = string.Equals(item.Tag as string, "tray", StringComparison.Ordinal);
            UpdateExitModeUi();

            HideOptionColumns();
            setup_popup.IsOpen = false;
        }

        /// <summary>同步通知选项的对号。</summary>
        private void UpdateNotifyUi()
        {
            notifyDeviceCheck.Visibility = Globals.AppSettings.NOTIFY_DEVICE_CHANGE ? Visibility.Visible : Visibility.Collapsed;
            notifyInstallCheck.Visibility = Globals.AppSettings.NOTIFY_INSTALL_RESULT ? Visibility.Visible : Visibility.Collapsed;
            notifyRemoteCheck.Visibility = Globals.AppSettings.NOTIFY_REMOTE_DISCONNECT ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>同步“关闭窗口时”的对号（单选）。</summary>
        private void UpdateExitModeUi()
        {
            bool toTray = Globals.AppSettings.CLOSE_TO_TRAY;
            exitToTrayCheck.Visibility = toTray ? Visibility.Visible : Visibility.Collapsed;
            exitDirectCheck.Visibility = toTray ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>切换窗口置顶（菜单项以对号表示当前状态）</summary>
        private void topmostItem_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            UpdateTopmostUi();
            setup_popup.IsOpen = false;
        }

        /// <summary>同步「置顶显示」的对号显示</summary>
        private void UpdateTopmostUi()
        {
            topmostCheck.Visibility = this.Topmost ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>当前展开的二级菜单及其对应的主菜单项</summary>
        private Border _activeOptionColumn;
        private FrameworkElement _activeOptionAnchor;

        /// <summary>鼠标进入「定时刷新」：在右侧展开间隔选项二级菜单</summary>
        private void refreshInterval_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateRefreshIntervalUi();
            ShowOptionColumn(refreshIntervalColumn, refreshIntervalBtn);
        }

        /// <summary>鼠标进入「通知」：展开通知多选二级菜单</summary>
        private void notifyBtn_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateNotifyUi();
            ShowOptionColumn(notifyColumn, notifyBtn);
        }

        /// <summary>鼠标进入「关闭窗口时」：展开单选二级菜单</summary>
        private void exitModeBtn_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateExitModeUi();
            ShowOptionColumn(exitColumn, exitModeBtn);
        }

        /// <summary>鼠标进入已展开的二级菜单：取消收起</summary>
        private void optionColumn_MouseEnter(object sender, MouseEventArgs e)
        {
            hideIntervalTimer?.Stop();
        }

        /// <summary>鼠标离开主菜单项或二级菜单：延迟判断，避免在两者间隙被误收起</summary>
        private void optionItem_MouseLeave(object sender, MouseEventArgs e)
        {
            hideIntervalTimer?.Stop();
            hideIntervalTimer?.Start();
        }

        /// <summary>展开指定二级菜单，并把它的顶边对齐到对应的主菜单项</summary>
        private void ShowOptionColumn(Border column, FrameworkElement anchor)
        {
            hideIntervalTimer?.Stop();

            if (refreshIntervalColumn != column) refreshIntervalColumn.Visibility = Visibility.Collapsed;
            if (notifyColumn != column) notifyColumn.Visibility = Visibility.Collapsed;
            if (exitColumn != column) exitColumn.Visibility = Visibility.Collapsed;

            AlignOptionColumn(column, anchor);
            column.Visibility = Visibility.Visible;
            _activeOptionColumn = column;
            _activeOptionAnchor = anchor;
        }

        /// <summary>二级菜单顶边与主菜单项顶边对齐（按实际布局算，不写死像素偏移）</summary>
        private void AlignOptionColumn(Border column, FrameworkElement anchor)
        {
            try
            {
                Point point = anchor.TransformToAncestor(setupMainCard).Transform(new Point(0, 0));
                column.Margin = new Thickness(4, point.Y, 0, 0);
            }
            catch
            {
                // 布局尚未完成时保留默认偏移，不影响功能
            }
        }

        /// <summary>延迟收起：鼠标已不在主菜单项和二级菜单上时才真正收起</summary>
        private void hideIntervalTimer_Tick(object sender, EventArgs e)
        {
            hideIntervalTimer.Stop();

            Border column = _activeOptionColumn;
            FrameworkElement anchor = _activeOptionAnchor;
            if (column != null && column.IsMouseOver) return;
            if (anchor != null && anchor.IsMouseOver) return;

            HideOptionColumns();
        }

        /// <summary>收起所有二级菜单</summary>
        private void HideOptionColumns()
        {
            hideIntervalTimer?.Stop();
            refreshIntervalColumn.Visibility = Visibility.Collapsed;
            notifyColumn.Visibility = Visibility.Collapsed;
            exitColumn.Visibility = Visibility.Collapsed;
            _activeOptionColumn = null;
            _activeOptionAnchor = null;
        }

        /// <summary>选择定时刷新间隔</summary>
        private void refreshInterval_Click(object sender, RoutedEventArgs e)
        {
            Button item = (Button)sender;
            refreshIntervalSeconds = int.Parse(item.Tag.ToString());

            // 持久化到配置文件，下次启动沿用
            Globals.AppSettings.REFRESH_INTERVAL = refreshIntervalSeconds;

            // 选择后立即刷新一次，无需等待第一个周期
            ApplyRefreshInterval(refreshIntervalSeconds > 0);

            HideOptionColumns();
            setup_popup.IsOpen = false;
        }

        /// <summary>合法的定时刷新间隔（秒），0表示关闭</summary>
        private static bool IsValidRefreshInterval(int seconds)
        {
            return seconds == 0 || seconds == 3 || seconds == 5 || seconds == 10
                || seconds == 15 || seconds == 20 || seconds == 30 || seconds == 60;
        }

        /// <summary>按当前间隔设置启停计时器并同步界面</summary>
        /// <param name="refreshNow">是否立即刷新一次</param>
        private void ApplyRefreshInterval(bool refreshNow)
        {
            if (refreshIntervalSeconds <= 0)
            {
                refreshTimer.Stop();
            }
            else
            {
                refreshTimer.Interval = TimeSpan.FromSeconds(refreshIntervalSeconds);
                refreshTimer.Stop();
                refreshTimer.Start();
                if (refreshNow)
                {
                    RefreshDevices(false);
                }
            }
            UpdateRefreshIntervalUi();
        }

        /// <summary>
        /// 同步定时刷新菜单的显示状态：当前生效的间隔打勾。
        /// 与「通知」「关闭窗口时」两级菜单一致——每项第一格是固定宽度的对号，避免选中后文字左右跳动。
        /// </summary>
        private void UpdateRefreshIntervalUi()
        {
            if (refreshIntervalPanel == null) return;

            foreach (UIElement child in refreshIntervalPanel.Children)
            {
                Button item = child as Button;
                if (item == null || item.Tag == null) continue;

                int seconds;
                if (!int.TryParse(item.Tag.ToString(), out seconds)) continue;

                Grid content = item.Content as Grid;
                if (content == null || content.Children.Count == 0) continue;

                TextBlock check = content.Children[0] as TextBlock;
                if (check == null) continue;

                check.Visibility = seconds == refreshIntervalSeconds ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        #endregion

        #region 《应用包管理》

        private void customkEnable_Click(object sender, RoutedEventArgs e)
        {
            CustomPackage_Click("启用", "pm enable");
        }

        private void customDisable_Click(object sender, RoutedEventArgs e)
        {
            CustomPackage_Click("禁用", "pm disable-user");
        }

        /// <summary>
        /// 输入包名并执行启用/禁用
        /// </summary>
        private void CustomPackage_Click(string action, string pmCommand)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            InputWindow inputWindow = new InputWindow()
            {
                Title = "请输入包名",
                Owner = this
            };
            if (inputWindow.ShowDialog() != true)
            {
                return;
            }
            string package = inputWindow.Result;
            if (MessageBoxResult.OK != MessageBox.Show($"确定要对设备【{device.ShowDeviceName}】执行【{action}包名{package}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{adbPath} -s {device.SerialArg} shell {pmCommand} {package}" },
                result =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{action}包名{package}成功", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                },
                err =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{action}包名{package}失败！\r\n1.检查设备是否已离线；\r\n2.应用包名未找到！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
        }

        /// <summary>
        /// 打开应用列表：一次性展示全部应用，并直接在列表里启动、停用、启用、卸载。
        /// 独立窗口（不依附主窗口），同一台设备只保留一个实例，重复点击时把已有窗口带到前台。
        /// </summary>
        private void appList_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            OpenAppList(device);
        }

        /// <summary>打开指定设备的应用列表窗口（主界面与托盘菜单共用）。</summary>
        private void OpenAppList(Device device)
        {
            if (device == null) return;

            // 设备列表刷新时已顺带读回硬件序列号，直接用；同一台手机 USB / IP / 无线只开一个窗口
            if (!string.IsNullOrEmpty(device.HardwareKey))
            {
                OpenAppListWindow(device, device.HardwareKey);
                return;
            }

            // 老设备或读取失败：现补一次，取不到时 AppDeviceKey 会退回连接序列号
            AppDeviceKey.ResolveAsync(adbPath, device.SerialArg, device.DeviceMark,
                key => OpenAppListWindow(device, key));
        }

        /// <summary>
        /// 按设备键打开（或激活）应用列表窗口：键相同说明是同一台手机，
        /// 已经有窗口就还原并带到前台，不重复创建。
        /// </summary>
        private void OpenAppListWindow(Device device, string deviceKey)
        {
            string key = BuildAppListWindowKey(device, deviceKey);

            AppListWindow opened;
            if (appListWindows.TryGetValue(key, out opened))
            {
                if (opened.IsLoaded)
                {
                    if (opened.WindowState == WindowState.Minimized)
                    {
                        opened.WindowState = WindowState.Normal;
                    }
                    opened.Activate();
                    return;
                }
                appListWindows.Remove(key);
            }

            AppListWindow window = new AppListWindow
            {
                AdbPath = adbPath,
                DeviceInfo = device
            };
            window.Closed += (s, args) => appListWindows.Remove(key);
            appListWindows[key] = window;
            window.Show();
        }

        /// <summary>
        /// 窗口去重键：序列号能唯一标识设备时用它，USB 与无线连接会落到同一个键上；
        /// 模拟器、克隆机常见通用序列号（如 0123456789ABCDEF）重复度高，
        /// 此时退回连接标识，避免两台不同设备被当成同一台。
        /// </summary>
        private static string BuildAppListWindowKey(Device device, string deviceKey)
        {
            const string SimulatorSerial = "0123456789ABCDEF";

            bool serialUsable = !string.IsNullOrEmpty(deviceKey)
                && !string.Equals(deviceKey, AppDeviceKey.Unknown, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(deviceKey, SimulatorSerial, StringComparison.OrdinalIgnoreCase);

            if (serialUsable)
            {
                return "serial:" + deviceKey;
            }

            string fallback = device.DeviceMark;
            if (string.IsNullOrEmpty(fallback))
            {
                fallback = device.SerialArg;
            }
            return "mark:" + (fallback ?? string.Empty);
        }

        #endregion

        #region 《拖放安装》

        private bool dragFilevalid = false;
        private bool exceededMax = false;
        private int maxFileCount = 1000;

        private void listBoxItem_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                Globals.FileCounter.CountFiles(files, out exceededMax, maxFileCount);

                List<string> apkList = exceededMax ? new List<string>() : GetAllApkFilesFromDrop(files);
                dragFilevalid = !exceededMax && apkList.Count > 0;
                e.Effects = dragFilevalid ? DragDropEffects.Copy : DragDropEffects.None;

                if (sender is Grid grid)
                {
                    // 半透明深色蒙层提示拖放目标（直接设置卡片背景，
                    // 本地值会覆盖样式中的悬停/选中效果，拖放结束后清除）
                    if (grid.FindName("itemCard") is Border itemCard)
                    {
                        itemCard.Background = new SolidColorBrush(Color.FromArgb(0xa0, 0x1f, 0x29, 0x3b));
                    }
                    if (grid.FindName("dragLabel") is Label dragLabel)
                    {
                        dragLabel.Visibility = Visibility.Visible;
                        dragLabel.Content = exceededMax
                            ? $"文件过多（超过 {maxFileCount} 个）"
                            : dragFilevalid
                                ? $"松手{(apkList.Count <= 1 ? "" : "依次")}安装 {apkList.Count} 个文件"
                                : "未检测到有效的 APK 文件!";
                        dragLabel.Foreground = dragFilevalid ? Brushes.LightGreen : Brushes.LightCoral;
                        dragLabel.FontWeight = FontWeights.Bold;
                    }
                }
            }
            e.Handled = true;
        }

        private void listBoxItem_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Grid grid)
            {
                ResetDragOverlay(grid);
            }
            e.Handled = true;
        }

        private void listBoxItem_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !exceededMax && dragFilevalid ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void listBoxItem_Drop(object sender, DragEventArgs e)
        {
            // 先恢复背景和标签
            if (sender is Grid grid)
            {
                ResetDragOverlay(grid);
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && !exceededMax
                && ((FrameworkElement)sender).DataContext is Device targetDevice)
            {
                List<string> apkList = GetAllApkFilesFromDrop((string[])e.Data.GetData(DataFormats.FileDrop));
                if (apkList.Count > 0)
                {
                    // 执行安装逻辑
                    HandleApkInstallation(apkList, 0, targetDevice);
                }
            }
            e.Handled = true;
        }

        /// <summary>
        /// 恢复拖放提示：清除卡片背景的本地值（恢复样式悬停/选中效果）并隐藏标签
        /// </summary>
        private static void ResetDragOverlay(Grid grid)
        {
            if (grid.FindName("itemCard") is Border itemCard)
            {
                itemCard.ClearValue(Border.BackgroundProperty);
            }
            if (grid.FindName("dragLabel") is Label dragLabel)
            {
                dragLabel.Visibility = Visibility.Collapsed;
                dragLabel.Content = "";
            }
        }

        /// <summary>
        /// 从拖放的数据中提取所有 APK 文件（支持目录递归，无权限目录自动跳过）
        /// </summary>
        private static List<string> GetAllApkFilesFromDrop(string[] files)
        {
            var apkFiles = new List<string>();
            // "*.apk.*" 的通配语义也会命中 a.apk，两个模式会重叠，按路径去重
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in files)
            {
                if (File.Exists(path))
                {
                    if (IsApkFile(path) && seen.Add(path))
                    {
                        apkFiles.Add(path);
                    }
                }
                else if (Directory.Exists(path))
                {
                    // 使用 Directory.EnumerateFiles 递归搜索所有.apk文件
                    try
                    {
                        foreach (string found in Directory.EnumerateFiles(path, "*.apk", SearchOption.AllDirectories))
                        {
                            if (seen.Add(found)) apkFiles.Add(found);
                        }
                        // 微信等分发工具会把 xxx.apk 存成 xxx.apk.1，一并找出来
                        foreach (string found in Directory.EnumerateFiles(path, "*.apk.*", SearchOption.AllDirectories))
                        {
                            if (seen.Add(found)) apkFiles.Add(found);
                        }
                    }
                    catch (Exception)
                    {
                        // 跳过无法访问的目录
                    }
                }
            }
            return apkFiles;
        }

        /// <summary>
        /// 是否为可安装的 APK 文件：常规以 .apk 结尾；
        /// 微信、QQ 等分发工具会把文件存成 xxx.apk.1 / xxx.apk.2，这类也认作 APK。
        /// </summary>
        private static bool IsApkFile(string path)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(".apk.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 安装前准备可用的文件路径：非 .apk 后缀（如 xxx.apk.1）复制一份到缓存目录，
        /// 避免安装器按后缀校验失败；原文件不动，安装结束后删除临时副本。
        /// </summary>
        private static string PrepareInstallFile(string apkPath)
        {
            if (apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                return apkPath;
            }

            string target = Globals.GetCacheFilePath("install-" + Guid.NewGuid().ToString("N") + ".apk");
            File.Copy(apkPath, target, true);
            return target;
        }

        /// <summary>
        /// 删除为安装生成的临时副本。路径与原文件相同说明没有复制过，直接跳过；
        /// 另外只删缓存目录里的文件，确保无论如何都不会碰到用户的原始文件。
        /// </summary>
        private static void DeleteTempInstallFile(string installPath, string originalPath)
        {
            if (string.Equals(installPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string cacheRoot = Globals.CacheDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!installPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Delete(installPath);
            }
            catch (Exception)
            {
                // 被占用时留给启动时的缓存清理
            }
        }

        /// <summary>
        /// 依次安装apk列表（每个文件安装前都会提醒）
        /// </summary>
        private void HandleApkInstallation(List<string> apkList, int index, Device targetDevice)
        {
            if (index >= apkList.Count)
            {
                return;
            }
            string apkFilePath = apkList[index];
            // 从 APK 里读出应用名与包名，让用户在确认前看清装的是什么
            string label;
            string packageName;
            ApkInfoReader.Read(apkFilePath, out label, out packageName);

            InstallConfirmWindow confirm = new InstallConfirmWindow(
                label, packageName, apkFilePath, targetDevice.ShowDeviceName)
            {
                Owner = this
            };

            if (confirm.ShowDialog() != true)
            {
                HandleApkInstallation(apkList, index + 1, targetDevice);
                return;
            }

            // 微信等来源的文件名是 xxx.apk.1，安装前复制成 .apk 的临时副本
            string installPath;
            try
            {
                installPath = PrepareInstallFile(apkFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法读取文件【{apkFilePath}】：\r\n{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                HandleApkInstallation(apkList, index + 1, targetDevice);
                return;
            }

            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{adbPath} -s {targetDevice.SerialArg} install \"{installPath}\"" },
                result =>
                {
                    waitAnimation(false);
                    DeleteTempInstallFile(installPath, apkFilePath);
                    if (Globals.ContainsAny(result, "Success"))
                    {
                        MessageBox.Show($"设备【{targetDevice.ShowDeviceName}】安装应用成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                        if (Globals.AppSettings.NOTIFY_INSTALL_RESULT)
                        {
                            App.Notify($"安装完成\r\n{targetDevice.ShowDeviceName}\r\n{label ?? packageName ?? Path.GetFileName(apkFilePath)} 安装成功");
                        }
                    }
                    // 无论成功与否都继续下一个，避免批量安装中断
                    HandleApkInstallation(apkList, index + 1, targetDevice);
                },
                err =>
                {
                    waitAnimation(false);
                    DeleteTempInstallFile(installPath, apkFilePath);
                    if (Globals.ContainsAny(err, "adb: failed to install", "device offline", "Error: closed"))
                    {
                        MessageBox.Show($"设备【{targetDevice.ShowDeviceName}】安装应用失败！\r\n1.请确认安卓设备是否允许安装应用；\r\n2.应用签名是否不符；\r\n3.检查设备是否已离线。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (Globals.AppSettings.NOTIFY_INSTALL_RESULT)
                        {
                            App.Notify($"安装失败\r\n{targetDevice.ShowDeviceName}\r\n{label ?? packageName ?? Path.GetFileName(apkFilePath)} 安装失败",
                                BalloonIcon.Error);
                        }
                        if (!Globals.ContainsAny(err, "device offline", "Error: closed"))
                        {
                            HandleApkInstallation(apkList, index + 1, targetDevice);
                        }
                        return true;
                    }
                    return false;
                }, 600);
        }

        #endregion
    }
}
