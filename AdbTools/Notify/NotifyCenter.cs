using System;
using System.Windows;
using AdbTools.Tray;
using Hardcodet.Wpf.TaskbarNotification;

namespace AdbTools.Notify
{
    /// <summary>通知的语义级别，仅用于选图标。</summary>
    internal enum NotifyKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// 通知统一入口：统一走 Windows 通知（Toast），发不出去再退回托盘气泡。
    /// 前台也照常提醒——程序内不再弹结果对话框，通知就是唯一的反馈。
    /// </summary>
    internal static class NotifyCenter
    {
        /// <summary>
        /// 发一条通知。返回 true 表示已送达（Toast 或托盘气泡），false 表示两种渠道都没发出去。
        /// </summary>
        /// <param name="message">正文，第一行会作为通知标题，其余作为内容</param>
        /// <param name="kind">语义级别，仅用于气泡兜底时的图标</param>
        /// <param name="tag">同类通知的分组标识</param>
        public static bool Inform(string message, NotifyKind kind = NotifyKind.Info, string tag = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            Split(message, out string title, out string body);

            if (ToastService.TryShow(title, body, tag, App.NotificationTitle)) return true;
            return ShowBalloon(title, body, kind);
        }

        /// <summary>
        /// 操作结果类通知（如批量安装结果）：先交给系统通知；
        /// 若用户关闭了这类通知，或通知渠道不可用，则用对话框兜底，保证结果不会没人看到。
        /// 只适用于“结果丢了会影响用户判断”的场景，可忽略的事件不要用这个入口。
        /// </summary>
        /// <param name="notifyAllowed">对应的通知开关是否为开</param>
        /// <param name="owner">对话框的宿主窗口，可空（自动使用主窗口）</param>
        public static void InformResult(string message, NotifyKind kind, string tag,
            bool notifyAllowed, Window owner = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (notifyAllowed && Inform(message, kind, tag)) return;

            Split(message, out string title, out string body);
            ShowDialog(title, body, kind, owner);
        }

        /// <summary>通知发不出去时的兜底对话框；不在 UI 线程时自动切回去。</summary>
        private static void ShowDialog(string title, string body, NotifyKind kind, Window owner)
        {
            Application app = Application.Current;
            if (app == null) return;

            Action show = () =>
            {
                try
                {
                    // 宿主窗口必须先可见，否则对话框会被隐藏的托盘窗口拖到后台
                    Window host = owner != null && owner.IsLoaded && owner.IsVisible
                        ? owner
                        : null;

                    string text = string.IsNullOrWhiteSpace(body) ? title : body;
                    string caption = string.IsNullOrWhiteSpace(title) ? App.NotificationTitle : title;

                    if (host != null)
                    {
                        MessageBox.Show(host, text, caption, MessageBoxButton.OK, ToBoxIcon(kind));
                    }
                    else
                    {
                        MessageBox.Show(text, caption, MessageBoxButton.OK, ToBoxIcon(kind));
                    }
                }
                catch
                {
                    // 提示本身失败不再抛出，不能因为“提示失败”把主流程弄崩
                }
            };

            if (app.CheckAccess()) show();
            else app.Dispatcher.BeginInvoke(show);
        }

        private static MessageBoxImage ToBoxIcon(NotifyKind kind)
        {
            switch (kind)
            {
                case NotifyKind.Error:
                    return MessageBoxImage.Error;
                case NotifyKind.Warning:
                    return MessageBoxImage.Warning;
                default:
                    return MessageBoxImage.Information;
            }
        }

        /// <summary>把“事件 + 详情”的多行文案拆成标题与正文。</summary>
        private static void Split(string message, out string title, out string body)
        {
            string text = message.Replace("\r\n", "\n").Trim();
            int index = text.IndexOf('\n');

            if (index < 0)
            {
                title = App.NotificationTitle;
                body = text;
                return;
            }

            title = text.Substring(0, index).Trim();
            body = text.Substring(index + 1).Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                title = App.NotificationTitle;
            }
        }

        /// <summary>Toast 不可用（无快捷方式、系统版本偏低、被策略限制）时的兜底。</summary>
        private static bool ShowBalloon(string title, string body, NotifyKind kind)
        {
            TrayIconHost tray = App.TrayIcon;
            if (tray == null) return false;

            return tray.Notify(string.IsNullOrWhiteSpace(title) ? App.NotificationTitle : title,
                body, ToBalloonIcon(kind));
        }

        private static BalloonIcon ToBalloonIcon(NotifyKind kind)
        {
            switch (kind)
            {
                case NotifyKind.Error:
                    return BalloonIcon.Error;
                case NotifyKind.Warning:
                    return BalloonIcon.Warning;
                default:
                    return BalloonIcon.Info;
            }
        }
    }
}
