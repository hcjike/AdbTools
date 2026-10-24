using System.Windows;

namespace AdbTools
{
    /// <summary>
    /// InfoShowWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InfoShowWindow : Window
    {
        public InfoShowWindow()
        {
            InitializeComponent();
        }

        public string DescriptionContent { get; set; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.message.Text = DescriptionContent;
        }
    }
}
