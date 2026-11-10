using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// 安装前的确认窗口：显示从 APK 里读到的应用名与包名。
    /// 解析不到名称时退回文件名，避免让用户对着 xxx.apk.1 猜装的是什么。
    /// </summary>
    public partial class InstallConfirmWindow : Window
    {
        public InstallConfirmWindow(string label, string packageName, string apkPath, string deviceName)
        {
            InitializeComponent();

            appNameText.Text = string.IsNullOrWhiteSpace(label)
                ? Path.GetFileName(apkPath)
                : label.Trim();

            if (string.IsNullOrWhiteSpace(packageName))
            {
                packageText.Text = "未能解析";
                packageText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            }
            else
            {
                packageText.Text = packageName.Trim();
            }

            string size = FormatSize(apkPath);
            fileText.Text = size.Length == 0 ? apkPath : apkPath + "   ·   " + size;
            deviceText.Text = deviceName;
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
