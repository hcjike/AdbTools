using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace AdbTools
{
    /// <summary>
    /// 拖放提示的兜底巡检：OLE 有概率漏发 DragLeave（快速拖出窗口时尤其明显），
    /// 提示就会一直挂在界面上。这里只看真实鼠标状态——左键已松开（拖拽已结束），
    /// 或指针已经离开窗口——就直接回调收尾。
    /// 只在提示可见期间每 200ms 巡检一次，收起提示后立即停止。
    /// </summary>
    internal sealed class DragHintWatchdog
    {
        private const int VkLeftButton = 0x01;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly Window _window;
        private readonly Func<bool> _isHintVisible;
        private readonly Action _hide;
        private readonly DispatcherTimer _timer;

        /// <param name="window">提示所在的窗口，用来判断指针是否还在窗口内。</param>
        /// <param name="isHintVisible">提示当前是否可见；不可见时自动停止巡检。</param>
        /// <param name="hide">收起提示（调用方自己实现，需可重复调用）。</param>
        public DragHintWatchdog(Window window, Func<bool> isHintVisible, Action hide)
        {
            _window = window;
            _isHintVisible = isHintVisible;
            _hide = hide;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (s, e) => Check();
        }

        /// <summary>提示显示时调用；重复调用无副作用。</summary>
        public void Start()
        {
            if (!_timer.IsEnabled) _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private void Check()
        {
            if (!_isHintVisible() || !_window.IsVisible)
            {
                _timer.Stop();
                return;
            }

            // 最高位为 1 表示当前按下；拖拽结束后该键一定是松开的
            if ((GetAsyncKeyState(VkLeftButton) & 0x8000) == 0)
            {
                StopAndHide();
                return;
            }

            Point cursor;
            try
            {
                cursor = _window.PointFromScreen(
                    new Point(WinForms.Cursor.Position.X, WinForms.Cursor.Position.Y));
            }
            catch (InvalidOperationException)
            {
                // 窗口还没有呈现源或正在关闭，下一轮再判
                return;
            }

            if (cursor.X < 0 || cursor.Y < 0
                || cursor.X > _window.ActualWidth || cursor.Y > _window.ActualHeight)
            {
                StopAndHide();
            }
        }

        private void StopAndHide()
        {
            _timer.Stop();
            _hide();
        }
    }
}
