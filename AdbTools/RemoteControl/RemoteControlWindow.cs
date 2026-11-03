using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        /// <summary>分辨率档位：名称、长边像素、图标几何、悬停提示。</summary>
        private sealed class ResolutionPreset
        {
            public string Name { get; set; }
            public int LongSide { get; set; }
            public System.Windows.Media.Geometry Icon { get; set; }
            public string Tip { get; set; }

            /// <summary>让 UI 自动化/屏幕阅读器读到档位名，而不是类型名。</summary>
            public override string ToString()
            {
                return Name;
            }
        }

        /// <summary>分辨率档位，第 0 档为原始分辨率（不缩放），其余按长边像素缩放。</summary>
        private static readonly ResolutionPreset[] ResolutionPresets =
        {
            new ResolutionPreset
            {
                Name = "原始分辨率",
                LongSide = 0,
                Icon = CreateQualityIcon(4),
                Tip = "不缩放，直接按设备物理分辨率采集\n"
                    + "清晰度：最高，文字与细线无缩放锯齿\n"
                    + "速度：最慢，编解码与画面转换约占 3 个 CPU 核心，带宽约 20 Mbps\n"
                    + "建议：USB 连接，或需要看清细节、截图取材时使用"
            },
            new ResolutionPreset
            {
                Name = "1080P 高清",
                LongSide = 1920,
                Icon = CreateQualityIcon(3),
                Tip = "长边缩到 1920（约 1080P，短边按设备比例）\n"
                    + "清晰度：很高，正常视距下与原分辨率差别很小\n"
                    + "速度：开销约为原始分辨率的一半，主流电脑可稳定 30~60 fps\n"
                    + "建议：USB 或 5G Wi-Fi，兼顾清晰度与流畅度"
            },
            new ResolutionPreset
            {
                Name = "720P 标准",
                LongSide = 1280,
                Icon = CreateQualityIcon(2),
                Tip = "长边缩到 1280（约 720P，短边按设备比例）\n"
                    + "清晰度：够用，界面与文字清晰可读\n"
                    + "速度：快，开销约为原始分辨率的 1/4，低配电脑也能跑满帧\n"
                    + "建议：2.4G Wi-Fi、笔记本省电模式，或设备性能较弱时优先选这档"
            },
            new ResolutionPreset
            {
                Name = "480P 流畅",
                LongSide = 854,
                Icon = CreateQualityIcon(1),
                Tip = "长边缩到 854（约 480P，短边按设备比例）\n"
                    + "清晰度：偏糊，小字可能看不清\n"
                    + "速度：最快，延迟最低，最省 CPU 与带宽\n"
                    + "建议：网络差、只需要点击操作和看大致画面时使用"
            }
        };

        /// <summary>
        /// 清晰度图标：屏幕外框 + 内部横向线条，线条条数随清晰度档位递减（4 → 1）。
        /// 返回冻结的几何，可被下拉项与选中框里的多个 Path 安全共用。
        /// </summary>
        private static System.Windows.Media.Geometry CreateQualityIcon(int bars)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("M1.5,1.6 L14.5,1.6 L14.5,12.4 L1.5,12.4 Z");   // 屏幕外框
            sb.Append(" M5.6,14.4 L10.4,14.4");                        // 底座

            const double top = 3.6;
            const double span = 7.0;
            for (int i = 0; i < bars; i++)
            {
                double y = bars > 1 ? top + span * i / (bars - 1) : top + span / 2;
                sb.Append(" M3.6,").Append(y.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(" L12.4,").Append(y.ToString("0.##", CultureInfo.InvariantCulture));
            }

            var geometry = System.Windows.Media.Geometry.Parse(sb.ToString());
            if (geometry.CanFreeze) geometry.Freeze();
            return geometry;
        }

        private readonly string _adbPath;
        private readonly string _serial;

        private readonly Image _image = new Image();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBox _textSink = new TextBox();

        /// <summary>仿手机外框：包裹画面并按窗口大小等比缩放。</summary>
        private readonly Border _phoneFrame = new Border();

        /// <summary>画面区域中央的状态卡片：启动过程、锁屏/息屏、异常说明共用一套样式。</summary>
        private readonly Border _startupPanel = new Border();
        private readonly TextBlock _startupTitle = new TextBlock();
        private readonly TextBlock _startupSteps = new TextBlock();
        private readonly List<string> _startupLines = new List<string>();

        /// <summary>首帧已上屏，不再显示/更新启动过程。</summary>
        private bool _startupHidden;

        /// <summary>画面宿主区域（占满可用空间），用于计算外框尺寸。</summary>
        private Grid _screenHost;

        /// <summary>底部虚拟按键栏（自适应窗口尺寸时用于计算高度）。</summary>
        private StackPanel _navBar;

        /// <summary>分辨率下拉菜单。</summary>
        private ComboBox _resolutionBox;

        /// <summary>当前选择的长边像素，0 表示原始分辨率。</summary>
        private int _presetLongSide;

        /// <summary>同一分辨率下连续停滞的次数，达到阈值才降档。</summary>
        private int _stallCount;

        /// <summary>同一档位连续取流失败的次数，达到 2 次才认定该档不可用。</summary>
        private int _failureCount;

        /// <summary>是否已记录过一次方向查询结果（用于确认轮询链路正常）。</summary>
        private bool _orientationProbeLogged;

        /// <summary>方向查询是否正在执行（0 空闲 / 1 进行中），防止 adb 进程堆积。</summary>
        private int _orientationQuerying;

        /// <summary>方向快速检测窗口的截止时间（此期间 300ms 一次，平时 1 秒一次）。</summary>
        private DateTime _orientationBurstUntil = DateTime.MinValue;

        /// <summary>上一次方向查询时间。</summary>
        private DateTime _lastOrientationCheck = DateTime.MinValue;

        /// <summary>上一次写状态日志的时间。</summary>
        private DateTime _lastStatusLog = DateTime.MinValue;

        /// <summary>待确认的新方向（-1 表示无），用于过滤旋转过程中的方向抖动。</summary>
        private int _pendingRotation = -1;
        private int _pendingWidth;
        private int _pendingHeight;
        private DateTime _pendingSince = DateTime.MinValue;

        /// <summary>设备是否处于锁屏状态（用于状态栏提示）。</summary>
        private bool _locked;

        /// <summary>设备是否熄屏（含 Doze）。</summary>
        private bool _screenOff;

        /// <summary>取流是否因锁屏/息屏而暂停。</summary>
        private bool _paused;

        /// <summary>横竖屏与设备状态轮询计时器，窗口关闭时需要停掉。</summary>
        private DispatcherTimer _rotationTimer;
        private DispatcherTimer _stateTimer;

        /// <summary>是否已按首帧画面比例自动调整过窗口尺寸。</summary>
        private bool _autoFitted;

        private IVideoStream _stream;
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

        /// <summary>真正写进位图、画面变化的帧数（眼睛看到的帧率）。</summary>
        private int _presentedFrames;
        private int _lastPresentedFrames;

        /// <summary>上一次帧率采样的时刻，用真实经过时间计算，避免计时器被推迟时算出虚高的帧率。</summary>
        private DateTime _lastFpsSample = DateTime.UtcNow;

        /// <summary>上屏帧率与解码帧率（平滑后）。</summary>
        private double _displayFps = -1;
        private double _decodeFps = -1;

        /// <summary>
        /// 上屏延迟（毫秒，指数平滑）：从解码输出一帧到真正写进位图的耗时，
        /// 包含 UI 线程排队与像素拷贝。-1 表示还没测到。
        /// </summary>
        private double _latencyMs = -1;

        /// <summary>最近一帧解码完成的时刻，供上屏延迟计算使用。</summary>
        private long _decodedAtTicks;

        /// <summary>最近一次注入的命令，显示在状态栏便于确认点击是否落点正确。</summary>
        private string _lastInject = "";

        /// <summary>是否已有待渲染任务在 UI 队列中（用于合并/丢弃过期帧）。</summary>
        private int _renderQueued;

        /// <summary>是否已收到首帧；收到后状态栏才显示实时指标。</summary>
        private bool _connected;

        /// <summary>窗口创建到首帧显示的耗时（毫秒）。</summary>
        private double _firstFrameMs = -1;

        /// <summary>本次取流开始时间；暂停/恢复后重新计时，使"首帧"指标反映真实等待。</summary>
        private DateTime _createdTime = DateTime.UtcNow;

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
            var ctorWatch = System.Diagnostics.Stopwatch.StartNew();
            _adbPath = adbPath;
            _serial = serial;
            LogToFile("=== 打开远程控制: adb=" + adbPath + " serial=" + serial + " size=" + size + " ===");
            // 分辨率查询放到后台线程（见 StartCaptureAsync），这里不做阻塞的 adb 调用

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

            // 与运行时格式一致，避免首帧前闪一下别的文案
            _status.Text = "- → -   0 fps";
            _status.Foreground = System.Windows.Media.Brushes.White;
            _status.Background = System.Windows.Media.Brushes.Black;
            _status.Padding = new Thickness(0);
            _status.FontSize = 12;
            // 单行显示，过长时省略，保证顶部栏高度稳定、与下拉框对齐
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(8, 0, 8, 0);

            // 顶部：左侧状态文字，右侧分辨率菜单，整条栏固定高度并统一底色
            var header = new Grid
            {
                Height = 30,
                Background = System.Windows.Media.Brushes.Black
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_status);

            _resolutionBox = new ComboBox
            {
                Width = 132,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.White,
                Cursor = Cursors.Hand,
                // 去掉点击后的虚线焦点框，避免看起来像错位的系统控件
                FocusVisualStyle = null,
                ToolTip = "采集分辨率：原始分辨率不缩放，其他档位按长边缩放\n鼠标悬停到各项可查看清晰度与速度的取舍说明",
                // 自绘深色模板，避免系统默认下拉框在暗色窗口里显得突兀
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(ResolutionComboTemplateXaml),
                ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(ResolutionItemTemplateXaml),
                ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(ResolutionComboItemStyleXaml)
            };
            foreach (var preset in ResolutionPresets)
            {
                // 直接放数据对象，由 ItemTemplate 渲染（放 UIElement 会被"选中框"和"下拉项"争抢同一个实例）
                _resolutionBox.Items.Add(preset);
            }
            _resolutionBox.SelectedIndex = 0;
            _resolutionBox.SelectionChanged += Resolution_SelectionChanged;
            Grid.SetColumn(_resolutionBox, 1);
            header.Children.Add(_resolutionBox);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

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

            // 画面中央卡片：启动过程、锁屏/息屏、异常说明都在这里，
            // 只显示简化的阶段文案，不把 scrcpy/adb 的详细日志刷到画面上
            _startupTitle.Text = "正在连接设备";
            _startupTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            _startupTitle.FontSize = 16;
            _startupTitle.FontWeight = FontWeights.SemiBold;
            _startupTitle.TextAlignment = TextAlignment.Center;

            _startupSteps.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
            _startupSteps.FontSize = 12;
            _startupSteps.TextAlignment = TextAlignment.Center;
            _startupSteps.TextWrapping = TextWrapping.Wrap;
            _startupSteps.Margin = new Thickness(0, 10, 0, 0);

            var startupStack = new StackPanel();
            startupStack.Children.Add(_startupTitle);
            startupStack.Children.Add(_startupSteps);

            _startupPanel.Child = startupStack;
            _startupPanel.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xEE, 0x1A, 0x1A, 0x22));
            _startupPanel.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
            _startupPanel.BorderThickness = new Thickness(1);
            _startupPanel.CornerRadius = new CornerRadius(10);
            _startupPanel.Padding = new Thickness(26, 20, 26, 20);
            _startupPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _startupPanel.VerticalAlignment = VerticalAlignment.Center;
            _startupPanel.MaxWidth = 360;

            _screenHost = new Grid();
            _screenHost.Children.Add(_phoneFrame);
            _screenHost.Children.Add(_startupPanel);
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
            // 分隔线：左侧导航键，右侧音量与电源
            navBar.Children.Add(new Border
            {
                Width = 1,
                Height = 15,
                Margin = new Thickness(5, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4A, 0x4A, 0x55))
            });
            navBar.Children.Add(CreateNavButton("音量+ 长按连发", 24, CreateVolumeIcon(true), repeatOnHold: true));
            navBar.Children.Add(CreateNavButton("音量- 长按连发", 25, CreateVolumeIcon(false), repeatOnHold: true));
            navBar.Children.Add(CreateNavButton("电源 短按亮灭屏 / 长按 3 秒", 26,
                CreatePowerIcon(), longPressOnHold: true));
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

            // 查询分辨率、方向、设备状态、启动取流都属于阻塞操作（adb 调用 + 编码器准备），
            // 全部放到后台线程，避免构造窗口时把界面卡住
            ThreadPool.QueueUserWorkItem(_ => StartCaptureAsync(size, bitRate));

            // 方向检测采用"事件触发 + 低频兜底"：
            // 平时 1 秒查一次（省资源）；画面比例变化、断流重连、用户操作后进入 2 秒的 300ms 快速检测窗口。
            _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _rotationTimer.Tick += (s, e) =>
            {
                DateTime now = DateTime.UtcNow;
                bool burst = now < _orientationBurstUntil;
                if (!burst && (now - _lastOrientationCheck).TotalMilliseconds < 1000) return;

                // 上一次查询还没结束就跳过本次，避免 adb 进程堆积
                if (Interlocked.CompareExchange(ref _orientationQuerying, 1, 0) != 0) return;
                _lastOrientationCheck = now;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { CheckRotation(); }
                    catch { }
                    finally { Interlocked.Exchange(ref _orientationQuerying, 0); }
                });
            };
            _rotationTimer.Start();

            // 锁屏/熄屏状态轮询：锁屏时画面往往静止，状态栏给出明确提示
            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _stateTimer.Tick += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                try { CheckDeviceState(); } catch { }
            });
            _stateTimer.Start();

            LogToFile("窗口创建完成，耗时 " + ctorWatch.ElapsedMilliseconds + " ms（取流准备在后台进行）");
        }

        /// <summary>
        /// 更新首帧前画面中央的启动过程（可从后台线程调用）。
        /// 只保留简化文案，同一条步骤不重复追加，最多显示 4 行。
        /// </summary>
        private void SetStartupStep(string title, string step = null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_startupHidden) return;
                if (!string.IsNullOrEmpty(title)) _startupTitle.Text = title;
                if (!string.IsNullOrEmpty(step) && !_startupLines.Contains(step))
                {
                    _startupLines.Add(step);
                    if (_startupLines.Count > 4) _startupLines.RemoveAt(0);
                    _startupSteps.Text = string.Join("\n", _startupLines.ToArray());
                }
            }));
        }

        /// <summary>把取流日志压成少量阶段文案，避免把详细调试输出刷到界面上。</summary>
        private void UpdateStartupFromLog(string msg)
        {
            if (_connected || string.IsNullOrEmpty(msg)) return;
            if (msg.StartsWith("取流片段", StringComparison.Ordinal))
                SetStartupStep("正在等待首帧", "视频通道已建立");
            else if (msg.StartsWith("已接收", StringComparison.Ordinal))
                SetStartupStep(null, "正在接收画面数据");
            else if (msg.StartsWith("档位不兼容", StringComparison.Ordinal))
                SetStartupStep("正在匹配分辨率", "当前档位不兼容，自动切换");
            else if (msg.StartsWith("设备不支持 scrcpy server", StringComparison.Ordinal))
                SetStartupStep("正在切换取流方式", "已回退兼容模式");
            else if (msg.StartsWith("取流停滞", StringComparison.Ordinal)
                || msg.StartsWith("取流失败", StringComparison.Ordinal)
                || msg.StartsWith("scrcpy 连接失败", StringComparison.Ordinal))
                SetStartupStep("正在重新连接", "取流中断，正在重试");
            else if (msg.StartsWith("scrcpy: [server]", StringComparison.Ordinal))
                SetStartupStep(null, "设备端编码服务启动中");
        }

        /// <summary>首帧真正上屏后收起启动过程卡片（在 UI 线程调用）。</summary>
        private void HideStartupHint()
        {
            if (_startupHidden) return;
            _startupHidden = true;
            _startupPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 统一决定画面中央卡片的内容：息屏/暂停提示优先，其次是首帧前的启动过程。
        /// 状态栏只保留尺寸与帧率，其它说明一律落在中央卡片里。
        /// </summary>
        private void UpdateNotice()
        {
            if (_paused)
            {
                _startupTitle.Text = "设备已息屏";
                _startupSteps.Text = "亮屏后自动恢复画面\n也可点下方「电源」键唤醒";
                _startupPanel.Visibility = Visibility.Visible;
                // 画面已清空，隐藏空的机身外框，让提示落在深色背景上
                _phoneFrame.Visibility = Visibility.Collapsed;
                return;
            }

            _phoneFrame.Visibility = Visibility.Visible;
            // 首帧前由 SetStartupStep 维护启动过程；首帧上屏后收起
            _startupPanel.Visibility = _startupHidden ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 后台完成取流前的准备工作并启动：分辨率/方向/锁屏查询 + 唤醒设备 + 建立解码通道。
        /// 这些都会阻塞（多次 adb 调用），放在构造里会明显卡顿。
        /// </summary>
        private void StartCaptureAsync(string size, int bitRate)
        {
            try
            {
                SetStartupStep("正在连接设备", "查询分辨率与屏幕方向");

                if (_deviceWidth <= 0 || _deviceHeight <= 0)
                {
                    QueryPhysicalSize();
                }

                // 启动时设备可能已经是横屏，按实际方向取尺寸，避免先用竖屏尺寸再纠正
                bool landscapeAtStart = QueryStartupLandscape();
                SetStartupStep("正在准备取流", "唤醒设备");
                string captureSize = ComputeCaptureSize(landscapeAtStart, size);
                // 原生分辨率像素多，码率按像素数估算；调用方给的值作为下限
                int effectiveBitRate = Math.Max(bitRate, SuggestBitRate(captureSize));
                LogToFile("采集尺寸 " + captureSize + "，码率 " + (effectiveBitRate / 10000 / 100.0) + " Mbps");

                // 打开窗口时设备可能已锁屏：先判断一次，锁屏时直接挂起，避免一进来就重连
                CheckDeviceState();

                StartPipeline(captureSize, effectiveBitRate);

                IVideoStream stream = _stream;
                if (_paused && stream != null)
                {
                    // 开始时就是锁屏状态：新流直接挂起，等解锁再启动
                    stream.Paused = true;
                    stream.SuppressWatchdog = true;
                }
            }
            catch (Exception ex)
            {
                LogToFile("启动取流失败：" + ex.Message);
                SetStartupStep("启动取流失败", "请检查设备连接后重试");
            }
        }

        private void StartPipeline(string size, int bitRate)
        {
            // 注意：Media Foundation 在取流线程里初始化（见下方 _pumpThread），
            // 避免 MFStartup 阻塞调用线程
            // 优先使用设备端 MediaCodec 主动编码（scrcpy server）；设备不支持时
            // ScrcpyVideoStream 会在连续两次连不上后自动回退到 screenrecord。
            _stream = new ScrcpyVideoStream(_adbPath, _serial, size, bitRate);
            SetStartupStep("正在启动设备端编码服务", "建立视频通道");
            _stream.Log += msg =>
            {
                _lastLog = msg;
                LogToFile(msg);
                if (!string.IsNullOrEmpty(msg) && msg.StartsWith("取流停滞", StringComparison.Ordinal))
                {
                    // 连续多次取不到数据时尝试降一档分辨率（部分机型编码器不支持当前尺寸）
                    Dispatcher.BeginInvoke(new Action(HandleStreamStall));
                }
                if (!string.IsNullOrEmpty(msg) && msg.StartsWith("取流失败", StringComparison.Ordinal))
                {
                    // 整个片段一个字节都没有：当前尺寸基本不可用，直接降一档
                    Dispatcher.BeginInvoke(new Action(HandleStreamFailure));
                }
                if (!string.IsNullOrEmpty(msg) && msg.StartsWith("档位不兼容", StringComparison.Ordinal))
                {
                    // scrcpy server 报编码器不支持后由自身 downsize 兜底，不会走"取流失败"；
                    // 但降低 max_size 能让它原生编码、画面反而更清晰，这里同样依次向下找兼容档位
                    Dispatcher.BeginInvoke(new Action(() => DowngradeResolution("设备编码器不支持当前档位")));
                }
                if (!string.IsNullOrEmpty(msg) && msg.StartsWith("取流片段", StringComparison.Ordinal))
                {
                    // 片段重建常由旋转触发：顺便确认一次方向
                    RequestOrientationCheck();
                }
                // 画面中央与顶栏都只显示压缩后的阶段文案，不再堆完整日志
                UpdateStartupFromLog(msg);
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
                    SetStartupStep("解码出错", "正在重试…");
                }
            };

            // 先唤醒设备再取流：息屏状态下 screenrecord 可能完全不产帧，
            // 会白白等看门狗超时，反而拖慢首帧。
            // 唤醒与解锁必须在取流前完成：与取流并发执行 adb 命令会拖慢取流启动。
            // 已合并为单次 adb 调用，耗时约 0.3~0.5 秒。
            try { _stream.WakeUpDevice(); } catch { }

            _pumpThread = new Thread(() =>
            {
                // 解码用的 Media Foundation 在本线程初始化，解码回调也在这里触发
                try { MfInterop.Startup(); } catch { }
                _stream.Pump((data, count) => _splitter.Feed(data, count));
            })
            {
                IsBackground = true,
                Name = "adb-stream"
            };
            _pumpThread.Start();
            SetStartupStep("正在等待首帧", "等待设备端首帧");

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
            // 锁屏/息屏暂停期间不解码上屏：锁屏界面通常是竖屏，会把横屏窗口的画面拉变形成竖图，
            // 保持最后一帧即可，恢复后再接收新画面。
            if (_paused) return;

            Interlocked.Increment(ref _decodedFrames);
            // 有画面进来就说明当前分辨率可用，清空失败/停滞计数，停止继续降档
            _stallCount = 0;
            _failureCount = 0;

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
                Interlocked.Exchange(ref _decodedAtTicks, DateTime.UtcNow.Ticks);
            }

            // 只在没有待渲染任务时排一次渲染：渲染跟不上时自动丢中间帧，
            // 保证画面始终是"最新一帧"，避免队列堆积导致延迟越来越大。
            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
            }
        }

        /// <summary>清空当前显示的画面（锁屏/息屏暂停时使用，避免旧画面盖住提示文字）。</summary>
        private void ClearDisplayedFrame()
        {
            lock (_frameLock)
            {
                _front = null;
                _back = null;
                _framePending = false;
                _frameWidth = 0;
                _frameHeight = 0;
            }

            _bitmap = null;
            _image.Source = null;
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
                    // scrcpy 的 server 会自己跟随设备旋转改变输出尺寸，不经过取流重建；
                    // 宽高比变化时必须立刻重新自适应窗口，不能等方向轮询去重置标记
                    // （轮询重置往往晚于这一帧，之后尺寸不再变化就再也没机会自适应）。
                    bool aspectChanged = _bitmap == null
                        || Math.Abs((double)_bitmap.PixelWidth / _bitmap.PixelHeight - (double)width / height) > 0.02;
                    if (aspectChanged) _autoFitted = false;

                    _bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    _image.Source = _bitmap;
                    UpdatePhoneFrame();
                    // 画面宽高比变化通常意味着设备旋转，立刻加密方向检测
                    RequestOrientationCheck();
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
                // 首帧真正显示出来，收起居中的启动过程提示
                HideStartupHint();

                Interlocked.Increment(ref _presentedFrames);

                // 上屏延迟：解码完成 → 真正写进位图，反映 UI 排队与像素拷贝的耗时
                long decodedAt = Interlocked.Read(ref _decodedAtTicks);
                if (decodedAt > 0)
                {
                    double ms = (DateTime.UtcNow.Ticks - decodedAt) / 10000.0;
                    // 忽略刚重连等异常值，避免一次尖刺把显示带跑偏
                    if (ms >= 0 && ms < 5000)
                    {
                        _latencyMs = _latencyMs < 0 ? ms : _latencyMs * 0.8 + ms * 0.2;
                    }
                }
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
            // 按真实经过时间计算帧率：DispatcherTimer 在 UI 繁忙时会被推迟，
            // 若仍按固定的 500ms 换算，界面越卡、算出来的帧率反而越高。
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastFpsSample).TotalSeconds;
            _lastFpsSample = now;

            int decoded = _decodedFrames;
            int presented = _presentedFrames;
            if (elapsed >= 0.05)
            {
                double rawDecode = (decoded - _lastDecodedFrames) / elapsed;
                double rawDisplay = (presented - _lastPresentedFrames) / elapsed;
                _decodeFps = _decodeFps < 0 ? rawDecode : _decodeFps * 0.6 + rawDecode * 0.4;
                _displayFps = _displayFps < 0 ? rawDisplay : _displayFps * 0.6 + rawDisplay * 0.4;
            }
            _lastDecodedFrames = decoded;
            _lastPresentedFrames = presented;

            // 锁屏/熄屏时在状态栏最前面提示，方便判断"画面没变"是不是设备休眠了
            // 中央卡片负责息屏/启动过程等说明；状态栏只保留三项指标
            UpdateNotice();

            string size = _bitmap == null ? "-" : _bitmap.PixelWidth + "x" + _bitmap.PixelHeight;
            string device = _deviceWidth > 0 ? _deviceWidth + "x" + _deviceHeight : "-";

            // 左上角固定为：画面分辨率 → 设备物理分辨率、真实上屏帧率；
            // 延迟、解码帧率等放悬停提示，其它说明一律在中央卡片里
            _status.Text = string.Format("{0} → {1}   {2:F0} fps", size, device, Math.Max(0, _displayFps));

            // 详细指标放悬停提示：解码帧率高于上屏帧率说明画面被丢了，是卡顿的主要原因
            _status.ToolTip = string.Format(
                "上屏 {0:F0} fps：真正写到画面上的帧数，即你眼睛看到的流畅度\n" +
                "解码 {1:F0} fps：解码器输出的帧数，明显高于上屏说明电脑跟不上、中间帧被丢弃\n" +
                "上屏延迟 {2}：解码完成到画面更新，只含电脑端排队与像素拷贝；\n" +
                "    另有设备端采集编码约 30~80 ms 与传输耗时，不计入此值\n" +
                "帧率偏低或延迟偏大时，建议把分辨率调低一档",
                Math.Max(0, _displayFps), Math.Max(0, _decodeFps),
                _latencyMs >= 0 ? string.Format("{0:F0} ms", _latencyMs) : "-");

            // 每 10 秒把当前状态写进日志，便于事后排查"画面没恢复"这类问题
            if ((DateTime.UtcNow - _lastStatusLog).TotalSeconds >= 10)
            {
                _lastStatusLog = DateTime.UtcNow;
                LogToFile("状态：" + (_status.Text ?? "-") + "，采集=" + (_stream != null ? _stream.Size : "-")
                    + "，锁屏=" + _locked + "，暂停=" + _paused);
            }
        }

        /// <summary>把运行日志写到临时目录，便于排查"等待首帧"卡住的问题。</summary>
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
            // 用户刚旋转完设备往往会马上点一下，此时加密方向检测
            RequestOrientationCheck();
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
                                // 拿到真实分辨率后，把设备根本达不到的高档位从菜单移除
                                TrimUnreachablePresets();
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 采集尺寸只会缩小、不会放大；档位长边超过设备原生分辨率长边时，
        /// 该档位永远达不到（选了也等同于「原始分辨率」），启动时直接从菜单移除。
        /// 例如 720P 屏就不该列出「1080P 高清」。
        /// 用 Invoke 同步执行，确保裁剪先于后续任何降档操作。
        /// </summary>
        private void TrimUnreachablePresets()
        {
            int width = _deviceWidth;
            int height = _deviceHeight;
            if (width <= 0 || height <= 0) return;
            int nativeLongSide = Math.Max(width, height);

            try
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    if (_resolutionBox == null) return;
                    for (int i = _resolutionBox.Items.Count - 1; i >= 0; i--)
                    {
                        ResolutionPreset preset = _resolutionBox.Items[i] as ResolutionPreset;
                        // 第 0 档「原始分辨率」LongSide=0，始终保留
                        if (preset == null || preset.LongSide == 0) continue;
                        if (preset.LongSide > nativeLongSide)
                        {
                            _resolutionBox.Items.RemoveAt(i);
                            LogToFile("隐藏设备达不到的档位：" + preset.Name
                                + "（设备原始长边 " + nativeLongSide + "）");
                        }
                    }
                }));
            }
            catch { }
        }

        /// <summary>关闭窗口后取消"保持常亮"，恢复设备原有行为。</summary>
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
            // 暂停期间（锁屏/息屏）不调整窗口尺寸，等恢复后再按新画面适配
            if (_paused)
            {
                _autoFitted = false;
                return;
            }

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

        /// <summary>按当前分辨率档位计算采集尺寸。</summary>
        private string ComputeCaptureSize(bool landscape, string fallback = null)
        {
            return ComputeCaptureSizeFor(_presetLongSide, landscape, fallback);
        }

        /// <summary>
        /// 计算采集尺寸：targetLongSide 为 0 时用原始分辨率，否则按目标长边等比缩放
        /// （不会超过原生分辨率）；长短边都对齐到 16 像素，避免编码器补边产生绿条。
        /// </summary>
        private string ComputeCaptureSizeFor(int targetLongSide, bool landscape, string fallback = null)
        {
            if (_deviceWidth <= 0 || _deviceHeight <= 0)
            {
                return string.IsNullOrEmpty(fallback) ? (landscape ? "1280x720" : "720x1280") : fallback;
            }

            // wm size 返回的是当前旋转下的物理分辨率，横屏时宽高已互换，按大小边计算
            int maxSide = Math.Max(_deviceWidth, _deviceHeight);
            int minSide = Math.Min(_deviceWidth, _deviceHeight);

            // 4K 及以上屏幕像素过多（编解码与转换开销翻倍），等比压到约 420 万像素以内；
            // 常见 1080p+ 机型仍保持原生分辨率
            const double maxPixels = 4200000;
            double pixels = (double)maxSide * minSide;
            if (pixels > maxPixels)
            {
                double scale = Math.Sqrt(maxPixels / pixels);
                maxSide = (int)(maxSide * scale) & ~15;
                minSide = (int)(minSide * scale) & ~15;
            }

            int longSide = Math.Max(640, AlignTo16(maxSide));
            if (targetLongSide > 0 && targetLongSide < longSide)
            {
                longSide = Math.Max(240, AlignTo16(targetLongSide));
            }

            int shortSide = Math.Max(240, AlignTo16((int)Math.Round((double)longSide * minSide / maxSide)));

            return landscape ? longSide + "x" + shortSide : shortSide + "x" + longSide;
        }

        /// <summary>就近对齐到 16 的倍数（编码器对齐要求，且不会引入补边）。</summary>
        private static int AlignTo16(int value)
        {
            return ((value + 8) / 16) * 16;
        }

        /// <summary>分辨率菜单切换：按新档位重建取流。</summary>
        private void Resolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ResolutionPreset item = _resolutionBox != null ? _resolutionBox.SelectedItem as ResolutionPreset : null;
            if (item == null) return;

            int longSide = item.LongSide;
            if (longSide == _presetLongSide) return;

            _presetLongSide = longSide;
            _stallCount = 0;
            _failureCount = 0;
            ApplyResolutionChange();
        }

        /// <summary>用当前档位重建取流，尺寸与码率一起更新。</summary>
        private void ApplyResolutionChange()
        {
            IVideoStream stream = _stream;
            if (stream == null) return;

            bool landscape = _bitmap != null
                ? _bitmap.PixelWidth > _bitmap.PixelHeight
                : _logicalWidth > _logicalHeight;

            string newSize = ComputeCaptureSize(landscape);
            int index = IndexOfPreset(_presetLongSide);
            string name = index >= 0 ? ResolutionPresets[index].Name : "自定义";

            if (string.Equals(stream.Size, newSize, StringComparison.OrdinalIgnoreCase))
            {
                // 档位变了但采集尺寸相同（设备长边本就是 1920 时，
                // 「原始分辨率」与「1080P 高清」都得到同样的 max_size）。
                // 仍需重建取流让这一档真正跑一遍：不兼容时 server 会再次报错，
                // 才能依次向下找兼容；否则降级会卡在这一档，看起来像被越级跳过。
                LogToFile("分辨率档位切换为 " + name + "（采集尺寸不变），重建取流以验证该档位");
                stream.Restart();
                return;
            }

            LogToFile("分辨率切换为 " + newSize + "（" + name + "）");
            stream.BitRate = SuggestBitRate(newSize);
            stream.Size = newSize;
            stream.Restart();
        }

        /// <summary>
        /// 取流连续停滞：当前档位可能不被设备编码器支持，自动降到下一档；已是最低档则不再降。
        /// </summary>
        private void HandleStreamStall()
        {
            if (_stream == null) return;
            if (++_stallCount < 2) return;

            _stallCount = 0;
            DowngradeResolution("当前分辨率取不到画面");
        }

        /// <summary>
        /// 整段取流失败（一个字节都没有）：连续两次都失败才判定当前档位不可用，
        /// 避免一次偶发断连就把本来能用的分辨率降掉。
        /// </summary>
        private void HandleStreamFailure()
        {
            if (_stream == null) return;
            _stallCount = 0;

            if (++_failureCount < 2)
            {
                LogToFile("取流失败（第 " + _failureCount + " 次），先以当前分辨率重试");
                return;
            }

            _failureCount = 0;
            DowngradeResolution("连续两次取流失败");
        }

        /// <summary>
        /// 把分辨率降一档，并把确认不可用的档位从下拉菜单移除；
        /// 这样会一直往下找，直到某个档位能正常出画面为止。
        /// </summary>
        private void DowngradeResolution(string reason)
        {
            // 基于菜单里实际存在的档位找下一档：启动裁剪可能已移除设备达不到的高档位，
            // 不能再按静态数组 index+1 去找，否则找不到目标会卡住降级。
            if (_resolutionBox == null) return;

            int current = _resolutionBox.SelectedIndex;
            if (current < 0) return;
            if (current + 1 >= _resolutionBox.Items.Count)
            {
                var lastPreset = _resolutionBox.Items[_resolutionBox.Items.Count - 1] as ResolutionPreset;
                LogToFile(reason + "，但已是最低档：" + (lastPreset != null ? lastPreset.Name : "-"));
                return;
            }

            var currentPreset = _resolutionBox.Items[current] as ResolutionPreset;
            var nextPreset = _resolutionBox.Items[current + 1] as ResolutionPreset;
            if (nextPreset == null) return;

            LogToFile(reason + "，分辨率降级为 " + nextPreset.Name);
            // 一次只降一档，切过去重建取流真正验证；仍不兼容会再次收到信号继续往下降
            _resolutionBox.SelectedItem = nextPreset;
            // 切走后再移除旧档位，避免 ComboBox 自动改选造成一次无谓重启
            if (currentPreset != null) _resolutionBox.Items.Remove(currentPreset);
        }

        private static int IndexOfPreset(int longSide)
        {
            for (int i = 0; i < ResolutionPresets.Length; i++)
            {
                if (ResolutionPresets[i].LongSide == longSide) return i;
            }
            return -1;
        }

        /// <summary>按分辨率估算码率：约 0.2 bit/像素/帧，限制在 8~32 Mbps。</summary>
        private static int SuggestBitRate(string size)
        {
            int w, h;
            string[] parts = (size ?? string.Empty).Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)) return 8000000;

            double bps = 0.2 * w * (double)h * 30;
            if (bps < 8000000) bps = 8000000;
            if (bps > 32000000) bps = 32000000;
            return (int)bps;
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
                // 在设备端就把整份 dumpsys 过滤成一行，避免 350ms 一次轮询时传输上百 KB 文本
                var psi = new ProcessStartInfo(_adbPath,
                    "-s " + _serial + " shell \"dumpsys input | grep -m1 'Viewport INTERNAL'\"")
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

        /// <summary>
        /// 触发一次快速方向检测：画面宽高比变化、断流重连、用户操作之后往往伴随旋转，
        /// 触发后 2 秒内按 300ms 检测，其余时间保持 1 秒一次的兜底查询。
        /// </summary>
        private void RequestOrientationCheck()
        {
            _orientationBurstUntil = DateTime.UtcNow.AddSeconds(2);
        }

        /// <summary>查询当前方向与逻辑分辨率，变化时重建取流并重新自适应窗口。</summary>
        private void CheckRotation()
        {
            int orientation;
            int logicalWidth;
            int logicalHeight;
            if (!QueryDisplayGeometry(out orientation, out logicalWidth, out logicalHeight))
            {
                if (!_orientationProbeLogged)
                {
                    _orientationProbeLogged = true;
                    LogToFile("方向查询失败（未取到 Viewport INTERNAL）");
                }
                return;
            }

            if (!_orientationProbeLogged)
            {
                _orientationProbeLogged = true;
                LogToFile("方向查询正常，orientation=" + orientation + "，" + logicalWidth + "x" + logicalHeight);
            }

            ApplyOrientation(orientation, logicalWidth, logicalHeight);
        }

        /// <summary>旋转/分辨率变化时按新比例重建取流，并重新自适应窗口。</summary>
        private void ApplyOrientation(int orientation, int logicalWidth, int logicalHeight)
        {
            if (logicalWidth <= 0 || logicalHeight <= 0) return;

            if (_rotation < 0)
            {
                _rotation = orientation;
                _logicalWidth = logicalWidth;
                _logicalHeight = logicalHeight;
                LogToFile("初始方向：orientation=" + orientation + "，" + logicalWidth + "x" + logicalHeight);
                return;
            }

            if (orientation == _rotation && logicalWidth == _logicalWidth && logicalHeight == _logicalHeight)
            {
                _pendingRotation = -1;
                return;
            }

            // 旋转过程中方向会连续抖动（0→1→3…），先记录下来，等下一次查询仍是同一值再切换，
            // 否则会连续重启取流，导致画面长时间回不来
            if (_pendingRotation != orientation || _pendingWidth != logicalWidth || _pendingHeight != logicalHeight)
            {
                _pendingRotation = orientation;
                _pendingWidth = logicalWidth;
                _pendingHeight = logicalHeight;
                _pendingSince = DateTime.UtcNow;
                RequestOrientationCheck();
                return;
            }

            if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < 300) return;

            _pendingRotation = -1;
            _rotation = orientation;
            _logicalWidth = logicalWidth;
            _logicalHeight = logicalHeight;

            // 锁屏/息屏期间只记录方向：此时是锁屏界面的方向，不代表解锁后的方向，
            // 也不能去重建取流，否则会把画面切错比例
            if (_paused)
            {
                LogToFile("暂停期间方向变化，已记录：" + orientation + " " + logicalWidth + "x" + logicalHeight);
                return;
            }

            bool landscape = logicalWidth > logicalHeight;
            string newSize = ComputeCaptureSize(landscape);

            if (_stream == null || string.Equals(_stream.Size, newSize, StringComparison.OrdinalIgnoreCase)) return;

            LogToFile("屏幕方向变化（orientation=" + orientation + "，" + logicalWidth + "x" + logicalHeight
                + "），采集尺寸切换为 " + newSize);
            _autoFitted = false;
            _stream.Size = newSize;

            if (_stream is ScrcpyVideoStream)
            {
                // scrcpy server 自己跟随设备旋转改变输出尺寸，且 max_size 按长边计算、
                // 横竖屏结果相同；重建只会造成几秒黑屏，还可能触发无谓的档位验证/降档。
                LogToFile("scrcpy 取流由设备端自行跟随旋转，无需重建");
            }
            else
            {
                _stream.Restart();
            }

            // 主动补一次自适应：scrcpy 重启后可能仍输出同尺寸画面，
            // 若不主动触发，_autoFitted 重置后就没有下一次尺寸变化来调用 AutoFitToPhone。
            Dispatcher.BeginInvoke(new Action(UpdatePhoneFrame));
        }

        /// <summary>
        /// 恢复取流前按当前屏幕方向重算采集尺寸：锁屏界面方向常与解锁后不同，
        /// 直接沿用会把横屏画面按竖屏比例采集，导致显示被挤变形。
        /// </summary>
        private void ReapplyCaptureSizeForCurrentOrientation()
        {
            int orientation;
            int width;
            int height;
            if (!QueryDisplayGeometry(out orientation, out width, out height)) return;

            _rotation = orientation;
            _logicalWidth = width;
            _logicalHeight = height;
            // 恢复后按新画面重新适配窗口
            _autoFitted = false;

            IVideoStream stream = _stream;
            if (stream == null) return;

            string newSize = ComputeCaptureSize(width > height);
            if (string.Equals(stream.Size, newSize, StringComparison.OrdinalIgnoreCase)) return;

            LogToFile("恢复取流：采集尺寸切换为 " + newSize);
            stream.BitRate = SuggestBitRate(newSize);
            stream.Size = newSize;
        }

        /// <summary>
        /// 查询锁屏/熄屏状态：一次 adb 调用同时取 keyguard 与 wakefulness，减少往返开销。
        /// 兼容不同版本：isKeyguardShowing（较新）、mDreamingLockscreen（旧版）。
        /// </summary>
        private void CheckDeviceState()
        {
            // 注意不能对 window 部分用 -m1：两条关键字段谁先出现不固定，
            // 取第一条可能刚好取到 =false 的那条，导致锁屏状态判断错误（横屏时尤其明显）
            string script = "dumpsys window | grep -E 'isKeyguardShowing=|mDreamingLockscreen=' | head -n 4; "
                + "dumpsys power | grep 'mWakefulness=' | head -n 1";
            string output = RunAdbShell(script);
            if (output == null) return;

            bool locked = output.IndexOf("isKeyguardShowing=true", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("mDreamingLockscreen=true", StringComparison.OrdinalIgnoreCase) >= 0;
            bool screenOff = output.IndexOf("mWakefulness=Asleep", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("mWakefulness=Dozing", StringComparison.OrdinalIgnoreCase) >= 0;

            // 只有真正熄屏（Asleep/Dozing）才暂停取流：此时 screenrecord 会直接退出，
            // 不暂停就会陷入"取流结束 → 准备重连"的无限循环；
            // 锁屏但屏幕仍亮时必须继续取流，否则远程看不到锁屏界面、也就无法滑动解锁。
            bool paused = screenOff;
            IVideoStream stream = _stream;
            if (stream != null)
            {
                // 锁屏静态界面长时间不产帧，同样不该被看门狗判为断流
                stream.SuppressWatchdog = paused || locked;
                if (stream.Paused != paused)
                {
                    if (paused)
                    {
                        // 先置暂停再结束片段，避免被打断时提示"准备重连"
                        stream.Paused = true;
                        stream.Restart();
                        // 清空画面：否则留下的最后一帧会让"已息屏"提示完全看不清
                        Dispatcher.BeginInvoke(new Action(ClearDisplayedFrame));
                    }
                    else
                    {
                        // 恢复前先按解锁后的真实方向校正采集尺寸（锁屏界面多为竖屏，
                        // 直接沿用会把横屏画面挤成竖图），再放行取流
                        ReapplyCaptureSizeForCurrentOrientation();
                        _connected = false;
                        _firstFrameMs = -1;
                        _createdTime = DateTime.UtcNow;
                        stream.Paused = false;
                    }
                }
            }
            _paused = paused;

            if (locked == _locked && screenOff == _screenOff) return;

            _locked = locked;
            _screenOff = screenOff;
            LogToFile("设备状态：锁屏=" + locked + "，熄屏=" + screenOff);
            Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        /// <summary>执行一段 shell 脚本并返回标准输出（失败返回 null）。</summary>
        private string RunAdbShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell \"" + script + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return text;
                }
            }
            catch { return null; }
        }

        /// <summary>分辨率下拉框的深色模板：扁平圆角、自绘箭头，与远程控制窗口配色一致。</summary>
        private const string ResolutionComboTemplateXaml = @"
