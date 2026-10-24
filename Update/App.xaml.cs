using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Update
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

        public static string downloadUrl;
        public static string unZipPath;
        public static string startExePath;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                // 参数：更新包地址、解压目录、重启程序路径（可选）
                if (e.Args.Length < 2)
                {
                    Environment.Exit(0);
                    return;
                }
                downloadUrl = e.Args[0];
                unZipPath = e.Args[1];
                if (e.Args.Length > 2)
                {
                    startExePath = e.Args[2];
                }

                // 已运行更新程序时，激活已有窗口并退出当前实例
                Process current = Process.GetCurrentProcess();
                Process[] processes = Process.GetProcessesByName(current.ProcessName);
                Process running = Array.Find(processes, p => p.Id != current.Id);
                if (running != null)
                {
                    HandleRunningInstance(running);
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception)
            {
            }


            //UI线程未捕获异常处理事件（UI主线程）
            Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
            //非UI线程未捕获异常处理事件(例如自己创建的一个子线程)
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            //Task线程内未捕获异常处理事件
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void HandleRunningInstance(Process instance)
        {
            ShowWindowAsync(instance.MainWindowHandle, 1);  //调用api函数，正常显示窗口
            SetForegroundWindow(instance.MainWindowHandle); //将窗口放置最前端
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
