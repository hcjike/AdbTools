using System.Windows;

namespace AdbTools
{
    /// <summary>
    /// UpdateWindow.xaml 的交互逻辑
    /// 新版本提示弹窗：立即更新 / 以后再说，说明文字中的链接可打开版本说明
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private readonly string releasePageUrl;

        public UpdateWindow(string tagName, string releasePageUrl)
        {
            InitializeComponent();

            this.releasePageUrl = releasePageUrl;

            string version = (tagName ?? string.Empty).Trim().TrimStart('v', 'V');
            titleText.Text = "发现新版本 V" + version;

            // 没有发布页地址时隐藏链接，只保留纯说明文字
            releaseLink.IsEnabled = !string.IsNullOrEmpty(releasePageUrl);
        }

        /// <summary>用户选择立即下载并安装</summary>
        public bool StartUpdate { get; private set; }

        private void update_Click(object sender, RoutedEventArgs e)
        {
            StartUpdate = true;
            DialogResult = true;
        }

        private void releaseLink_Click(object sender, RoutedEventArgs e)
        {
            // 仅用浏览器打开版本说明，不关闭本窗口，用户看完仍可继续更新或稍后再说
            if (!string.IsNullOrEmpty(releasePageUrl))
            {
                CmdExecutor.StartExe(releasePageUrl);
            }
        }

        private void cancel_Click(object sender, RoutedEventArgs e)
        {
            StartUpdate = false;
            DialogResult = false;
        }
    }
}