<ControlTemplate TargetType='ComboBox'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid>
    <ToggleButton x:Name='Toggle' Focusable='False' ClickMode='Press'
        Foreground='#FFF2F2F2' FontSize='12'
        Content='{TemplateBinding SelectionBoxItem}'
        ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
        IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='bd' Background='#FF23232A' BorderBrush='#FF3F3F46' BorderThickness='1' CornerRadius='4'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <ContentPresenter Grid.Column='0' Margin='8,0,4,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>
              <Path Grid.Column='1' Margin='0,0,8,0' VerticalAlignment='Center' Data='M0,0 L8,0 L4,5 Z' Fill='#FFB9B9C2'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='#FF5C5C68'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <Popup x:Name='PART_Popup' Placement='Bottom' VerticalOffset='2'
           IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
      <Border Background='#FF23232A' BorderBrush='#FF3F3F46' BorderThickness='1' CornerRadius='4' Padding='2' MinWidth='132'>
        <ItemsPresenter/>
      </Border>
    </Popup>
  </Grid>
</ControlTemplate>";

        /// <summary>下拉项内容：清晰度图标 + 档位名称（图标几何由多个 Path 共用）。</summary>
        private const string ResolutionItemTemplateXaml = @"
<DataTemplate
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <StackPanel Orientation='Horizontal' Margin='0,1'>
    <Path Data='{Binding Icon}' Width='14' Height='14' Margin='1,0,7,0' Stretch='Uniform'
          Stroke='#FFF2F2F2' StrokeThickness='1.5' Fill='Transparent'
          StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'
          VerticalAlignment='Center'/>
    <TextBlock Text='{Binding Name}' Foreground='#FFF2F2F2' FontSize='12' VerticalAlignment='Center'/>
  </StackPanel>
