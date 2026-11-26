using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AdbTools.Notify;
using AdbTools.RemoteControl;
using AdbTools.Tray;
using Hardcodet.Wpf.TaskbarNotification;

using AdbTools.Common;
using AdbTools.Windows;

namespace AdbTools
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        [DllImport("User32.dll")]
        private static extern bool ShowWindowAsync(System.IntPtr hWnd, int cmdShow);
        [DllImport("User32.dll")]
        private static extern bool SetForegroundWindow(System.IntPtr hWnd);
        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);
        [DllImport("User32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        /// <summary>所有托盘通知统一使用的标题（与窗口标题一致）。</summary>
        internal const string NotificationTitle = "Adb连接工具";

        /// <summary>
        /// 应用通知使用的 AUMID。Windows 需要它把 Toast 归属到本程序；
        /// 安装器创建快捷方式时也会写入同一个值。
        /// </summary>
        internal const string AppUserModelId = "Hcjike.AdbTools";

        /// <summary>广播句柄：向所有顶层窗口广播消息，隐藏的主窗口同样能收到。</summary>
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

        /// <summary>全局滚动条粗细（像素）。</summary>
        private const double ThinScrollBarSize = 8;

        /// <summary>第二个实例用它通知已运行实例“把主窗口显示出来”。</summary>
        internal static int ShowMainWindowMessage { get; private set; }

        /// <summary>系统托盘图标；程序启动后创建，退出时释放。</summary>
        internal static TrayIconHost TrayIcon { get; private set; }

        /// <summary>已确认退出：主窗口关闭时不再隐藏到托盘。</summary>
        internal static bool ExitRequested { get; private set; }

        /// <summary>当前主窗口实例（可能已隐藏到托盘）。</summary>
        internal static MainWindow MainWindowInstance => Current?.MainWindow as MainWindow;

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 全局收窄滚动条：按 Loaded 类处理器直接写本地值。
            // 隐式 ScrollBar 样式在 ListBox / ListView 里可能被模板或主题的局部值盖掉，
            // 这里在每个滚动条加载时强制设成 8px，所有窗口（含后开的子窗口）都生效。
            EventManager.RegisterClassHandler(typeof(ScrollBar), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    var bar = (ScrollBar)sender;
                    if (bar.Orientation == Orientation.Vertical)
                    {
                        bar.Width = ThinScrollBarSize;
                        bar.MinWidth = ThinScrollBarSize;
                        bar.MaxWidth = ThinScrollBarSize;
                    }
                    else
                    {
                        bar.Height = ThinScrollBarSize;
                        bar.MinHeight = ThinScrollBarSize;
                        bar.MaxHeight = ThinScrollBarSize;
                    }
                }));

            // 同一字符串注册出来的消息号相同，两个实例都能对上
            ShowMainWindowMessage = RegisterWindowMessage("AdbTools.ShowMainWindow");

            // 已运行相同程序时，激活已有窗口并退出当前实例
            Process current = Process.GetCurrentProcess();
            Process[] processes = Process.GetProcessesByName(current.ProcessName);
            Process running = Array.Find(processes, p => p.Id != current.Id);
            if (running != null)
            {
                HandleRunningInstance(running);
                Current.Shutdown();
                return;
            }

            // 让 Windows 通知（Toast）把本程序识别出来；通知中心里显示的应用名由下面这步写定。
            // 失败只影响通知归属/名称，不影响功能。
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            ToastService.EnsureIdentity();

            // 缓存一律"用完即清理"：启动时清空 cache（含上次残留的更新安装包）
            Globals.CleanupCacheDirectory();

            //UI线程未捕获异常处理事件（UI主线程）
            Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
            //非UI线程未捕获异常处理事件(例如自己创建的一个子线程)
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            //Task线程内未捕获异常处理事件
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void HandleRunningInstance(Process instance)
        {
            // 已运行实例可能把主窗口隐藏到托盘，此时 MainWindowHandle 可能为 0，
            // 先广播消息让它自己把窗口显示出来（隐藏的窗口也能收到广播）
            try { PostMessage(HwndBroadcast, ShowMainWindowMessage, IntPtr.Zero, IntPtr.Zero); } catch { }

            // 旧版本或广播失败时仍用窗口句柄兜底
            try
            {
                if (instance.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(instance.MainWindowHandle, 1);
                    SetForegroundWindow(instance.MainWindowHandle);
                }
            }
            catch { }
        }

        /// <summary>创建系统托盘图标（主窗口加载后调用，只创建一次）。</summary>
        internal static void InitializeTray()
        {
            if (TrayIcon != null) return;
            try
            {
                TrayIcon = new TrayIconHost();
            }
            catch
            {
                // 托盘创建失败时程序仍可正常使用，只是没有后台图标
            }
        }

        /// <summary>把主窗口显示出来并激活（托盘菜单、左键、第二个实例都走这里）。</summary>
        internal static void ShowMainWindow()
        {
            MainWindow main = MainWindowInstance;
            // 窗口已被关闭（直接退出模式下关闭瞬间）时不再尝试显示，避免调用已关闭窗口的 Show
            if (main == null || !main.IsLoaded) return;

            try
            {
                if (main.WindowState == WindowState.Minimized)
                {
                    main.WindowState = WindowState.Normal;
                }
                if (!main.IsVisible)
                {
                    main.Show();
                }
                main.Activate();
            }
            catch
            {
                // 窗口正在关闭等异常情况忽略
            }
        }

        /// <summary>左键单击托盘图标：主窗口在前台时收起，否则显示到前台。</summary>
        internal static void ToggleMainWindow()
        {
            MainWindow main = MainWindowInstance;
            if (main == null) return;

            if (main.IsVisible && main.WindowState != WindowState.Minimized && main.IsActive)
            {
                main.Hide();
                return;
            }
            ShowMainWindow();
        }

        /// <summary>
        /// 弹系统通知：第一行作为标题，其余作为正文。
        /// 前台也照常提醒，后台同样走这条路；返回 false 表示通知没发出去。
        /// </summary>
        /// <param name="tag">同类通知的分组标识，用于后续聚合/替换</param>
        internal static bool Notify(string message, BalloonIcon icon = BalloonIcon.Info, string tag = null)
        {
            return NotifyCenter.Inform(message, ToKind(icon), tag);
        }

        /// <summary>
        /// 操作结果类通知（如批量安装结果）：通知被关闭或发不出去时改用对话框，
        /// 避免“结果只在通知里、通知又被关掉”导致用户完全不知道结果。
        /// 一次性、可忽略的事件（如配对成功）请直接用 <see cref="Notify"/>，不要弹窗。
        /// </summary>
        /// <param name="notifyAllowed">对应的通知开关是否为开</param>
        /// <param name="owner">对话框宿主窗口，可空（自动使用主窗口）</param>
        internal static void NotifyResult(string message, string tag = null,
            BalloonIcon icon = BalloonIcon.Info, bool notifyAllowed = true, Window owner = null)
        {
            NotifyCenter.InformResult(message, ToKind(icon), tag, notifyAllowed, owner);
        }

        private static NotifyKind ToKind(BalloonIcon icon)
        {
            return icon == BalloonIcon.Error ? NotifyKind.Error
                 : icon == BalloonIcon.Warning ? NotifyKind.Warning
                 : NotifyKind.Info;
        }

        /// <summary>更新托盘提示文本（版本 + 设备数）。</summary>
        internal static void UpdateTrayStatus(int deviceCount)
        {
            TrayIconHost tray = TrayIcon;
            if (tray != null)
            {
                tray.UpdateStatus(deviceCount);
            }
        }

        /// <summary>
        /// 托盘菜单「退出」：不再二次确认，直接走统一退出流程。
        /// 用 BeginInvoke 排到当前鼠标消息处理完之后，避免在托盘菜单回调里直接销毁托盘图标；
        /// 不弹对话框、不做额外等待，用户点完即退出。
        /// </summary>
        internal static void ExitFromTray()
        {
            Application app = Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(ExitApplication), DispatcherPriority.Background);
        }

        /// <summary>
        /// 唯一退出入口：先关远程控制窗口并等后台清理把设备端服务停掉，
        /// 再释放托盘图标，最后关窗口并退出进程。
        /// </summary>
        internal static void ExitApplication()
        {
            if (ExitRequested) return;
            ExitRequested = true;

            try
            {
                RemoteControlWindow.CloseAll();
                // 给后台清理一点时间收尾，避免设备端 scrcpy server 残留
                RemoteControlWindow.WaitAllCleanup(2500);
            }
            catch { }

            TrayIconHost tray = TrayIcon;
            TrayIcon = null;
            if (tray != null)
            {
                try { tray.Dispose(); } catch { }
            }

            try { Current?.Shutdown(); } catch { }
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            // 先落盘再提示：异常日志是排查这类问题的唯一线索
            AppExceptionLog.Write("Task 线程未处理异常", e.Exception);

            Dispatcher.Invoke(new Action(() =>
            {
                MessageBox.Show("未捕获Task线程异常：" + e.Exception?.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
            // 标记为已处理，避免进程退出时再次抛出
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Environment.Exit 会跳过终结器，托盘图标必须先自行释放，否则任务栏会残留
            TrayIconHost tray = TrayIcon;
            TrayIcon = null;
            if (tray != null)
            {
                try { tray.Dispose(); } catch { }
            }

            base.OnExit(e);

            Environment.Exit(0);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // 先落盘再提示
            Exception exception = e.ExceptionObject as Exception;
            if (exception != null)
            {
                AppExceptionLog.Write("非UI线程未处理异常", exception);
            }
            else
            {
                AppExceptionLog.WriteMessage("非UI线程未处理异常", e.ExceptionObject?.ToString());
            }

            Dispatcher.Invoke(new Action(() =>
            {
                string message = exception != null ? exception.Message : e.ExceptionObject?.ToString();
                MessageBox.Show("未捕获非UI线程异常：" + message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }

        private void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var comException = e.Exception as COMException;
            if (comException != null && comException.ErrorCode == -2147221040)
            {
                // 忽略剪贴板相关的COM异常
                e.Handled = true;
                return;
            }

            e.Handled = true;

            // 先落盘再提示
            AppExceptionLog.Write("UI线程未处理异常", e.Exception);

            Dispatcher.Invoke(new Action(() =>
            {
                MessageBox.Show("未捕获UI线程异常：" + e.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }
    }
}
