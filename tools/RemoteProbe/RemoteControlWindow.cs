using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemoteProbe
{
    /// <summary>
    /// 远程控制窗口（M1/M2 验证）：实时显示设备画面，鼠标点击/拖动与键盘输入直接注入设备。
    /// </summary>
    internal sealed class RemoteControlWindow : Window
    {
        private readonly string _adbPath;
        private readonly string _serial;

        private readonly Image _image = new Image();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBox _textSink = new TextBox();

        private AdbVideoStream _stream;
        private H264DecoderMft _decoder;
        private InputInjector _injector;
        private AccessUnitSplitter _splitter;
        private Thread _pumpThread;

        private WriteableBitmap _bitmap;
        private byte[] _front;
        private byte[] _back;
        private readonly object _frameLock = new object();
        private bool _framePending;
        private int _frameWidth;
        private int _frameHeight;

        private int _decodedFrames;
        private int _lastDecodedFrames;
        private double _fps;

        private bool _dragging;
        private bool _moved;
        private Point _startPoint;
        private Point _lastSentPoint;
        private DateTime _lastMoveSent = DateTime.MinValue;

        public RemoteControlWindow(string adbPath, string serial, string size, int bitRate)
        {
            _adbPath = adbPath;
            _serial = serial;

            Title = "远程控制 - " + serial;
            Width = 420;
            Height = 780;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = System.Windows.Media.Brushes.Black;

            var panel = new DockPanel { LastChildFill = true };

            _status.Text = "正在连接…";
            _status.Foreground = System.Windows.Media.Brushes.White;
            _status.Background = System.Windows.Media.Brushes.Black;
            _status.Padding = new Thickness(8, 4, 8, 4);
            _status.FontSize = 12;
            DockPanel.SetDock(_status, Dock.Top);
            panel.Children.Add(_status);

            _image.Stretch = System.Windows.Media.Stretch.Uniform;
            _image.HorizontalAlignment = HorizontalAlignment.Center;
            _image.VerticalAlignment = VerticalAlignment.Center;
            _image.MouseLeftButtonDown += Image_MouseLeftButtonDown;
            _image.MouseMove += Image_MouseMove;
            _image.MouseLeftButtonUp += Image_MouseLeftButtonUp;
            panel.Children.Add(_image);

            // 隐藏的文本框负责接收键盘字符输入（WPF 文本输入需要可聚焦元素）
            _textSink.Width = 1;
            _textSink.Height = 1;
            _textSink.Opacity = 0;
            _textSink.IsTabStop = false;
            _textSink.PreviewTextInput += TextSink_PreviewTextInput;
            DockPanel.SetDock(_textSink, Dock.Bottom);
            panel.Children.Add(_textSink);

            Content = panel;

            Loaded += Window_Loaded;
            Closed += Window_Closed;
            PreviewKeyDown += Window_PreviewKeyDown;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            StartPipeline(size, bitRate);
        }

        private void StartPipeline(string size, int bitRate)
        {
            MfInterop.Check(MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_FULL), "MFStartup");

            _injector = new InputInjector(_adbPath, _serial);
            _stream = new AdbVideoStream(_adbPath, _serial, size, bitRate);
            _stream.WakeUpDevice();
            _stream.Log += msg => Dispatcher.BeginInvoke(new Action(() => _status.Text = msg));

            _decoder = new H264DecoderMft();
            _decoder.FrameDecoded += OnFrameDecoded;

            _splitter = new AccessUnitSplitter();
            _splitter.AccessUnit += (au, keyFrame) =>
            {
                try { _decoder.Feed(au, keyFrame); }
                catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => _status.Text = "解码错误：" + ex.Message)); }
            };

            _pumpThread = new Thread(() => _stream.Pump((data, count) => _splitter.Feed(data, count)))
            {
                IsBackground = true,
                Name = "adb-stream"
            };
            _pumpThread.Start();
        }

        private static bool _firstFrameLogged;

        private void OnFrameDecoded(IntPtr nv12, int length, int width, int height)
        {
            Interlocked.Increment(ref _decodedFrames);
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                Console.WriteLine("[UI] 首帧 " + width + "x" + height + "（" + length + " 字节 NV12）");
            }

            int need = width * height * 4;
            byte[] buffer = _back;
            if (buffer == null || buffer.Length < need) buffer = new byte[need];
            if (!Nv12Converter.Convert(nv12, length, width, height, buffer)) return;

            lock (_frameLock)
            {
                _back = _front;
                _front = buffer;
                _frameWidth = width;
                _frameHeight = height;
                _framePending = true;
            }

            Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
        }

        private void RenderFrame()
        {
            byte[] frame;
            int width, height;
            lock (_frameLock)
            {
                if (!_framePending) return;
                _framePending = false;
                frame = _front;
                width = _frameWidth;
                height = _frameHeight;
            }
            if (frame == null || width <= 0 || height <= 0) return;

            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
        }

        private void UpdateStatus()
        {
            int now = _decodedFrames;
            _fps = (now - _lastDecodedFrames) * 2.0; // 500ms 采样
            _lastDecodedFrames = now;

            string size = _bitmap == null ? "-" : _bitmap.PixelWidth + "x" + _bitmap.PixelHeight;
            _status.Text = string.Format("{0}  {1:F1} fps  片段 {2}  |  点击/拖动控制，Esc 返回",
                size, _fps, _stream != null ? _stream.SegmentCount : 0);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _textSink.Focus();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_injector == null) return;

            switch (e.Key)
            {
                case Key.Escape: _injector.KeyEvent(4); e.Handled = true; return;
                case Key.Enter: _injector.KeyEvent(66); e.Handled = true; return;
                case Key.Back: _injector.KeyEvent(67); e.Handled = true; return;
                case Key.Home: _injector.KeyEvent(3); e.Handled = true; return;
                case Key.Delete: _injector.KeyEvent(112); e.Handled = true; return;
                case Key.Up: _injector.KeyEvent(19); e.Handled = true; return;
                case Key.Down: _injector.KeyEvent(20); e.Handled = true; return;
                case Key.Left: _injector.KeyEvent(21); e.Handled = true; return;
                case Key.Right: _injector.KeyEvent(22); e.Handled = true; return;
            }
        }

        private void TextSink_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_injector != null) _injector.Text(e.Text);
            e.Handled = true;
            _textSink.Clear();
        }

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _textSink.Focus();
            int x, y;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y)) return;

            _dragging = true;
            _moved = false;
            _startPoint = new Point(x, y);
            _lastSentPoint = _startPoint;
            _lastMoveSent = DateTime.MinValue;
            _image.CaptureMouse();

            // Android 11+ 直接按下，拖动才有连续反馈；旧系统在抬起时用 swipe 模拟
            if (_injector != null && _injector.SupportsMotionEvent)
            {
                _injector.MotionDown(x, y);
            }
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _injector == null) return;

            int x, y;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y)) return;

            if (!_moved && (Math.Abs(x - _startPoint.X) > 6 || Math.Abs(y - _startPoint.Y) > 6))
            {
                _moved = true;
            }
            if (!_moved) return;

            if (_injector.SupportsMotionEvent)
            {
                // 限制发送频率，避免淹没 adb shell
                if ((DateTime.UtcNow - _lastMoveSent).TotalMilliseconds < 30) return;
                _lastMoveSent = DateTime.UtcNow;
                _lastSentPoint = new Point(x, y);
                _injector.MotionMove(x, y);
            }
        }

        private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _image.ReleaseMouseCapture();

            int x, y;
            bool mapped = TryMapToDevice(e.GetPosition(_image), out x, out y);
            if (!mapped)
            {
                x = (int)_lastSentPoint.X;
                y = (int)_lastSentPoint.Y;
            }

            if (_injector == null) return;

            if (_injector.SupportsMotionEvent)
            {
                if (_moved) _injector.MotionUp(x, y);
                else _injector.MotionUp((int)_startPoint.X, (int)_startPoint.Y);
                return;
            }

            if (_moved)
            {
                _injector.Swipe((int)_startPoint.X, (int)_startPoint.Y, x, y, 200);
            }
            else
            {
                _injector.Tap((int)_startPoint.X, (int)_startPoint.Y);
            }
        }

        /// <summary>把窗口内坐标换算成设备像素坐标（按等比缩放后的实际显示区域）。</summary>
        private bool TryMapToDevice(Point point, out int deviceX, out int deviceY)
        {
            deviceX = 0;
            deviceY = 0;
            if (_bitmap == null) return false;

            double viewWidth = _image.ActualWidth;
            double viewHeight = _image.ActualHeight;
            double bitmapWidth = _bitmap.PixelWidth;
            double bitmapHeight = _bitmap.PixelHeight;
            if (viewWidth <= 0 || viewHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0) return false;

            double scale = Math.Min(viewWidth / bitmapWidth, viewHeight / bitmapHeight);
            double drawWidth = bitmapWidth * scale;
            double drawHeight = bitmapHeight * scale;
            double offsetX = (viewWidth - drawWidth) / 2;
            double offsetY = (viewHeight - drawHeight) / 2;

            double px = (point.X - offsetX) / scale;
            double py = (point.Y - offsetY) / scale;
            if (px < 0 || py < 0 || px >= bitmapWidth || py >= bitmapHeight) return false;

            deviceX = (int)px;
            deviceY = (int)py;
            return true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            try { if (_stream != null) _stream.Stop(); } catch { }
            try { if (_pumpThread != null) _pumpThread.Join(1000); } catch { }
            try { if (_injector != null) _injector.Dispose(); } catch { }
            try { if (_decoder != null) _decoder.Dispose(); } catch { }
            try { MfInterop.MFShutdown(); } catch { }
        }
    }
}