</DataTemplate>";

        /// <summary>下拉项样式：深色底、白字，悬停浅灰、选中蓝色，悬停显示该档位的说明。</summary>
        private const string ResolutionComboItemStyleXaml = @"
<Style TargetType='ComboBoxItem'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Foreground' Value='#FFF2F2F2'/>
  <!-- 鼠标移到项上变小手，与窗口里其他可点控件保持一致 -->
  <Setter Property='Cursor' Value='Hand'/>
  <!-- 提示文字较长，延长显示时间；ToolTip 内容由档位数据的 Tip 提供 -->
  <Setter Property='ToolTip' Value='{Binding Tip}'/>
  <Setter Property='ToolTipService.ShowDuration' Value='20000'/>
  <Setter Property='ToolTipService.InitialShowDelay' Value='300'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ComboBoxItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='3' Padding='8,4' Margin='0,1'>
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='#FF33333C'/>
          </Trigger>
          <Trigger Property='IsSelected' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='#FF2A9FFF'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

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

        /// <summary>导航按键模板只解析一次后复用，避免每个按键都解析一遍 XAML。</summary>
        private static ControlTemplate _navButtonTemplate;

        private static ControlTemplate NavButtonTemplate
        {
            get
            {
                if (_navButtonTemplate == null)
                {
                    _navButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(NavButtonTemplateXaml);
                }
                return _navButtonTemplate;
            }
        }

        /// <summary>
        /// 创建虚拟导航按键（图标 + 注入 keyevent）。
        /// repeatOnHold：按住时连发（音量键，等效真实长按快速调节）；
        /// longPressOnHold：按住超过 0.5 秒用 --longpress 注入（电源键）。
        /// </summary>
        private Button CreateNavButton(string tip, int keyCode, FrameworkElement icon,
            bool repeatOnHold = false, bool longPressOnHold = false)
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
                Template = NavButtonTemplate
            };
            if (repeatOnHold || longPressOnHold)
            {
                DispatcherTimer holdTimer = null;
                int heldTicks = 0;
                DateTime pressStarted = DateTime.MinValue;
                DateTime lastReleaseAt = DateTime.MinValue;

                button.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    pressStarted = DateTime.UtcNow;
                    if (!repeatOnHold) return;

                    InjectNavKey(keyCode);
                    heldTicks = 0;
                    // 250ms 一次：单条 `input keyevent` 在设备上约 100~150ms，
                    // 间隔太密会在 shell 里排队，松手后还在继续调音量
                    holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    holdTimer.Tick += (s2, e2) =>
                    {
                        // 前 2 个 tick（约 500ms）不发，避免单击时重复触发
                        if (++heldTicks <= 2) return;
                        // 最多连发 12 次（约 3 秒），防止一直按住时音量一直跑到头
                        if (heldTicks > 14) return;
                        InjectNavKey(keyCode);
                    };
                    holdTimer.Start();
                };

                button.PreviewMouseLeftButtonUp += (s, e) =>
                {
                    StopHoldTimer(ref holdTimer);
                    lastReleaseAt = DateTime.UtcNow;

                    if (longPressOnHold)
                    {
                        HandlePowerRelease((lastReleaseAt - pressStarted).TotalMilliseconds);
                    }
                };

                // 鼠标移出按钮时也停止连发，避免拖走后还在响
                button.MouseLeave += (s, e) => StopHoldTimer(ref holdTimer);

                // 键盘/自动化触发（没有鼠标按下事件）时补一次单击；
                // 鼠标点击后紧跟的 Click 要跳过，否则长按松手会多注入一次
                button.Click += (s, e) =>
                {
                    if (lastReleaseAt != DateTime.MinValue
                        && (DateTime.UtcNow - lastReleaseAt).TotalMilliseconds < 500)
                    {
                        return;
                    }
                    InjectNavKey(keyCode);
                };
            }
            else
            {
                button.Click += (s, e) => InjectNavKey(keyCode);
            }
            return button;
        }

        private static void StopHoldTimer(ref DispatcherTimer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer = null;
        }

        /// <summary>
        /// 电源键松开：按实际按住时长分两档，阈值适当放宽，避免手抖误触发。
        /// <list type="bullet">
        /// <item>小于 0.5 秒：短按，亮屏/灭屏（keyevent 26）</item>
        /// <item>0.5 秒以上：长按，用 --longpress 注入（keyevent --longpress 26）</item>
        /// </list>
        /// </summary>
        private void HandlePowerRelease(double heldMs)
        {
            const int keyPower = 26;
            if (_injector == null)
            {
                LogToFile("按键注入不可用（输入通道未建立）：keyevent 26");
                return;
            }

            if (heldMs < 500)
            {
                LogToFile("电源短按 " + heldMs.ToString("F0") + "ms");
                InjectNavKey(keyPower);
                return;
            }

            // 按住超过 0.5 秒即按长按处理，走系统 --longpress
            LogToFile("电源长按 " + heldMs.ToString("F0") + "ms");
            InjectNavKey(keyPower, true);
        }

        /// <summary>注入按键；longPress 为 true 时走 --longpress（触发长按行为）。</summary>
        private void InjectNavKey(int keyCode, bool longPress = false)
        {
            if (_injector == null)
            {
                LogToFile("按键注入不可用（输入通道未建立）：keyevent " + keyCode);
                return;
            }

            LogToFile("注入 keyevent " + keyCode + (longPress ? "（长按）" : ""));
            if (longPress) _injector.KeyEventLongPress(keyCode);
            else _injector.KeyEvent(keyCode);
            _lastInject = "keyevent " + keyCode;
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

        /// <summary>音量：喇叭轮廓 + 加号/减号。</summary>
        private static FrameworkElement CreateVolumeIcon(bool up)
        {
            var stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            string data = "M3,6.3 L5.6,6.3 L9,3.2 L9,12.8 L5.6,9.7 L3,9.7 Z M11.8,8 L15.8,8";
            if (up) data += " M13.8,6 L13.8,10";

            return new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(data),
                Stroke = stroke,
                StrokeThickness = 1.5,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Width = 16,
                Height = 16,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
        }

        /// <summary>电源：常见电源符号（缺口圆 + 顶部竖线）。</summary>
        private static FrameworkElement CreatePowerIcon()
        {
            var stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            return new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M8,2 L8,8.5 M4.2,4.4 A6,6 0 1 0 11.8,4.4"),
                Stroke = stroke,
                StrokeThickness = 1.8,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Width = 16,
                Height = 16,
                Stretch = System.Windows.Media.Stretch.Uniform
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
            try { if (_rotationTimer != null) _rotationTimer.Stop(); } catch { }
            try { if (_stateTimer != null) _stateTimer.Stop(); } catch { }
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
