using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AdbToolsInstaller
{
    public partial class MainWindow : Window
    {
        private bool _upgrading;
        private bool _uninstallMode;
        private bool _isDowngrade;

        public MainWindow(bool uninstallMode = false)
        {
            InitializeComponent();
            if (uninstallMode) SetupUninstallMode();
            else LoadState();
        }

        private void SetupUninstallMode()
        {
            InstallPanel.Visibility = Visibility.Collapsed;
            string installed = InstallEngine.GetInstalledPath();
            StatusText.Text = "确定要卸载 " + InstallConsts.ProductName + " 吗？"
                + (string.IsNullOrEmpty(installed) ? "" : ("\n安装位置：" + installed));
            LogBox.Text = "点击“确定”开始卸载…";
            InstallButton.Visibility = Visibility.Collapsed;
            // “确定”用主按钮（蓝色）样式，“取消”保持幽灵样式，两者大小一致
            UninstallButton.Visibility = Visibility.Visible;
            UninstallButton.Style = (Style)FindResource("PrimaryButton");
            UninstallButton.Content = "确定";
            UninstallButton.Width = 120;
            UninstallButton.Margin = new Thickness(0, 0, 8, 0);
            ExitButton.Content = "退出";
            ExitButton.Width = 120;
            _uninstallMode = true;
        }

        private void LoadState()
        {
            string installed = InstallEngine.GetInstalledPath();
            string installedVer = InstallEngine.GetInstalledVersion();
            string pkgVer = InstallEngine.GetPackageVersion();
            string pkgVerText = string.IsNullOrEmpty(pkgVer) ? "（未知版本）" : ("v" + pkgVer);
            _upgrading = !string.IsNullOrEmpty(installed) && Directory.Exists(installed);
            _isDowngrade = false;

            if (_upgrading)
            {
                PathBox.Text = installed;
                var vi = NormalizeVersion(installedVer);
                var vp = NormalizeVersion(pkgVer);

                if (vi == null || vp == null)
                {
                    StatusText.Text = "已安装版本 " + (installedVer ?? "（未知）") + "，当前安装包版本 " + pkgVerText
                        + "，将覆盖安装到原位置。";
                    InstallButton.Content = "升级安装";
                }
                else
                {
                    int cmp = vp.CompareTo(vi);
                    if (cmp > 0)
                    {
                        StatusText.Text = "已安装版本 " + installedVer + "，当前安装包版本 " + pkgVerText
                            + "，将升级到新版本并覆盖到原位置。";
                        InstallButton.Content = "升级安装";
                    }
                    else if (cmp < 0)
                    {
                        StatusText.Text = "已安装版本 " + installedVer + "，当前安装包版本 " + pkgVerText
                            + "，当前安装包较旧，将降级为旧版本并覆盖到原位置。";
                        InstallButton.Content = "降级安装";
                        _isDowngrade = true;
                    }
                    else
                    {
                        StatusText.Text = "已安装版本 " + installedVer + "，当前安装包版本 " + pkgVerText
                            + "，版本相同，将重新安装并覆盖到原位置。";
                        InstallButton.Content = "重新安装";
                    }
                }
                UninstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                PathBox.Text = InstallEngine.DefaultInstallPath();
                StatusText.Text = "首次安装：将安装 " + InstallConsts.ProductName + " " + pkgVerText
                    + " 到以下位置（系统中保持唯一条目，支持升级覆盖与卸载）：";
                InstallButton.Content = "安装";
                UninstallButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 把 "1.4" / "1.4.0" / "1.4.0.0" 归一化为四段 Version 便于比较；无法解析返回 null。
        /// </summary>
        private static Version NormalizeVersion(string s)
        {
            Version v;
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!Version.TryParse(s.Trim(), out v)) return null;
            return new Version(v.Major, v.Minor,
                v.Build < 0 ? 0 : v.Build,
                v.Revision < 0 ? 0 : v.Revision);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.SelectedPath = PathBox.Text;
                dlg.Description = "选择安装目录";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    PathBox.Text = dlg.SelectedPath;
            }
        }

        private void SetBusy(bool busy)
        {
            InstallButton.IsEnabled = !busy;
            UninstallButton.IsEnabled = !busy;
            ExitButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            PathBox.IsEnabled = !busy;
            if (busy)
            {
                Progress.IsIndeterminate = true;
                PercentText.Text = "";
            }
            else
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
            }
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() => { LogBox.AppendText(msg + Environment.NewLine); LogBox.ScrollToEnd(); });
        }

        private void SetProgress(int p)
        {
            Dispatcher.Invoke(() =>
            {
                if (p >= 0)
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = Math.Max(0, Math.Min(100, p));
                    PercentText.Text = p + "%";
                }
            });
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            string path = PathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                System.Windows.MessageBox.Show("请选择安装目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_isDowngrade)
            {
                if (System.Windows.MessageBox.Show(
                    "当前安装包版本比已安装版本更旧，将降级覆盖安装。\n是否继续？",
                    "降级确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            SetBusy(true);
            LogBox.Clear();
            Task.Run(() =>
            {
                try
                {
                    InstallEngine.Install(path, Log, SetProgress);
                    Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show("安装完成！", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        if (LaunchCheck.IsChecked == true)
                        {
                            try { Process.Start(Path.Combine(path, InstallConsts.AppExe)); } catch { }
                        }
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Log("错误：" + ex.Message);
                        System.Windows.MessageBox.Show("安装失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        SetBusy(false);
                    });
                }
            });
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!_uninstallMode && System.Windows.MessageBox.Show("确定要卸载 " + InstallConsts.ProductName + " 吗？", "卸载", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            SetBusy(true);
            LogBox.Clear();
            Task.Run(() =>
            {
                try
                {
                    InstallEngine.Uninstall(Log, SetProgress);
                }
                catch (Exception ex)
                {
                    Log("错误：" + ex.Message);
                }
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show("卸载完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    Environment.Exit(0);
                });
            });
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>
    /// 让 WinForms 的 FolderBrowserDialog 能挂靠到 WPF 窗口（模态居中）。
    /// </summary>
    public class WindowWrapper : System.Windows.Forms.IWin32Window
    {
        public WindowWrapper(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }
}
