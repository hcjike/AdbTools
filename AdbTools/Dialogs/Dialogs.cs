using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;

namespace AdbTools
{
    /// <summary>
    /// 全局对话框入口：统一使用自绘的 <see cref="MessageDialog"/>。
    /// 除启动失败、未捕获异常与系统通知兜底外，业务代码不要再直接调用 MessageBox。
    /// 显示失败时会退回系统 MessageBox，保证提示本身不会把主流程带崩。
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>
        /// 显示一条提示，只有一个“确定”按钮；Enter / Esc 都能关闭。
        /// </summary>
        /// <param name="icon">图标类型；传 <see cref="MessageBoxImage.None"/> 时不显示图标</param>
        public static void Show(string text, string caption = "提示",
            MessageBoxImage icon = MessageBoxImage.None, Window owner = null)
        {
            Show(null, text, caption, icon, owner);
        }

        /// <summary>
        /// 显示一条富文本提示：调用方用 <see cref="DialogText"/> 拼装彩色 Run、加粗与链接。
        /// </summary>
        /// <param name="buildContent">拼装正文 Inlines 的回调</param>
        /// <param name="plainText">纯文本版本，用于复制与自绘失败时回退系统对话框</param>
        public static void Show(Action<InlineCollection> buildContent, string plainText,
            string caption = "提示", MessageBoxImage icon = MessageBoxImage.None,
            Window owner = null)
        {
            Window host = ResolveOwner(owner);
            try
            {
                MessageDialog dialog = Create(buildContent, plainText, caption, icon, false,
                    "确定", "取消", false, host);
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Write("自绘提示框显示失败，改用系统对话框：{0}", ex.Message);
                ShowNative(host, plainText, caption, MessageBoxButton.OK, icon);
            }
        }

        /// <summary>
        /// 显示一个确认框：Enter 触发主按钮，Esc / 关闭按钮取消。
        /// </summary>
        /// <param name="confirmText">主按钮文案，按动作定制，如“删除”“清空”“卸载”</param>
        /// <param name="danger">危险操作：主按钮显示红色</param>
        /// <returns>点主按钮返回 true，其余情况返回 false</returns>
        public static bool Confirm(string text, string caption = "确认", Window owner = null,
            string confirmText = "确定", bool danger = false,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            return Confirm(null, text, caption, owner, confirmText, danger, icon);
        }

        /// <summary>
        /// 富文本确认框：正文可拼装彩色 Run 与链接，其余行为与纯文本确认框一致。
        /// </summary>
        public static bool Confirm(Action<InlineCollection> buildContent, string plainText,
            string caption = "确认", Window owner = null,
            string confirmText = "确定", bool danger = false,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            Window host = ResolveOwner(owner);
            try
            {
                MessageDialog dialog = Create(buildContent, plainText, caption, icon, true,
                    confirmText, "取消", danger, host);
                return dialog.ShowDialog() == true;
            }
            catch (Exception ex)
            {
                AppLog.Write("自绘确认框显示失败，改用系统对话框：{0}", ex.Message);
                return ShowNative(host, plainText, caption, MessageBoxButton.OKCancel, icon)
                    == MessageBoxResult.OK;
            }
        }

        private static MessageDialog Create(Action<InlineCollection> buildContent, string plainText,
            string caption, MessageBoxImage icon, bool confirm, string confirmText,
            string cancelText, bool danger, Window owner)
        {
            var dialog = new MessageDialog(buildContent, plainText, caption, icon,
                confirm, confirmText, cancelText, danger);

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dialog.Topmost = owner.Topmost;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return dialog;
        }

        /// <summary>
        /// 解析宿主窗口：优先用调用方传入的可见窗口，其次是当前激活的可见窗口，
        /// 最后退回主窗口。主窗口最小化到托盘时不拿它当宿主，避免对话框被带到后台。
        /// </summary>
        private static Window ResolveOwner(Window owner)
        {
            try
            {
                if (IsUsable(owner)) return owner;

                Application app = Application.Current;
                if (app == null) return null;

                Window active = app.Windows.OfType<Window>()
                    .FirstOrDefault(window => window.IsActive && IsUsable(window));
                if (active != null) return active;

                return IsUsable(app.MainWindow) ? app.MainWindow : null;
            }
            catch (Exception)
            {
                // 非 UI 线程等异常场景：不设宿主，交由自绘窗口失败后回退系统对话框
                return null;
            }
        }

        private static bool IsUsable(Window window)
        {
            return window != null
                && window.IsLoaded
                && window.IsVisible
                && window.WindowState != WindowState.Minimized;
        }

        private static MessageBoxResult ShowNative(Window owner, string text, string caption,
            MessageBoxButton button, MessageBoxImage icon)
        {
            try
            {
                if (icon == MessageBoxImage.None) icon = MessageBoxImage.Information;

                return owner != null
                    ? MessageBox.Show(owner, text, caption, button, icon)
                    : MessageBox.Show(text, caption, button, icon);
            }
            catch (Exception)
            {
                // 连系统对话框都弹不出来时只能放弃，不能把提示失败变成新的异常
                return MessageBoxResult.None;
            }
        }
    }
}
