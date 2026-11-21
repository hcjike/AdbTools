using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// 安装前的确认窗口：先在后台线程解析 APK 的应用名与包名，解析完成后再允许安装。
    /// 解析不到名称时退回文件名，避免让用户对着 xxx.apk.1 猜装的是什么。
    /// </summary>
    public partial class InstallConfirmWindow : Window
    {
        private readonly string _apkPath;
        private volatile bool _closed;

        /// <summary>最终显示的应用名；解析失败时为文件名。</summary>
        public string Label { get; private set; }

        /// <summary>最终解析出的包名；解析失败时为 null。</summary>
        public string PackageName { get; private set; }

        public InstallConfirmWindow(string apkPath, string deviceName)
        {
            InitializeComponent();

            _apkPath = apkPath;
            Label = Path.GetFileName(apkPath);
            PackageName = null;

            appNameText.Text = Label;
            packageText.Text = "解析中…";
            packageText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

            string size = FormatSize(apkPath);
            fileText.Text = size.Length == 0 ? apkPath : apkPath + "   ·   " + size;
            deviceText.Text = deviceName;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string label;
                string packageName;
                ApkInfoReader.Read(apkPath, out label, out packageName);

                if (_closed) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    ApplyInfo(label, packageName);
                }));
            });
        }

        private void ApplyInfo(string label, string packageName)
        {
            Label = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(_apkPath) : label.Trim();
            PackageName = string.IsNullOrWhiteSpace(packageName) ? null : packageName.Trim();

            appNameText.Text = Label;

            if (PackageName == null)
            {
                packageText.Text = "未能解析";
                packageText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            }
            else
            {
                packageText.Text = PackageName;
                packageText.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            }

            installButton.IsEnabled = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
        }

        /// <summary>文件大小：列表里只作辅助信息，取不到就不显示。</summary>
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

        private void install_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
