using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Update.AccessInterface;

namespace Update
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private string updateFilePath;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            waitProcessBar.IsShowAnimation = true;
            updateFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.zip");
            updateTip("正在下载...");

            new Thread(() =>
            {
                try
                {
                    if (RequestJson.DownloadFile(App.downloadUrl, updateFilePath))
                    {
                        updateTip("正在解压");
                        Thread.Sleep(1000);
                        ZipFileUtils.UnzipFile(updateFilePath, App.unZipPath);
                        updateTip("更新完成");
                        Thread.Sleep(1000);

                        if (!string.IsNullOrWhiteSpace(App.startExePath))
                        {
                            updateTip("启动程序");
                            StartExe(App.startExePath, "");
                        }
                        else
                        {
                            Thread.Sleep(1000);
                            Quit();
                        }
                    }
                    else
                    {
                        updateTip("下载失败");
                    }
                }
                catch (Exception)
                {
                    updateTip("更新失败，请使用全量更新");
                    Dispatcher.Invoke(new Action(() =>
                    {
                        waitProcessBar.IsShowAnimation = false;
                    }));
                }
            })
            { IsBackground = true }.Start();
        }

        private void updateTip(string tip)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                tipLabel.Content = tip;
            }));
        }

        private void StartExe(string exePath, string args)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = args,
                        UseShellExecute = true // 使用操作系统shell启动
                    });

                    ///退出当前新开进程，不走OnExit方法
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }));
        }

        private void Quit()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                Application.Current.Shutdown();
            }));
        }

    }
}
