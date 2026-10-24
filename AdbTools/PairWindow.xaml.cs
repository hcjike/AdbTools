using System;
using System.Windows;

namespace AdbTools
{
    /// <summary>
    /// PairWindow.xaml 的交互逻辑
    /// </summary>
    public partial class PairWindow : Window
    {
        public string AdbPath { get; set; }

        public string Address { get; set; }

        public PairWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Address))
            {
                this.deviceAddress.Text = Address;
            }
        }

        private void pairDevice_Click(object sender, RoutedEventArgs e)
        {
            string address = deviceAddress.Text.Replace("：", ":");
            deviceAddress.Text = address;
            string code = pairCode.Text;
            if (!Globals.IsValidIPEndPoint(address))
            {
                MessageBox.Show("设备配对地址输入错误！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("请输入配对码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            waitAnimation(true);
            // 第二条为配对码，由 adb pair 从stdin读取
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{AdbPath} pair {address}", code }, result =>
            {
                waitAnimation(false);
                if (Globals.ContainsAny(result, "Successfully"))
                {
                    MessageBox.Show("设备配对成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                    this.Close();
                }
                else
                {
                    MessageBox.Show("设备配对失败！\r\n1.确认IP地址和端口是否处于配对状态；\r\n2.配对码输入是否正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, null);
        }

        private void waitAnimation(bool show)
        {
            processBarGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            waitProcessBar.IsShowAnimation = show;
        }
    }
}
