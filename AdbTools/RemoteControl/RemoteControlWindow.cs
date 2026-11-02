using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AdbTools.RemoteControl
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

        /// <summary>仿手机外框：包裹画面并按窗口大小等比缩放。</summary>
        private readonly Border _phoneFrame = new Border();

        /// <summary>画面宿主区域（占满可用空间），用于计算外框尺寸。</summary>
        private Grid _screenHost;

        /// <summary>底部虚拟按键栏（自适应窗口尺寸时用于计算高度）。</summary>
        private StackPanel _navBar;

        /// <summary>是否已按首帧画面比例自动调整过窗口尺寸。</summary>
        private bool _autoFitted;

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

        /// <summary>最近一次注入的命令，显示在状态栏便于确认点击是否落点正确。</summary>
        private string _lastInject = "";

        /// <summary>是否已有待渲染任务在 UI 队列中（用于合并/丢弃过期帧）。</summary>
        private int _renderQueued;

        /// <summary>是否已收到首帧；收到后状态栏才显示实时指标。</summary>
        private bool _connected;

        /// <summary>窗口创建到首帧显示的耗时（毫秒）。</summary>
        private double _firstFrameMs = -1;

        private readonly DateTime _createdTime = DateTime.UtcNow;

        /// <summary>最近一条取流/解码日志；未出首帧时显示在状态栏，便于定位问题。</summary>
        private volatile string _lastLog = "";

        private bool _dragging;
        private bool _moved;
        private Point _startPoint;
        private Point _lastPoint;
        private DateTime _dragStartTime = DateTime.UtcNow;

        /// <summary>设备物理分辨率（输入注入使用物理像素，而视频流是缩放后的尺寸）。</summary>
        private int _deviceWidth;
        private int _deviceHeight;

        /// <summary>最近一次已知的显示方向与逻辑分辨率，-1 表示未知。</summary>
        private int _rotation = -1;
        private int _logicalWidth;
        private int _logicalHeight;

        public RemoteControlWindow(string adbPath, string serial, string size, int bitRate)
        {
            _adbPath = adbPath;
            _serial = serial;
            LogToFile("=== 打开远程控制: adb=" + adbPath + " serial=" + serial + " size=" + size + " ===");
            QueryPhysicalSize();

            Title = "远程控制 - " + serial;
            // 默认按手机比例（约 9:19.5）+ 机身内边距给窗口尺寸，减少两侧黑边
            Width = 500;
            Height = 880;
            MinWidth = 320;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // 与机身接近的深色背景：机身外的留白不显得突兀
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0E, 0x0E, 0x12));

            // 四行：状态栏 / 手机外框（占满剩余空间）/ 虚拟按键栏 / 隐藏输入框
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _status.Text = "正在连接…";
            _status.Foreground = System.Windows.Media.Brushes.White;
            _status.Background = System.Windows.Media.Brushes.Black;
            _status.Padding = new Thickness(8, 4, 8, 4);
            _status.FontSize = 12;
            // 文本较长时自动换行，避免被窗口裁掉
            _status.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_status, 0);
            root.Children.Add(_status);

            // 画面：等比填充在手机外框内部，外框尺寸随窗口缩放同步调整
            _image.Stretch = System.Windows.Media.Stretch.Uniform;
            _image.HorizontalAlignment = HorizontalAlignment.Stretch;
            _image.VerticalAlignment = VerticalAlignment.Stretch;
            _image.MouseLeftButtonDown += Image_MouseLeftButtonDown;
            _image.MouseMove += Image_MouseMove;
            _image.MouseLeftButtonUp += Image_MouseLeftButtonUp;
            _image.MouseWheel += Image_MouseWheel;

            // 仿手机外框：深色机身 + 圆角 + 内边距 + 投影
            _phoneFrame.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x1A));
            _phoneFrame.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
            _phoneFrame.BorderThickness = new Thickness(1);
            _phoneFrame.CornerRadius = new CornerRadius(28);
            _phoneFrame.Padding = new Thickness(10);
            _phoneFrame.HorizontalAlignment = HorizontalAlignment.Center;
            _phoneFrame.VerticalAlignment = VerticalAlignment.Center;
            _phoneFrame.Child = _image;
            _phoneFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 0,
                Color = System.Windows.Media.Colors.Black,
                Opacity = 0.75
            };

            _screenHost = new Grid();
            _screenHost.Children.Add(_phoneFrame);
            _screenHost.SizeChanged += (s, e) => UpdatePhoneFrame();
            _image.SizeChanged += (s, e) => UpdatePhoneFrame();
            Grid.SetRow(_screenHost, 1);
            root.Children.Add(_screenHost);

            // 虚拟按键：小尺寸、在窗口下方居中摆放
            var navBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 8)
            };
            navBar.Children.Add(CreateNavButton("返回", 4, CreateBackIcon()));
            navBar.Children.Add(CreateNavButton("Home", 3, CreateHomeIcon()));
            navBar.Children.Add(CreateNavButton("任务", 187, CreateRecentsIcon()));
            Grid.SetRow(navBar, 2);
            root.Children.Add(navBar);
            _navBar = navBar;

            // 隐藏的文本框负责接收键盘字符输入（WPF 文本输入需要可聚焦元素）
            _textSink.Width = 1;
            _textSink.Height = 1;
            _textSink.Opacity = 0;
            _textSink.IsTabStop = false;
            _textSink.PreviewTextInput += TextSink_PreviewTextInput;
            Grid.SetRow(_textSink, 3);
            root.Children.Add(_textSink);

            Content = root;

            Loaded += Window_Loaded;
            Closed += Window_Closed;
            PreviewKeyDown += Window_PreviewKeyDown;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            // 静止画面时补发最后一帧：否则该帧要等到下一次画面变化才会送进解码器
            var pendingFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pendingFrameTimer.Tick += (s, e) =>
            {
                AccessUnitSplitter splitter = _splitter;
                if (splitter == null) return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { splitter.FlushPending(); } catch { }
                });
            };
            pendingFrameTimer.Start();

            // 用设备物理比例反推采集尺寸：长边 1280，短边按比例取偶数，
            // 否则指定的固定尺寸（如 720x1280）与设备比例不符，画面左右会被加黑边。
            // 启动时设备可能已经是横屏，按实际方向取尺寸，避免先用竖屏尺寸再等 2 秒纠正。
            bool landscapeAtStart = QueryStartupLandscape();
            StartPipeline(ComputeCaptureSize(landscapeAtStart, size), bitRate);

            // 横竖屏切换检测：定期查询 SurfaceOrientation，变化后用新尺寸重建取流
            var rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            rotationTimer.Tick += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                try { CheckRotation(); } catch { }
            });
            rotationTimer.Start();
        }

        private void StartPipeline(string size, int bitRate)
        {
            MfInterop.Startup();

            _stream = new AdbVideoStream(_adbPath, _serial, size, bitRate);
            _stream.Log += msg =>
            {
                _lastLog = msg;
                LogToFile(msg);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_connected) _status.Text = "等待首帧… " + msg;
                }));
            };

            _decoder = new H264DecoderMft { Verbose = false, LogSink = LogToFile };
            _decoder.FrameDecoded += OnFrameDecoded;

            _splitter = new AccessUnitSplitter();
            int auSeen = 0;
            _splitter.AccessUnit += (au, keyFrame) =>
            {
                try
                {
                    if (auSeen++ == 0)
                    {
                        LogToFile("首个 AU " + au.Length + " 字节，头部 "
                            + BitConverter.ToString(au, 0, Math.Min(8, au.Length)) + " 关键帧=" + keyFrame);
                    }
                    _decoder.Feed(au, keyFrame);
                    if (auSeen % 60 == 0)
                    {
                        LogToFile("AU=" + auSeen + " 解码=" + _decoder.DecodedFrames);
                    }
                }
                catch (Exception ex)
                {
                    _lastLog = "解码错误：" + ex.Message;
                    LogToFile(_lastLog);
                    Dispatcher.BeginInvoke(new Action(() => _status.Text = _lastLog));
                }
            };

            // 先唤醒设备再取流：息屏状态下 screenrecord 可能完全不产帧，
            // 会白白等看门狗超时，反而拖慢首帧。
            // 唤醒与解锁必须在取流前完成：与取流并发执行 adb 命令会导致首个 screenrecord 空跑。
            // 已合并为单次 adb 调用，耗时约 0.3~0.5 秒。
            try { _stream.WakeUpDevice(); } catch { }

            _pumpThread = new Thread(() => _stream.Pump((data, count) => _splitter.Feed(data, count)))
            {
                IsBackground = true,
                Name = "adb-stream"
            };
            _pumpThread.Start();

            // 查询分辨率、创建输入通道放后台，避免拖慢首帧
            ThreadPool.QueueUserWorkItem(_ =>
            {
                QueryPhysicalSize();
                try { _injector = new InputInjector(_adbPath, _serial); } catch { }
            });
        }

        private void OnFrameDecoded(IntPtr nv12, int length, int sourceWidth, int sourceHeight,
            int cropX, int cropY, int width, int height)
        {
            Interlocked.Increment(ref _decodedFrames);
            if (!_connected)
            {
                _connected = true;
                _firstFrameMs = (DateTime.UtcNow - _createdTime).TotalMilliseconds;
                LogToFile(string.Format("首帧 {0}x{1}（源 {2}x{3}，裁剪 {4},{5}），用时 {6:F0} ms",
                    width, height, sourceWidth, sourceHeight, cropX, cropY, _firstFrameMs));
            }

            int need = width * height * 4;
            byte[] buffer = _back;
            if (buffer == null || buffer.Length < need) buffer = new byte[need];
            if (!Nv12Converter.Convert(nv12, length, sourceWidth, sourceHeight,
                    cropX, cropY, width, height, buffer)) return;

            lock (_frameLock)
            {
                _back = _front;
                _front = buffer;
                _frameWidth = width;
                _frameHeight = height;
                _framePending = true;
            }

            // 只在没有待渲染任务时排一次渲染：渲染跟不上时自动丢中间帧，
            // 保证画面始终是"最新一帧"，避免队列堆积导致延迟越来越大。
            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
            }
        }

        private void RenderFrame()
        {
            byte[] frame;
            int width, height;
            lock (_frameLock)
            {
                if (!_framePending)
                {
                    _renderQueued = 0;
                    return;
                }
                _framePending = false;
                frame = _front;
                width = _frameWidth;
                height = _frameHeight;
            }

            if (frame != null && width > 0 && height > 0)
            {
                if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    _image.Source = _bitmap;
                    UpdatePhoneFrame();
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
            }

            Interlocked.Exchange(ref _renderQueued, 0);

            // 渲染期间又到了新帧：补一次渲染，保持"总是显示最新帧"
            bool hasNewFrame;
            lock (_frameLock) hasNewFrame = _framePending;
            if (hasNewFrame && Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
            }
        }

        private void UpdateStatus()
        {
            int now = _decodedFrames;
            _fps = (now - _lastDecodedFrames) * 2.0; // 500ms 采样
            _lastDecodedFrames = now;

            if (!_connected)
            {
                _status.Text = string.IsNullOrEmpty(_lastLog)
                    ? "正在连接设备，等待首帧…"
                    : "等待首帧… " + _lastLog;
                return;
            }

            string size = _bitmap == null ? "-" : _bitmap.PixelWidth + "x" + _bitmap.PixelHeight;
            string device = _deviceWidth > 0 ? _deviceWidth + "x" + _deviceHeight : "-";
            string first = _firstFrameMs >= 0 ? string.Format("首帧 {0:F0}ms", _firstFrameMs) : "-";

            _status.Text = string.Format("{0} → {1}   {2:F1} fps   {3}\n片段 {4} · {5}",
                size, device, _fps, first, _stream != null ? _stream.SegmentCount : 0,
                string.IsNullOrEmpty(_lastInject) ? "左键点击/拖动控制，Esc 返回" : _lastInject);
        }

        /// <summary>把运行日志写到临时目录，便于排查“等待首帧”卡住的问题。</summary>
        private static void LogToFile(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "AdbTools_RemoteControl.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine);
            }
            catch { }
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
            _lastPoint = _startPoint;
            _dragStartTime = DateTime.UtcNow;
            _image.CaptureMouse();

            // 注意：`input motionevent` 每条命令都要起一次 Java 进程（约 150~300ms），
            // 无法支撑实时拖动，因此只记录起点，抬起时用一次 tap / swipe 完成注入。
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            int x, y;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y)) return;

            _lastPoint = new Point(x, y);
            if (!_moved && (Math.Abs(x - _startPoint.X) > 6 || Math.Abs(y - _startPoint.Y) > 6))
            {
                _moved = true;
            }
        }

        private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _image.ReleaseMouseCapture();

            int x, y;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y))
            {
                x = (int)_lastPoint.X;
                y = (int)_lastPoint.Y;
            }

            if (_injector == null) return;

            if (_moved)
            {
                // 用一次 swipe 复现整段拖动，耗时按真实手势时长换算（限制在 100~1500ms）
                double elapsed = (DateTime.UtcNow - _dragStartTime).TotalMilliseconds;
                int duration = (int)Math.Max(100, Math.Min(1500, elapsed));
                _injector.Swipe((int)_startPoint.X, (int)_startPoint.Y, x, y, duration);
                _lastInject = string.Format("swipe {0},{1} -> {2},{3} ({4}ms)",
                    (int)_startPoint.X, (int)_startPoint.Y, x, y, duration);
            }
            else
            {
                _injector.Tap((int)_startPoint.X, (int)_startPoint.Y);
                _lastInject = string.Format("tap {0},{1}", (int)_startPoint.X, (int)_startPoint.Y);
            }
        }

        /// <summary>滚轮：在鼠标位置做一次短距离上下滑动。</summary>
        private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_injector == null) return;

            int x, y;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y)) return;

            int distance = (int)Math.Max(120, Math.Min(600, Math.Abs(e.Delta) * 1.5));
            int from = e.Delta > 0 ? y + distance / 2 : y - distance / 2;
            int to = e.Delta > 0 ? y - distance / 2 : y + distance / 2;

            from = Clamp(from, 0, Math.Max(0, _deviceHeight - 1));
            to = Clamp(to, 0, Math.Max(0, _deviceHeight - 1));
            _injector.Swipe(x, from, x, to, 200);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>读取设备物理分辨率（wm size），注入坐标必须使用物理像素。</summary>
        private void QueryPhysicalSize()
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell wm size")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);

                    string overrideLine = null, physicalLine = null;
                    foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("Override size:", StringComparison.OrdinalIgnoreCase)) overrideLine = line;
                        else if (line.StartsWith("Physical size:", StringComparison.OrdinalIgnoreCase)) physicalLine = line;
                    }

                    string target = overrideLine ?? physicalLine;
                    if (target != null)
                    {
                        int colon = target.IndexOf(':');
                        if (colon > 0)
                        {
                            string[] parts = target.Substring(colon + 1).Trim().Split('x', 'X');
                            int w, h;
                            if (parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h))
                            {
                                _deviceWidth = w;
                                _deviceHeight = h;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>关闭窗口后取消“保持常亮”，恢复设备原有行为。</summary>
        private void RestoreDeviceState()
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell svc power stayon false")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }

        /// <summary>
        /// 按画面宽高比计算手机外框尺寸：外框 = 画面显示区域 + 四周内边距，
        /// 这样画面正好填满机身内部，不会在机身里再出现黑边。
        /// </summary>
        private void UpdatePhoneFrame()
        {
            if (_bitmap == null || _screenHost == null) return;
            if (_screenHost.ActualWidth <= 0 || _screenHost.ActualHeight <= 0) return;

            double bezel = _phoneFrame.Padding.Left + _phoneFrame.Padding.Right;
            double bezelV = _phoneFrame.Padding.Top + _phoneFrame.Padding.Bottom;
            double availableWidth = Math.Max(1, _screenHost.ActualWidth - bezel);
            double availableHeight = Math.Max(1, _screenHost.ActualHeight - bezelV);

            double scale = Math.Min(availableWidth / _bitmap.PixelWidth, availableHeight / _bitmap.PixelHeight);
            if (scale <= 0) return;

            _phoneFrame.Width = _bitmap.PixelWidth * scale + bezel;
            _phoneFrame.Height = _bitmap.PixelHeight * scale + bezelV;

            AutoFitToPhone();
        }

        /// <summary>
        /// 首帧/旋转后按画面比例自动调整窗口尺寸，让机身正好铺满窗口，
        /// 避免机身左右出现黑边（之后用户可自由缩放）。
        /// </summary>
        private void AutoFitToPhone()
        {
            if (_autoFitted || _bitmap == null || !IsLoaded) return;

            // 最大化/全屏时画面已按可用区域铺满，交回窗口管理，不要改尺寸和位置
            if (WindowState != WindowState.Normal)
            {
                _autoFitted = true;
                LogToFile("窗口已最大化，跳过自适应尺寸");
                return;
            }

            _autoFitted = true;

            // 状态栏会随文本折行到两行，取值加下限，避免按单行算出偏大的窗口
            double statusHeight = Math.Max(_status.ActualHeight, 40);
            double navHeight = _navBar != null && _navBar.ActualHeight > 0
                ? _navBar.ActualHeight + _navBar.Margin.Top + _navBar.Margin.Bottom
                : 46;
            navHeight = Math.Max(navHeight, 46);
            double bezel = _phoneFrame.Padding.Left + _phoneFrame.Padding.Right;
            double bezelV = _phoneFrame.Padding.Top + _phoneFrame.Padding.Bottom;
            const double outerMargin = 16;

            // 横屏窗口会被撑到接近整屏宽度，显得过大：宽度收到六成半左右；
            // 竖屏接近手机比例，仍按高度尽量铺满。
            bool landscape = _bitmap.PixelWidth > _bitmap.PixelHeight;
            double maxWindowHeight = Math.Min(SystemParameters.WorkArea.Height * (landscape ? 0.74 : 0.92), 1000);
            double maxWindowWidth = SystemParameters.WorkArea.Width * (landscape ? 0.65 : 0.95);
            double availableHeight = Math.Max(200, maxWindowHeight - statusHeight - navHeight - bezelV - outerMargin);
            double availableWidth = Math.Max(160, maxWindowWidth - bezel - outerMargin);

            // 同时受宽高限制，保证窗口不超出屏幕且画面不被裁剪
            double scale = Math.Min(availableHeight / _bitmap.PixelHeight, availableWidth / _bitmap.PixelWidth);
            double imageWidth = _bitmap.PixelWidth * scale;
            double imageHeight = _bitmap.PixelHeight * scale;

            Width = imageWidth + bezel + outerMargin;
            Height = imageHeight + bezelV + statusHeight + navHeight + outerMargin;
            LogToFile(string.Format("窗口已按画面比例自适应: {0:F0}x{1:F0}（{2}）",
                Width, Height, landscape ? "横屏" : "竖屏"));

            // 首帧与每次横竖屏切换后都把窗口重新居中，避免缩放后跑到屏幕角落
            System.Windows.Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);

            // 窗口尺寸变化后可用区域也变了，重新计算机身尺寸；
            // 延迟到布局完成再算，避免机身沿用旧尺寸而比可用区域高出十几像素被裁切。
            Dispatcher.BeginInvoke(new Action(UpdatePhoneFrame), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 按设备物理宽高比计算采集尺寸：长边 1280，短边按比例取偶数。
        /// 与设备比例一致的尺寸不会让 screenrecord 在画面两侧补黑边。
        /// </summary>
        private string ComputeCaptureSize(bool landscape, string fallback = null)
        {
            if (_deviceWidth <= 0 || _deviceHeight <= 0)
            {
                return string.IsNullOrEmpty(fallback) ? (landscape ? "1280x720" : "720x1280") : fallback;
            }

            // wm size 返回的是当前旋转下的物理分辨率，横屏时宽高已互换，
            // 因此按大小边取比例，避免算出一个远超长边限制的尺寸（会被转置成超大画面）
            const int longSide = 1280;
            int maxSide = Math.Max(_deviceWidth, _deviceHeight);
            int minSide = Math.Min(_deviceWidth, _deviceHeight);
            int shortSide = (int)Math.Round(longSide * (double)minSide / maxSide) & ~1;
            if (shortSide < 240) shortSide = 240;

            return landscape ? longSide + "x" + shortSide : shortSide + "x" + longSide;
        }

        /// <summary>
        /// 启动时查询一次设备方向，并记录为基准，后续 CheckRotation 只在实际变化时重建取流。
        /// 查询失败时按竖屏处理。
        /// </summary>
        private bool QueryStartupLandscape()
        {
            int orientation;
            int width;
            int height;
            if (!QueryDisplayGeometry(out orientation, out width, out height))
            {
                return false;
            }

            _rotation = orientation;
            _logicalWidth = width;
            _logicalHeight = height;
            return width > height;
        }

        /// <summary>
        /// 查询当前显示方向与逻辑分辨率（dumpsys input 中的 Viewport INTERNAL），
        /// 例如：orientation=1, logicalFrame=[0, 0, 2848, 1312]。失败返回 false。
        /// </summary>
        private bool QueryDisplayGeometry(out int orientation, out int width, out int height)
        {
            orientation = -1;
            width = 0;
            height = 0;
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell dumpsys input")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);

                    const string viewportKey = "Viewport INTERNAL:";
                    int index = text.IndexOf(viewportKey, StringComparison.Ordinal);
                    if (index < 0) return false;

                    int lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, index);
                    string line = lineEnd > 0 ? text.Substring(index, lineEnd - index) : text.Substring(index);

                    var orientationMatch = System.Text.RegularExpressions.Regex.Match(line, @"orientation=(\d+)");
                    var frameMatch = System.Text.RegularExpressions.Regex.Match(line, @"logicalFrame=\[0,\s*0,\s*(\d+),\s*(\d+)\]");
                    if (!orientationMatch.Success || !frameMatch.Success) return false;

                    orientation = int.Parse(orientationMatch.Groups[1].Value);
                    width = int.Parse(frameMatch.Groups[1].Value);
                    height = int.Parse(frameMatch.Groups[2].Value);
                    return width > 0 && height > 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>旋转/分辨率变化时按新比例重建取流，并重新自适应窗口。</summary>
        private void CheckRotation()
        {
            int orientation;
            int logicalWidth;
            int logicalHeight;
            if (!QueryDisplayGeometry(out orientation, out logicalWidth, out logicalHeight)) return;

            if (_rotation < 0)
            {
                _rotation = orientation;
                _logicalWidth = logicalWidth;
                _logicalHeight = logicalHeight;
                return;
            }

            if (orientation == _rotation && logicalWidth == _logicalWidth && logicalHeight == _logicalHeight)
            {
                return;
            }

            _rotation = orientation;
            _logicalWidth = logicalWidth;
            _logicalHeight = logicalHeight;

            bool landscape = logicalWidth > logicalHeight;
            int longSide = 1280;
            int shortSide = (int)Math.Round(longSide * (double)Math.Min(logicalWidth, logicalHeight) / Math.Max(logicalWidth, logicalHeight)) & ~1;
            if (shortSide < 240) shortSide = 240;
            string newSize = landscape ? longSide + "x" + shortSide : shortSide + "x" + longSide;

            if (_stream == null || string.Equals(_stream.Size, newSize, StringComparison.OrdinalIgnoreCase)) return;

            LogToFile("屏幕方向变化（orientation=" + orientation + "，" + logicalWidth + "x" + logicalHeight
                + "），采集尺寸切换为 " + newSize);
            _autoFitted = false;
            _stream.Size = newSize;
            _stream.Restart();
        }

        /// <summary>导航按键外观：深灰圆角，鼠标悬停变亮，按下变蓝（与主界面按钮色调一致）。</summary>
        private const string NavButtonTemplateXaml = @"
<ControlTemplate TargetType='Button'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Border x:Name='bd' Background='#FF35353D' CornerRadius='5' SnapsToDevicePixels='True'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#FF4C4C58'/>
    </Trigger>
    <Trigger Property='IsPressed' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#FF2A9FFF'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";

        /// <summary>创建虚拟导航按键（图标 + 注入 keyevent）。</summary>
        private Button CreateNavButton(string tip, int keyCode, FrameworkElement icon)
        {
            // 图标按原始路径坐标系绘制，用 Viewbox 统一缩放到小按钮内，避免被 26px 高度裁切
            var iconHost = new Viewbox
            {
                Child = icon,
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };

            var button = new Button
            {
                Content = iconHost,
                Width = 38,
                Height = 26,
                Padding = new Thickness(0),
                // 左右各留 3，按键间距 6，整体居中时左右留白对称
                Margin = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = tip + "（keyevent " + keyCode + "）",
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(NavButtonTemplateXaml)
            };
            button.Click += (s, e) =>
            {
                if (_injector == null) return;
                _injector.KeyEvent(keyCode);
                _lastInject = "keyevent " + keyCode;
            };
            return button;
        }

        /// <summary>返回：Android 风格的三角形图标。</summary>
        private static FrameworkElement CreateBackIcon()
        {
            var stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            return new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M15,2 L3,12 L15,22 Z"),
                Stroke = stroke,
                StrokeThickness = 2,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Width = 18,
                Height = 24,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
        }

        /// <summary>Home：空心圆。</summary>
        private static FrameworkElement CreateHomeIcon()
        {
            var stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            return new System.Windows.Shapes.Ellipse
            {
                Width = 18,
                Height = 18,
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = System.Windows.Media.Brushes.Transparent
            };
        }

        /// <summary>任务：空心圆角方块。</summary>
        private static FrameworkElement CreateRecentsIcon()
        {
            var stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            return new System.Windows.Shapes.Rectangle
            {
                Width = 18,
                Height = 18,
                RadiusX = 2,
                RadiusY = 2,
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = System.Windows.Media.Brushes.Transparent
            };
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

            // 视频流是缩放后的尺寸（如 720x1280），而 input 注入必须使用设备物理像素（如 1312x2848）
            if (_deviceWidth <= 0 || _deviceHeight <= 0)
            {
                deviceX = (int)px;
                deviceY = (int)py;
                return true;
            }

            bool frameLandscape = bitmapWidth > bitmapHeight;
            bool deviceLandscape = _deviceWidth > _deviceHeight;
            double targetWidth = frameLandscape == deviceLandscape ? _deviceWidth : _deviceHeight;
            double targetHeight = frameLandscape == deviceLandscape ? _deviceHeight : _deviceWidth;

            deviceX = Clamp((int)Math.Round(px * targetWidth / bitmapWidth), 0, (int)targetWidth - 1);
            deviceY = Clamp((int)Math.Round(py * targetHeight / bitmapHeight), 0, (int)targetHeight - 1);
            return true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            try { if (_stream != null) _stream.Stop(); } catch { }
            try { if (_stream != null) _stream.ReleaseDevice(); } catch { }
            try { if (_pumpThread != null) _pumpThread.Join(1000); } catch { }
            try { if (_injector != null) _injector.Dispose(); } catch { }
            try { if (_decoder != null) _decoder.Dispose(); } catch { }
            try { MfInterop.Shutdown(); } catch { }
            RestoreDeviceState();
        }
    }
}
