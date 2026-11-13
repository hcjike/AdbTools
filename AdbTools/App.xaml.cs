using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AdbTools.RemoteControl;
using AdbTools.Tray;
using Hardcodet.Wpf.TaskbarNotification;

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

        /// <summary>所有托盘通知统一使用的标题（与窗口标题一致）。</summary>
        internal const string NotificationTitle = "Adb连接工具";

        /// <summary>广播句柄：向所有顶层窗口广播消息，隐藏的主窗口同样能收到。</summary>
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

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

            // 不设置显式 AUMID：快捷方式没有绑定同一个 AUMID 时，系统会把通知的应用名
            // 显示成 AUMID 字符串本身。不设置则系统退回使用程序自身信息（AssemblyTitle）。

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
        /// 弹托盘通知：标题统一用程序名，事件与详情放在正文，
        /// 避免系统通知里出现各样的事件名当标题。
        /// </summary>
        internal static void Notify(string message, BalloonIcon icon = BalloonIcon.Info)
        {
            TrayIconHost tray = TrayIcon;
            if (tray != null)
            {
                tray.Notify(NotificationTitle, message, icon);
            }
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

        /// <summary>退出确认框是否正在显示，避免重复点击叠加多个对话框。</summary>
        private static bool _exitConfirming;

        /// <summary>
        /// 托盘菜单「退出」：先二次确认，默认选中“否”，确认后才真正退出。
        ///
        /// 托盘菜单还在关闭过程中的这次鼠标输入如果直接弹模态框，会被后续消息立即关掉
        /// （表现为确认框闪一下就消失）。所以先把主窗口恢复正常并激活、让程序拿到前台，
        /// 再延迟一小会儿，等输入队列彻底清空后由主窗口作为 Owner 弹出确认框。
        /// </summary>
        internal static void ConfirmAndExit()
        {
            Application app = Current;
            if (app == null || _exitConfirming) return;
            _exitConfirming = true;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowMainWindow();

                // 即便排到空闲优先级，托盘菜单收尾时仍可能残留鼠标/激活消息，
                // 会把刚弹出的对话框立即点掉。稍等一小会儿再弹，用户几乎察觉不到。
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    ShowExitConfirm();
                };
                timer.Start();
            }), DispatcherPriority.Background);
        }

        /// <summary>真正显示退出确认框（在输入事件处理完、程序回到前台之后执行）。</summary>
        private static void ShowExitConfirm()
        {
            const string message = "确定要退出 Adb 连接工具吗？\n\n"
                + "已打开的远程控制与应用列表窗口会一并关闭，"
                + "托盘图标也会消失，需要重新启动程序才能继续使用。";

            MainWindow main = MainWindowInstance;
            MessageBoxResult result = main != null
                ? MessageBox.Show(main, message, "退出确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, "退出确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            _exitConfirming = false;

            if (result != MessageBoxResult.Yes) return;

            ExitApplication();
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
            Dispatcher.Invoke(new Action(() =>
            {
                string message = e.ExceptionObject is Exception ex ? ex.Message : e.ExceptionObject?.ToString();
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

            Dispatcher.Invoke(new Action(() =>
            {
                MessageBox.Show("未捕获UI线程异常：" + e.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }
    }
}
