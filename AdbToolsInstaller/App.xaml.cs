using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AdbToolsInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

#if UNINSTALLER_ONLY
            // 独立卸载程序（不含安装载荷）：始终进入卸载模式
            new MainWindow(uninstallMode: true).Show();
#else
            bool silent = HasArg(e.Args, "/silent") || HasArg(e.Args, "-silent");
            // 以 uninstall.exe 名义启动（控制面板卸载入口复制的同名文件）即视为卸载模式，无需再传 /uninstall
            bool uninstall = HasArg(e.Args, "/uninstall") || HasArg(e.Args, "-uninstall")
                || string.Equals(Path.GetFileName(Assembly.GetExecutingAssembly().Location), InstallConsts.UninstallExeName, StringComparison.OrdinalIgnoreCase);

            if (uninstall)
            {
                if (silent)
                {
                    RunUninstall(true);
                    Shutdown();
                    return;
                }
                // 交互式卸载：弹出仅含“卸载”的窗口
                new MainWindow(uninstallMode: true).Show();
                return;
            }

            if (silent)
            {
                RunInstall(InstallEngine.GetInstalledPath() ?? InstallEngine.DefaultInstallPath(), silent);
                Shutdown();
                return;
            }

            // 交互模式：显示主窗口
            new MainWindow().Show();
#endif
        }

        private static bool HasArg(string[] args, string s)
        {
            foreach (var a in args)
                if (string.Equals(a, s, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void RunInstall(string path, bool silent)
        {
            try
            {
                InstallEngine.Install(path, m => { if (!silent) Console.WriteLine(m); }, p => { });
            }
            catch (Exception ex)
            {
                if (silent) Console.WriteLine("安装失败：" + ex.Message);
                else System.Windows.MessageBox.Show("安装失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void RunUninstall(bool silent)
        {
            try
            {
                InstallEngine.Uninstall(false, m => { if (!silent) Console.WriteLine(m); }, p => { });
            }
            catch (Exception ex)
            {
                if (silent) Console.WriteLine("卸载失败：" + ex.Message);
                else System.Windows.MessageBox.Show("卸载失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
