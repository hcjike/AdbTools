using System;
using System.Windows;

namespace AdbTools
{
    /// <summary>
    /// InputWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InputWindow : Window
    {
        public InputWindow()
        {
            InitializeComponent();
        }

        public string Result { get; private set; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.content.Focus();
        }

        private void enter_Click(object sender, RoutedEventArgs e)
        {
            Result = this.content.Text;
            if (string.IsNullOrEmpty(Result))
            {
                return;
            }
            DialogResult = true;
            this.Close();
        }

        private void cancle_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }

    }
}
