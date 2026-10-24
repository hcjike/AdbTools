using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// AlpsK71v1Window.xaml 的交互逻辑
    /// </summary>
    public partial class AlpsK71v1Window : Window
    {
        /// <summary>
        /// 待处理的应用（Uninstall为true时走卸载，否则走停用）
        /// </summary>
        private class AppAction
        {
            public string Name;
            public string Package;
            public bool Uninstall;
        }

        private static readonly AppAction[] AppActions = new[]
        {
            new AppAction { Name = "电话", Package = "com.android.dialer" },
            new AppAction { Name = "通讯录", Package = "com.android.contacts" },
            new AppAction { Name = "短信", Package = "com.android.mms" },
            new AppAction { Name = "SIM卡工具包", Package = "com.android.stk" },
            new AppAction { Name = "FM电台", Package = "com.android.music" },
            new AppAction { Name = "音乐", Package = "com.android.fmradio" },
            new AppAction { Name = "MTK（联发科）非框架定位服务", Package = "com.mediatek.gnss.nonframeworklbs" },
            new AppAction { Name = "Debug调试记录", Package = "com.debug.loggerui" },
            new AppAction { Name = "时钟", Package = "com.android.deskclock" },
            new AppAction { Name = "电子邮件", Package = "com.android.email", Uninstall = true },
            new AppAction { Name = "Search", Package = "com.android.quicksearchbox", Uninstall = true },
        };

        private readonly ObservableCollection<OperateLog> operateLogList = new ObservableCollection<OperateLog>();

        public string AdbPath { get; set; }

        public Device DeviceInfo { get; set; }


        public AlpsK71v1Window()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            logs.ItemsSource = operateLogList;

            ExecuteNext(0);
        }

        /// <summary>
        /// 按顺序执行下一个处理项（CmdExecutor的回调已在UI线程，可直接操作界面）
        /// </summary>
        private void ExecuteNext(int index)
        {
            if (index >= AppActions.Length)
            {
                PrintEndLog();
                return;
            }

            AppAction action = AppActions[index];
            string cmd = action.Uninstall
                ? $"{AdbPath} -s {DeviceInfo.DeviceMark} uninstall {action.Package} "
                : $"{AdbPath} -s {DeviceInfo.DeviceMark} shell pm disable-user {action.Package} ";

            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { cmd },
                result =>
                {
                    PrintLog(action, action.Uninstall ? "卸载" : "停用", Brushes.Green, cmd);
                    ExecuteNext(index + 1);
                },
                err =>
                {
                    PrintLog(action, "失败", Brushes.Red, cmd);
                    ExecuteNext(index + 1);
                    return true;
                });
        }

        private void PrintLog(AppAction action, string status, Brush statusColor, string cmd)
        {
            operateLogList.Add(new OperateLog
            {
                Message = $"应用【{action.Name}】处理结果",
                Status = status,
                StatusColor = statusColor,
                Cmd = cmd
            });
        }

        private void PrintEndLog()
        {
            operateLogList.Add(new OperateLog
            {
                Message = "执行完成",
                Status = "",
                StatusColor = Brushes.Black,
                Cmd = ""
            });
        }

        private void retry_Click(object sender, RoutedEventArgs e)
        {
            int index = logs.SelectedIndex;
            if (index < 0 || index >= operateLogList.Count)
            {
                return;
            }
            Clipboard.SetText(operateLogList[index].Cmd);
        }
    }
}
