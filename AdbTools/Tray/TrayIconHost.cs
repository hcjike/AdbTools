using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AdbTools.bean;
using Hardcodet.Wpf.TaskbarNotification;

namespace AdbTools.Tray
{
    /// <summary>
    /// 系统托盘图标：左键唤出主窗口，右键菜单提供常用操作，并负责通知气泡。
    /// 菜单项全部转调主窗口已实现的方法，这里不保存任何业务状态。
    /// </summary>
    internal sealed class TrayIconHost : IDisposable
    {
        private readonly TaskbarIcon _icon;
        private readonly MenuItem _remoteMenuItem;
        private readonly MenuItem _appListMenuItem;
        private readonly MenuItem _shellMenuItem;

        private bool _disposed;

        public TrayIconHost()
        {
            _remoteMenuItem = new MenuItem { Header = "远程控制" };
            _appListMenuItem = new MenuItem { Header = "应用列表" };
            _shellMenuItem = new MenuItem { Header = "Shell 窗口" };

            // 先放占位项，保证没有设备时子菜单箭头也能显示，展开时会被真实内容替换
            _remoteMenuItem.Items.Add(new MenuItem { Header = "（暂无已连接设备）", IsEnabled = false });
            _appListMenuItem.Items.Add(new MenuItem { Header = "（暂无已连接设备）", IsEnabled = false });
            _shellMenuItem.Items.Add(new MenuItem { Header = "（暂无已连接设备）", IsEnabled = false });

            // 设备子菜单在展开时才填充，保证内容始终与当前设备列表一致
            _remoteMenuItem.SubmenuOpened += (s, e) => FillDeviceMenu(_remoteMenuItem, RemoteControlDevice);
            _appListMenuItem.SubmenuOpened += (s, e) => FillDeviceMenu(_appListMenuItem, OpenAppListDevice);
            _shellMenuItem.SubmenuOpened += (s, e) => FillDeviceMenu(_shellMenuItem, OpenShellDevice);

            ContextMenu menu = new ContextMenu();
            menu.Items.Add(CreateItem("显示主界面", (s, e) => App.ShowMainWindow()));
            menu.Items.Add(CreateItem("刷新设备", (s, e) => App.MainWindowInstance?.TrayRefreshDevices()));
            menu.Items.Add(new Separator());
            menu.Items.Add(_remoteMenuItem);
            menu.Items.Add(_appListMenuItem);
            menu.Items.Add(_shellMenuItem);
            menu.Items.Add(CreateItem("断开全部设备", (s, e) => App.MainWindowInstance?.TrayDisconnectAll()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem("检查更新", (s, e) => App.MainWindowInstance?.TrayCheckUpdate()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem("退出", (s, e) => App.ConfirmAndExit()));

            _icon = new TaskbarIcon
            {
                ToolTipText = "Adb连接工具",
                ContextMenu = menu,
                // 不等待双击判定，单击立刻响应
                NoLeftClickDelay = true
            };

            // 图标解码失败不应该让整个托盘失效：先建好图标与菜单，再单独设置图片
            try
            {
                _icon.IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/icon.ico"));
            }
            catch
            {
                // 缺少图标资源时托盘仍可显示系统默认空图标
            }

            // 左键单击切换主窗口显示/隐藏；不注册双击，避免一次双击触发两次切换
            _icon.TrayLeftMouseUp += (s, e) => App.ToggleMainWindow();

            _icon.Visibility = Visibility.Visible;
        }

        /// <summary>托盘提示文本：版本 + 当前设备数。</summary>
        public void UpdateStatus(int deviceCount)
        {
            if (_disposed || _icon == null) return;

            string text = "Adb连接工具 v" + App.Version;
            text += deviceCount > 0 ? "\r\n已连接 " + deviceCount + " 台设备" : "\r\n暂无已连接设备";
            _icon.ToolTipText = text;
        }

        /// <summary>弹一条系统通知（只做提醒，不含点击动作）。</summary>
        public void Notify(string title, string message, BalloonIcon icon)
        {
            if (_disposed || _icon == null) return;
            try
            {
                _icon.ShowBalloonTip(title, message, icon);
            }
            catch
            {
                // 通知失败不影响主流程
            }
        }

        private void FillDeviceMenu(MenuItem parent, Action<Device> action)
        {
            parent.Items.Clear();

            List<Device> devices = App.MainWindowInstance?.GetTrayDevices();
            if (devices == null || devices.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "（暂无已连接设备）", IsEnabled = false });
                return;
            }

            foreach (Device device in devices)
            {
                Device target = device;
                MenuItem item = new MenuItem { Header = device.ShowDeviceName };
                item.Click += (s, e) => action(target);
                parent.Items.Add(item);
            }
        }

        private static void RemoteControlDevice(Device device)
        {
            App.MainWindowInstance?.TrayOpenRemoteControl(device);
        }

        private static void OpenAppListDevice(Device device)
        {
            App.MainWindowInstance?.TrayOpenAppList(device);
        }

        private static void OpenShellDevice(Device device)
        {
            App.MainWindowInstance?.TrayOpenShell(device);
        }

        private static MenuItem CreateItem(string header, RoutedEventHandler handler)
        {
            MenuItem item = new MenuItem { Header = header };
            item.Click += handler;
            return item;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _icon.Visibility = Visibility.Collapsed;
                _icon.Dispose();
            }
            catch
            {
                // 退出流程中忽略
            }
        }
    }
}
