using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 远程控制窗口（M1/M2 验证）：实时显示设备画面，鼠标点击/拖动与键盘输入直接注入设备。
    /// </summary>
    internal sealed partial class RemoteControlWindow : Window
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

        /// <summary>分辨率档位，第 0 档为原画（不缩放），其余按长边像素缩放。</summary>
        private static readonly ResolutionPreset[] ResolutionPresets =
        {
            new ResolutionPreset
            {
                Name = "原画",
                LongSide = 0,
                Icon = CreateQualityIcon(4),
                Tip = "不缩放，直接按设备物理分辨率采集\n"
                    + "清晰度：最高，文字与细线无缩放锯齿\n"
                    + "速度：最慢，编解码与画面转换约占 3 个 CPU 核心，带宽约 20 Mbps\n"
                    + "建议：USB 连接，或需要看清细节、截图取材时使用"
            },
            new ResolutionPreset
            {
                Name = "1080P 超清",
                LongSide = 1920,
                Icon = CreateQualityIcon(3),
                Tip = "长边缩到 1920（约 1080P，短边按设备比例）\n"
                    + "清晰度：很高，正常视距下与原画差别很小\n"
                    + "速度：开销约为原画的一半，主流电脑可稳定 30~60 fps\n"
                    + "建议：USB 或 5G Wi-Fi，兼顾清晰度与流畅度"
            },
            new ResolutionPreset
            {
                Name = "720P 高清",
                LongSide = 1280,
                Icon = CreateQualityIcon(2),
                Tip = "长边缩到 1280（约 720P，短边按设备比例）\n"
                    + "清晰度：够用，界面与文字清晰可读\n"
                    + "速度：快，开销约为原画的 1/4，低配电脑也能跑满帧\n"
                    + "建议：2.4G Wi-Fi、笔记本省电模式，或设备性能较弱时优先选这档"
            },
            new ResolutionPreset
            {
                Name = "480P 标清",
                LongSide = 854,
                Icon = CreateQualityIcon(1),
                Tip = "长边缩到 854（约 480P，短边按设备比例）\n"
                    + "清晰度：偏糊，小字可能看不清\n"
                    + "速度：很快，延迟低，省 CPU 与带宽\n"
                    + "建议：网络较差、只需要点击操作和看大致画面时使用"
            },
            new ResolutionPreset
            {
                Name = "360P 流畅",
                LongSide = 640,
                Icon = CreateQualityIcon(0),
                Tip = "长边缩到 640（约 360P，短边按设备比例）\n"
                    + "清晰度：很低，只能看清大块内容与图标\n"
                    + "速度：最快，CPU 与带宽占用最低\n"
                    + "建议：网络很差、设备性能较弱，或只操作界面时使用"
            }
        };

        /// <summary>
        /// 清晰度图标：屏幕外框 + 内部横向线条，线条条数随清晰度档位递减（4 → 0）。
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

        // 界面元素（_image / _status / _textSink / _phoneFrame / _startupPanel /
        // _startupTitle / _startupSteps）均由 RemoteControlWindow.xaml 的 x:Name 生成，
        // 这里只保留启动过程文案的逻辑状态。
        private readonly List<string> _startupLines = new List<string>();

        /// <summary>首帧已上屏，不再显示/更新启动过程。</summary>
        private bool _startupHidden;

        /// <summary>已进入关闭流程：中央卡片显示提示，暂时阻止窗口真正关闭。</summary>
        private bool _closing;

        /// <summary>后台清理已完成，允许窗口真正关闭。</summary>
        private bool _cleanupDone;

        /// <summary>关闭清理的超时兜底，避免清理卡住导致窗口永远关不掉。</summary>
        private DispatcherTimer _closeGuard;

        // _screenHost / _navBar / _resolutionBox 同样由 XAML 的 x:Name 生成。

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

        /// <summary>操作提示浮层的自动隐藏计时器。</summary>
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        /// <summary>是否已按首帧画面比例自动调整过窗口尺寸。</summary>
        private bool _autoFitted;

        /// <summary>Win32 消息：用户拖动边框/标题栏结束，窗口尺寸与位置最终确定。</summary>
        private const int WmExitSizeMove = 0x0232;

        /// <summary>Win32 矩形，用于实测窗口外框与客户区的差值。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        /// <summary>
        /// 用户在本次窗口会话中手动调整过大小。设为 true 后不再按画面比例自动改窗口尺寸，
        /// 只跟随用户设定；关闭窗口即失效，不写入配置。
        /// </summary>
        private bool _userResized;

        /// <summary>用户调整后的窗口尺寸，横屏、竖屏各记一份，切换方向时各用自己的。</summary>
        private Size? _userSizeLandscape;
        private Size? _userSizePortrait;

        private IVideoStream _stream;
        private H264DecoderMft _decoder;
        private InputInjector _injector;
        private AccessUnitSplitter _splitter;
        private Thread _pumpThread;

        private WriteableBitmap _bitmap;

        /// <summary>
        /// 显示位图缓存（按宽高）。横竖屏各一块，旋转时直接复用：
        /// 每次新建 5MP 位图会额外产生约 60MB 后备内存与纹理，内存曲线出现尖峰。
        /// </summary>
        private readonly Dictionary<string, WriteableBitmap> _bitmapCache =
            new Dictionary<string, WriteableBitmap>();

        /// <summary>
        /// 帧缓冲：解码线程写、UI 线程读。用三块固定缓冲轮换，
        /// 通过 _front / _pendingBuffer / _rendering 三个引用标记占用状态，
        /// 保证 UI 正在 WritePixels 的那块不会被后台线程改写（旧版双缓冲会撕裂）。
        /// </summary>
        private readonly byte[][] _frameBuffers = new byte[3][];
        private byte[] _front;          // 已解码完成、等待呈现
        private byte[] _pendingBuffer;  // 后台线程正在写入
        private byte[] _rendering;      // UI 线程正在读取
        private readonly object _frameLock = new object();
        private int _frameWidth;
        private int _frameHeight;
        private int _frontWidth;
        private int _frontHeight;

        /// <summary>视频帧的可见尺寸（= 服务端 videoSize），控制通道注入触摸时作为坐标系。</summary>
        private int _videoWidth;
        private int _videoHeight;

        /// <summary>本次拖拽是否已走实时触摸通道（按下注入成功后才为 true）。</summary>
        private bool _liveTouch;

        /// <summary>是否已记录过"实时触摸已启用"，避免每次点击都写日志。</summary>
        private bool _liveTouchLogged;

        /// <summary>上一条 MOVE 的发送时刻（Stopwatch 计数），用于限频。</summary>
        private long _lastTouchTick;

        private int _decodedFrames;
        private int _lastDecodedFrames;

        /// <summary>真正写进位图、画面变化的帧数（眼睛看到的帧率）。</summary>
        private int _presentedFrames;
        private int _lastPresentedFrames;

        /// <summary>上屏帧间隔统计：平均帧率高但间隔不均时，体感就是一顿一顿。</summary>
        private long _lastPresentTicks;
        private double _maxPresentIntervalMs;

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

        /// <summary>按键注入日志的节流时间戳（音量键按住会连发，不能每次按键都写日志）。</summary>
        private DateTime _lastInjectLog = DateTime.MinValue;

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

        /// <summary>按键按住连发/长按的计时器与时间戳（按钮由 XAML 声明，按实例记录状态）。</summary>
        private readonly Dictionary<Button, DispatcherTimer> _navHoldTimers = new Dictionary<Button, DispatcherTimer>();
        private readonly Dictionary<Button, DateTime> _navPressStarted = new Dictionary<Button, DateTime>();
        private readonly Dictionary<Button, DateTime> _navLastRelease = new Dictionary<Button, DateTime>();

        private bool _dragging;
        private bool _moved;
        private Point _startPoint;
        private Point _lastPoint;

        /// <summary>拖动中最近一次的视频帧坐标（控制通道使用，天然跟随旋转与缩放）。</summary>
        private Point _lastVideoPoint;

        /// <summary>滚轮累积量与触发位置，用于把高频滚轮事件合并成一次滑动。</summary>
        private double _wheelAccum;
        private Point _wheelPoint;
        private Point _wheelVideoPoint;

        /// <summary>滚轮拖拽手势：按下 → 逐帧移动 → 抬起，抬手后由系统产生惯性。</summary>
        private bool _wheelGestureActive;
        private int _wheelGestureX;
        private int _wheelGestureStartY;
        private double _wheelGesturePos;
        private double _wheelGestureTarget;
        private int _wheelGestureIdleFrames;
        private DateTime _wheelGestureLastTick;
        private int _wheelGestureFrames;
        private double _wheelGestureMaxDt;

        /// <summary>一格滚轮对应的拖拽像素（视频坐标）。</summary>
        private const double WheelPixelsPerNotch = 60.0;

        /// <summary>手势移动速度（像素/秒）；按真实经过时间推进，定时器抖动也不会忽快忽慢。</summary>
        private const double WheelGestureSpeed = 1200.0;

        /// <summary>单次手势最多移动画面高度的比例，避免快速滚轮后长时间追赶。</summary>
        private const double WheelGestureMaxDistanceRatio = 0.45;
        private DateTime _dragStartTime = DateTime.UtcNow;

        /// <summary>设备物理分辨率（输入注入使用物理像素，而视频流是缩放后的尺寸）。</summary>
        private int _deviceWidth;
        private int _deviceHeight;

        /// <summary>最近一次已知的显示方向与逻辑分辨率，-1 表示未知。</summary>
        private int _rotation = -1;
        private int _logicalWidth;
        private int _logicalHeight;

        /// <summary>设备键（硬件序列号）：窗口尺寸按它保存，与应用缓存使用同一套设备身份。</summary>
        private readonly string _deviceKey;

        public RemoteControlWindow(string adbPath, string serial, string size, int bitRate, string deviceKey = null)
        {
            // 先加载 XAML，生成界面元素与 x:Name 字段
            InitializeComponent();

            var ctorWatch = System.Diagnostics.Stopwatch.StartNew();
            _adbPath = adbPath;
            _serial = serial;
            _deviceKey = AppDeviceKey.Normalize(
                string.IsNullOrEmpty(deviceKey) ? serial : deviceKey);
            LogToFile("=== 打开远程控制: adb=" + adbPath + " serial=" + serial + " size=" + size + " ===");

            // 先取回该设备保存的窗口尺寸，首帧适配时直接按用户设定显示
            LoadSavedSize();
            // 分辨率查询放到后台线程（见 StartCaptureAsync），这里不做阻塞的 adb 调用

            Title = "远程控制 - " + serial;
            // 窗口尺寸、深色背景与四行布局（状态栏 / 手机外框 / 虚拟按键栏 / 隐藏输入框）
            // 均已声明在 RemoteControlWindow.xaml 中。

            // 状态栏与分辨率菜单的样式、模板、提示均已在 XAML 中声明，
            // 这里只填充档位数据并挂接切换事件。
            foreach (var preset in ResolutionPresets)
            {
                // 直接放数据对象，由 ItemTemplate 渲染（放 UIElement 会被"选中框"和"下拉项"争抢同一个实例）
                _resolutionBox.Items.Add(preset);
            }
            _resolutionBox.SelectedIndex = 0;
            _resolutionBox.SelectionChanged += Resolution_SelectionChanged;

            // 画面尺寸变化、窗口生命周期、虚拟按键与键盘输入事件均已由 XAML 声明。

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

            // 操作提示浮层：显示几秒后自动隐藏
            _toastTimer.Interval = TimeSpan.FromSeconds(2.6);
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                _toast.Visibility = Visibility.Collapsed;
            };

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
            // 关闭流程中卡片要一直显示到清理完成，不能被首帧收起
            if (_closing || _startupHidden) return;
            _startupHidden = true;
            _startupPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 统一决定画面中央卡片的内容：息屏/暂停提示优先，其次是首帧前的启动过程。
        /// 状态栏只保留尺寸与帧率，其它说明一律落在中央卡片里。
        /// </summary>
        private void UpdateNotice()
        {
            // 关闭流程的卡片由 ShowClosingNotice 维护，500ms 定时器不要覆盖它
            if (_closing)
            {
                SetNoticeVisibility(_startupPanel, Visibility.Visible);
                SetNoticeVisibility(_phoneFrame, Visibility.Visible);
                return;
            }

            if (_paused)
            {
                SetNoticeText(_startupTitle, "设备已息屏");
                SetNoticeText(_startupSteps, "亮屏后自动恢复画面\n也可点下方「电源」键唤醒");
                SetNoticeVisibility(_startupPanel, Visibility.Visible);
                // 画面已清空，隐藏空的机身外框，让提示落在深色背景上
                SetNoticeVisibility(_phoneFrame, Visibility.Collapsed);
                return;
            }

            SetNoticeVisibility(_phoneFrame, Visibility.Visible);
            // 首帧前由 SetStartupStep 维护启动过程；首帧上屏后收起
            SetNoticeVisibility(_startupPanel, _startupHidden ? Visibility.Collapsed : Visibility.Visible);
        }

        /// <summary>值没变就不赋值，避免 500ms 定时器反复触发依赖属性变更与布局。</summary>
        private static void SetNoticeText(TextBlock block, string text)
        {
            if (!string.Equals(block.Text, text, StringComparison.Ordinal)) block.Text = text;
        }

        private static void SetNoticeVisibility(UIElement element, Visibility visibility)
        {
            if (element.Visibility != visibility) element.Visibility = visibility;
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
                // Windows N/KN 版与部分精简系统不带 Media Foundation，
                // 表现为找不到 mfplat.dll 或 H.264 解码器 COM 类未注册。
                // 这种情况和设备连接无关，提示要指向真正的原因。
                bool missingMedia =
                    ex is DllNotFoundException
                    || ex is EntryPointNotFoundException
                    || (ex is System.Runtime.InteropServices.COMException
                        && (uint)ex.HResult == 0x80040154);

                if (missingMedia)
                {
                    LogToFile("启动取流失败（缺少媒体组件）：" + ex.Message);
                    SetStartupStep("系统缺少媒体组件",
                        "未找到 H.264 解码器（Media Foundation）\r\n"
                        + "Windows N 版或精简系统需先安装“媒体功能包”");
                    return;
                }

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
                    // 约每 10 秒记一次进度即可，避免每秒刷一行把日志撑大
                    if (auSeen % 600 == 0)
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

                InputInjector injector = _injector;
                if (injector != null)
                {
                    // 绑定控制通道与坐标系：之后按下/移动可直接注入，不必等抬手补一次 swipe
                    injector.Control = _stream as ScrcpyVideoStream;
                    injector.DeviceWidth = _deviceWidth;
                    injector.DeviceHeight = _deviceHeight;
                    injector.VideoWidth = _videoWidth;
                    injector.VideoHeight = _videoHeight;
                }
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
            byte[] buffer;
            lock (_frameLock)
            {
                // 只在容量不足时重新分配：横竖屏互换时像素总量不变，
                // 按宽高变化重建会在每次旋转瞬间产生 3×20MB 的垃圾，内存曲线出现尖峰。
                if (_frameBuffers[0] == null || _frameBuffers[0].Length < need)
                {
                    for (int i = 0; i < _frameBuffers.Length; i++) _frameBuffers[i] = new byte[need];
                    _front = null;
                }
                _frameWidth = width;
                _frameHeight = height;

                // 只取既不是"待呈现"、也不是"后台正在写/前台正在读"的那块
                buffer = null;
                foreach (byte[] candidate in _frameBuffers)
                {
                    if (candidate != _front && candidate != _pendingBuffer && candidate != _rendering)
                    {
                        buffer = candidate;
                        break;
                    }
                }
                // 三块都被占用，说明渲染严重跟不上：丢这一帧，绝不覆盖正在显示的画面
                if (buffer == null) return;
                _pendingBuffer = buffer;
            }

            bool converted = Nv12Converter.Convert(nv12, length, sourceWidth, sourceHeight,
                cropX, cropY, width, height, buffer);

            lock (_frameLock)
            {
                _pendingBuffer = null;
                if (!converted) return;
                // 旧 _front 还没被取走说明渲染跟不上，直接丢中间帧，始终显示最新一帧
                _front = buffer;
                _frontWidth = width;
                _frontHeight = height;
                Interlocked.Exchange(ref _decodedAtTicks, DateTime.UtcNow.Ticks);
            }

            // 触摸事件必须使用视频帧坐标系（服务端 videoSize，实测等于裁剪后的可见尺寸），
            // 首帧与旋转后都要同步给注入器。传 coded 尺寸（含 16 像素补边）会被服务端直接丢弃。
            // 尺寸没变就不要每帧重复赋值，减少跨线程属性写入。
            if (_videoWidth != width || _videoHeight != height)
            {
                _videoWidth = width;
                _videoHeight = height;
                InputInjector injector = _injector;
                if (injector != null)
                {
                    injector.VideoWidth = width;
                    injector.VideoHeight = height;
                }
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
                _pendingBuffer = null;
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
                frame = _front;
                if (frame == null)
                {
                    _renderQueued = 0;
                    return;
                }
                _front = null;
                // 标记这块缓冲正被 UI 读取，后台线程不得复用
                _rendering = frame;
                width = _frontWidth;
                height = _frontHeight;
            }

            if (width > 0 && height > 0 && frame.Length >= width * height * 4)
            {
                if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    // scrcpy 的 server 会自己跟随设备旋转改变输出尺寸，不经过取流重建；
                    // 宽高比变化时必须立刻重新自适应窗口，不能等方向轮询去重置标记
                    // （轮询重置往往晚于这一帧，之后尺寸不再变化就再也没机会自适应）。
                    bool aspectChanged = _bitmap == null
                        || Math.Abs((double)_bitmap.PixelWidth / _bitmap.PixelHeight - (double)width / height) > 0.02;
                    if (aspectChanged) _autoFitted = false;

                    _bitmap = GetOrCreateBitmap(width, height);
                    _image.Source = _bitmap;
                    UpdatePhoneFrame();
                    // 画面宽高比变化通常意味着设备旋转，立刻加密方向检测
                    RequestOrientationCheck();
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
                // 首帧真正显示出来，收起居中的启动过程提示
                HideStartupHint();

                Interlocked.Increment(ref _presentedFrames);

                // 记录上屏帧间隔峰值：平均帧率看不出卡顿，间隔抖动才能反映出来
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = Interlocked.Exchange(ref _lastPresentTicks, nowTicks);
                if (lastTicks > 0)
                {
                    double intervalMs = (nowTicks - lastTicks) / 10000.0;
                    if (intervalMs > 0 && intervalMs < 2000 && intervalMs > _maxPresentIntervalMs)
                    {
                        _maxPresentIntervalMs = intervalMs;
                    }
                }

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

            bool hasNewFrame;
            lock (_frameLock)
            {
                // 释放正在读取的缓冲，后台线程可以重新使用它
                _rendering = null;
                // 必须先清零再检查 _front：否则解码线程恰好在两者之间提交新帧时，
                // 会因为看到 _renderQueued=1 不再排队，导致这一帧永远不被呈现
                Interlocked.Exchange(ref _renderQueued, 0);
                hasNewFrame = _front != null;
            }

            // 渲染期间又到了新帧：补一次渲染，保持"总是显示最新帧"
            if (hasNewFrame && Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
            }
        }

        /// <summary>
        /// 取该尺寸的显示位图：横竖屏各缓存一块，旋转时直接复用，
        /// 避免每次新建 5MP 位图产生的后备内存与纹理（约 60MB/次）。
        /// </summary>
        private WriteableBitmap GetOrCreateBitmap(int width, int height)
        {
            string key = width + "x" + height;
            WriteableBitmap cached;
            if (_bitmapCache.TryGetValue(key, out cached)) return cached;

            var bitmap = new WriteableBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            _bitmapCache[key] = bitmap;

            // 只需要横竖两套；出现第三种尺寸时丢掉最旧的一块
            while (_bitmapCache.Count > 2)
            {
                string oldest = null;
                foreach (string k in _bitmapCache.Keys)
                {
                    if (k != key) { oldest = k; break; }
                }
                if (oldest == null) break;
                _bitmapCache.Remove(oldest);
            }

            return bitmap;
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

            double maxIntervalMs = _maxPresentIntervalMs;
            _maxPresentIntervalMs = 0;

            // 锁屏/熄屏时在状态栏最前面提示，方便判断"画面没变"是不是设备休眠了
            // 中央卡片负责息屏/启动过程等说明；状态栏只保留三项指标
            UpdateNotice();

            string size = _bitmap == null ? "-" : _bitmap.PixelWidth + "x" + _bitmap.PixelHeight;
            string device = _deviceWidth > 0 ? _deviceWidth + "x" + _deviceHeight : "-";

            // 左上角固定为：画面分辨率 → 设备物理分辨率、真实上屏帧率；
            // 延迟、解码帧率等放悬停提示，其它说明一律在中央卡片里
            string statusText = string.Format("{0} → {1}   {2:F0} fps", size, device, Math.Max(0, _displayFps));
            if (!string.Equals(_status.Text, statusText, StringComparison.Ordinal)) _status.Text = statusText;

            // 详细指标放悬停提示：解码帧率高于上屏帧率说明画面被丢了，是卡顿的主要原因
            string tooltip = string.Format(
                "上屏 {0:F0} fps：真正写到画面上的帧数，即你眼睛看到的流畅度\n" +
                "解码 {1:F0} fps：解码器输出的帧数，明显高于上屏说明电脑跟不上、中间帧被丢弃\n" +
                "最大帧间隔 {3:F0} ms：最近一次采样窗口内最长的两次上屏间隔，越大越容易感觉一顿\n" +
                "上屏延迟 {2}：解码完成到画面更新，只含电脑端排队与像素拷贝；\n" +
                "    另有设备端采集编码约 30~80 ms 与传输耗时，不计入此值\n" +
                "帧率偏低或延迟偏大时，建议把分辨率调低一档",
                Math.Max(0, _displayFps), Math.Max(0, _decodeFps),
                _latencyMs >= 0 ? string.Format("{0:F0} ms", _latencyMs) : "-",
                maxIntervalMs);
            if (!string.Equals(_status.ToolTip as string, tooltip, StringComparison.Ordinal))
            {
                _status.ToolTip = tooltip;
            }

            // 每 10 秒把当前状态写进日志，便于事后排查"画面没恢复"这类问题
            if ((DateTime.UtcNow - _lastStatusLog).TotalSeconds >= 30)
            {
                _lastStatusLog = DateTime.UtcNow;
                LogToFile("状态：" + (_status.Text ?? "-") + "，采集=" + (_stream != null ? _stream.Size : "-")
                    + "，锁屏=" + _locked + "，暂停=" + _paused);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static int _logWorkerStarted;

        /// <summary>
        /// 日志改为入队 + 单个后台线程常开 StreamWriter 写入：
        /// 解码/取流线程不再做同步磁盘 I/O，也不会每条日志开关一次文件。
        /// </summary>
        private static void LogToFile(string message)
        {
            try
            {
                _logQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
                if (Interlocked.CompareExchange(ref _logWorkerStarted, 1, 0) == 0)
                {
                    var worker = new Thread(LogWorker)
                    {
                        IsBackground = true,
                        Name = "remote-log"
                    };
                    worker.Start();
                }
            }
            catch { }
        }

        private static void LogWorker()
        {
            try
            {
                // 日志写到 %APPDATA%\AdbTools\logs，按天分文件，避免占用临时目录
                string path = AdbTools.Globals.GetLogFilePath(
                    "RemoteControl-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                CleanupOldLogs();

                // 日志目录不可写时退回临时目录，保证写日志本身不会变成故障点
                StreamWriter writer;
                try
                {
                    writer = new StreamWriter(path, true, System.Text.Encoding.UTF8);
                }
                catch
                {
                    writer = new StreamWriter(
                        AdbTools.Globals.GetCacheFilePath("RemoteControl.log"),
                        true, System.Text.Encoding.UTF8);
                }

                using (writer)
                {
                    DateTime lastFlush = DateTime.UtcNow;
                    string line;
                    while (true)
                    {
                        // 一次取空队列再写，减少文件写入与 flush 次数
                        bool wrote = false;
                        while (_logQueue.TryDequeue(out line))
                        {
                            writer.WriteLine(line);
                            wrote = true;
                        }

                        DateTime now = DateTime.UtcNow;
                        if (wrote && (now - lastFlush).TotalMilliseconds >= 200)
                        {
                            writer.Flush();
                            lastFlush = now;
                        }

                        Thread.Sleep(20);
                    }
                }
            }
            catch { }
        }

        /// <summary>只保留最近 7 天的日志，避免长期运行后日志目录无限增长。</summary>
        private static void CleanupOldLogs()
        {
            try
            {
                string dir = AdbTools.Globals.LogDirectory;
                DateTime limit = DateTime.Now.AddDays(-7);
                foreach (string file in Directory.GetFiles(dir, "RemoteControl-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                    }
                    catch { }
                }
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
            // 用户开始拖动画面时先结束滚轮手势，避免两条触摸序列叠加
            EndWheelGesture();
            // 用户刚旋转完设备往往会马上点一下，此时加密方向检测
            RequestOrientationCheck();
            int x, y, vx, vy;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y, out vx, out vy)) return;

            _dragging = true;
            _moved = false;
            _startPoint = new Point(x, y);
            _lastPoint = _startPoint;
            _lastVideoPoint = new Point(vx, vy);
            _dragStartTime = DateTime.UtcNow;
            _image.CaptureMouse();

            // 控制通道可用时按下就真实注入 DOWN，拖动过程中逐条发 MOVE，画面能实时跟随；
            // 否则保持原逻辑：只记起点，抬起时用一次 tap / swipe 完成
            // （`input motionevent` 每条都要起一次 Java 进程，无法支撑实时拖动）。
            InputInjector injector = _injector;
            _liveTouch = injector != null && injector.SupportsLiveTouch && injector.TouchDown(vx, vy);
            _lastTouchTick = Stopwatch.GetTimestamp();

            if (_liveTouch && !_liveTouchLogged)
            {
                _liveTouchLogged = true;
                LogToFile(string.Format("实时触摸已启用：视频坐标系 {0}x{1}，设备 {2}x{3}",
                    _videoWidth, _videoHeight, _deviceWidth, _deviceHeight));
            }
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            int x, y, vx, vy;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y, out vx, out vy)) return;

            _lastPoint = new Point(x, y);
            _lastVideoPoint = new Point(vx, vy);
            if (!_moved && (Math.Abs(x - _startPoint.X) > 6 || Math.Abs(y - _startPoint.Y) > 6))
            {
                _moved = true;
            }

            if (_liveTouch)
            {
                // 约 8ms 一条：控制通道是本地 TCP 写入，不会排队；
                // 限频只是避免高回报率鼠标把设备端输入队列打满。
                long now = Stopwatch.GetTimestamp();
                if ((now - _lastTouchTick) * 1000.0 / Stopwatch.Frequency >= 8.0)
                {
                    _lastTouchTick = now;
                    InputInjector injector = _injector;
                    if (injector == null || !injector.TouchMove(vx, vy)) _liveTouch = false;
                }
            }
        }

        private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _image.ReleaseMouseCapture();

            int x, y, vx, vy;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y, out vx, out vy))
            {
                x = (int)_lastPoint.X;
                y = (int)_lastPoint.Y;
                vx = (int)_lastVideoPoint.X;
                vy = (int)_lastVideoPoint.Y;
            }

            if (_injector == null) return;

            // 拖拽期间已实时注入过，抬手只补一条 UP 即可，不能再重复整段 swipe
            if (_liveTouch)
            {
                _liveTouch = false;
                if (_injector.TouchUp(vx, vy))
                {
                    _lastInject = _moved
                        ? string.Format("touch {0},{1} -> {2},{3}", (int)_startPoint.X, (int)_startPoint.Y, x, y)
                        : string.Format("tap {0},{1}", (int)_startPoint.X, (int)_startPoint.Y);
                    return;
                }
            }

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

        /// <summary>
        /// 滚轮：转成一次触摸拖拽手势（按下 → 逐帧移动 → 抬起），抬手后由系统产生惯性。
        /// 与鼠标拖拽共用同一条 scrcpy 控制通道，逐帧写入、不排队，反向滚动可立即改变方向。
        /// </summary>
        private void RemoteScreen_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_injector == null)
            {
                LogToFile("滚轮忽略：输入通道未建立");
                return;
            }

            // 用户正在用鼠标拖动画面时不叠加滚轮手势，避免手指事件冲突
            if (_dragging) return;

            int x, y, vx, vy;
            if (!TryMapToDevice(e.GetPosition(_image), out x, out y, out vx, out vy))
            {
                LogToFile("滚轮忽略：坐标不在画面内 image="
                    + _image.ActualWidth.ToString("F0") + "x" + _image.ActualHeight.ToString("F0"));
                return;
            }

            e.Handled = true;

            _wheelPoint = new Point(x, y);
            _wheelVideoPoint = new Point(vx, vy);
            _wheelAccum += e.Delta;
            StartOrExtendWheelGesture();
        }

        /// <summary>
        /// 启动或延长滚轮拖拽手势：把累积滚轮量换算成手指位移，交给手势状态机逐帧推进。
        /// 手势已在执行时只叠加目标位移，不重新按下，避免事件堆积。
        /// </summary>
        private void StartOrExtendWheelGesture()
        {
            InputInjector injector = _injector;
            if (injector == null) return;

            int vh = _videoHeight;
            if (vh <= 0 || _videoWidth <= 0)
            {
                FallbackWheelSwipe();
                return;
            }

            // 一格滚轮换算成固定像素位移；正数表示手指向下滑（内容向上滚）
            double delta = _wheelAccum;
            _wheelAccum = 0;
            double pixels = delta / 120.0 * WheelPixelsPerNotch;
            if (Math.Abs(pixels) < 1) return;

            if (!_wheelGestureActive)
            {
                int vx = Clamp((int)_wheelVideoPoint.X, 0, Math.Max(0, _videoWidth - 1));
                // 用真实位置按下，不预先挪到中间，否则按下瞬间画面会跳一下
                int vy = Clamp((int)_wheelVideoPoint.Y, 0, Math.Max(0, vh - 1));

                _wheelGestureX = vx;
                _wheelGestureStartY = vy;
                _wheelGesturePos = 0;
                // 目标位移同时受上下边界和单次最大距离限制
                double maxDistance = vh * WheelGestureMaxDistanceRatio;
                _wheelGestureTarget = Clamp(pixels,
                    Math.Max(-vy, -maxDistance),
                    Math.Min(Math.Max(0, vh - 1 - vy), maxDistance));
                _wheelGestureIdleFrames = 0;
                _wheelGestureLastTick = DateTime.UtcNow;
                _wheelGestureFrames = 0;
                _wheelGestureMaxDt = 0;

                if (!injector.TouchDown(vx, vy))
                {
                    _wheelGestureActive = false;
                    _wheelAccum = delta;   // 还原，交给回退逻辑
                    FallbackWheelSwipe();
                    return;
                }

                _wheelGestureActive = true;
                // 用渲染帧驱动：触摸移动与画面刷新对齐，避免定时器错帧造成顿挫
                System.Windows.Media.CompositionTarget.Rendering -= WheelGestureOnRendering;
                System.Windows.Media.CompositionTarget.Rendering += WheelGestureOnRendering;
                return;
            }

            // 手势进行中：叠加目标位移，同时限制单次手势的总距离
            double limit = vh * WheelGestureMaxDistanceRatio;
            double desired = Clamp(_wheelGestureTarget + pixels, -limit, limit);
            _wheelGestureTarget = Clamp(desired,
                -_wheelGestureStartY, Math.Max(0, vh - 1 - _wheelGestureStartY));
            _wheelGestureIdleFrames = 0;
        }

        /// <summary>渲染帧回调：每个画面帧推进一步，保证触摸移动与画面刷新同步。</summary>
        private void WheelGestureOnRendering(object sender, EventArgs e)
        {
            StepWheelGesture();
        }

        /// <summary>手势每帧推进一步；目标位移走完后抬手，让系统产生惯性。</summary>
        private void StepWheelGesture()
        {
            InputInjector injector = _injector;
            if (injector == null || !_wheelGestureActive)
            {
                EndWheelGesture();
                return;
            }

            double remaining = _wheelGestureTarget - _wheelGesturePos;
            if (Math.Abs(remaining) < 1)
            {
                // 快速滚动时事件间隔可能略长，保持手势一小段时间再抬手，
                // 避免反复“按下/抬起”打断上一次惯性，造成画面抖动
                if (++_wheelGestureIdleFrames >= 5) EndWheelGesture();
                return;
            }

            _wheelGestureIdleFrames = 0;

            // 按真实经过时间推进：定时器延迟时步长自动变长，整体速度保持恒定
            DateTime now = DateTime.UtcNow;
            double rawDt = (now - _wheelGestureLastTick).TotalSeconds;
            _wheelGestureLastTick = now;
            _wheelGestureFrames++;
            if (rawDt > _wheelGestureMaxDt) _wheelGestureMaxDt = rawDt;

            double dt = rawDt;
            if (dt <= 0 || dt > 0.1) dt = 0.016;

            double step = WheelGestureSpeed * dt;
            if (step > Math.Abs(remaining)) step = Math.Abs(remaining);
            _wheelGesturePos += remaining > 0 ? step : -step;

            int y = (int)Math.Round(_wheelGestureStartY + _wheelGesturePos);
            int clamped = Clamp(y, 0, Math.Max(0, _videoHeight - 1));
            if (clamped != y)
            {
                // 到边界后无法继续拖拽，直接抬手，让惯性自然接管
                _wheelGesturePos = clamped - _wheelGestureStartY;
                EndWheelGesture();
                return;
            }

            if (!injector.TouchMove(_wheelGestureX, clamped))
            {
                EndWheelGesture();
                return;
            }
            _lastInject = "scroll-gesture " + (int)_wheelGesturePos;
        }

        /// <summary>结束滚轮手势：抬起手指，结束按住状态。</summary>
        private void EndWheelGesture()
        {
            System.Windows.Media.CompositionTarget.Rendering -= WheelGestureOnRendering;

            if (!_wheelGestureActive) return;
            _wheelGestureActive = false;

            if (_wheelGestureFrames > 0)
            {
                LogToFile("滚轮手势 帧=" + _wheelGestureFrames + " 位移=" + (int)_wheelGesturePos
                    + " 最大帧间隔=" + (_wheelGestureMaxDt * 1000).ToString("F0") + "ms");
            }

            InputInjector injector = _injector;
            if (injector == null) return;

            int y = Clamp((int)Math.Round(_wheelGestureStartY + _wheelGesturePos),
                0, Math.Max(0, _videoHeight - 1));
            injector.TouchUp(_wheelGestureX, y);
        }

        /// <summary>控制通道不可用时的兜底：退回一次 adb shell swipe。</summary>
        private void FallbackWheelSwipe()
        {
            InputInjector injector = _injector;
            if (injector == null) return;

            double delta = _wheelAccum;
            _wheelAccum = 0;
            if (Math.Abs(delta) < 1) return;

            int x = (int)_wheelPoint.X;
            int y = (int)_wheelPoint.Y;

            // 累积量越大滑得越远，限制在合理区间，避免一次滚动直接跳屏。
            // 滚轮向上（delta > 0）表示查看更上面的内容 = 手指向下滑，因此起点在上。
            int distance = (int)Math.Max(120, Math.Min(600, Math.Abs(delta) * 1.5));
            int from = delta > 0 ? y - distance / 2 : y + distance / 2;
            int to = delta > 0 ? y + distance / 2 : y - distance / 2;

            // 物理分辨率未取到时退回视频尺寸，避免限幅把起止点压成同一点导致滑动无效
            int boundHeight = _deviceHeight > 0
                ? _deviceHeight
                : Math.Max(1, _bitmap != null ? _bitmap.PixelHeight : 1);
            from = Clamp(from, 0, boundHeight - 1);
            to = Clamp(to, 0, boundHeight - 1);
            injector.Swipe(x, from, x, to, 200);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double Clamp(double value, double min, double max)
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
                var psi = new ProcessStartInfo(_adbPath, "-s " + AdbTools.Globals.QuoteSerial(_serial) + " shell wm size")
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

                                // 物理坐标系的尺寸变了（如旋转），同步给注入器用于换算
                                InputInjector injector = _injector;
                                if (injector != null)
                                {
                                    injector.DeviceWidth = w;
                                    injector.DeviceHeight = h;
                                }
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
                var psi = new ProcessStartInfo(_adbPath, "-s " + AdbTools.Globals.QuoteSerial(_serial) + " shell svc power stayon false")
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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WindowProc);
            }
        }

        /// <summary>
        /// 只在用户拖动边框结束时记录尺寸：程序自己设置 Width/Height 不会产生该消息，
        /// 所以不必额外判断"这次变化是不是用户干的"。
        /// </summary>
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmExitSizeMove && WindowState == WindowState.Normal)
            {
                RememberUserSize();
            }
            return IntPtr.Zero;
        }

        /// <summary>记下用户调整后的尺寸，按当前画面方向分别保存。</summary>
        private void RememberUserSize()
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;

            // 用实际尺寸而不是 Width 属性：1:1 走 Win32 调整后属性可能还没同步
            double actualWidth = ActualWidth;
            double actualHeight = ActualHeight;
            if (actualWidth < MinWidth || actualHeight < MinHeight) return;

            bool landscape = IsLandscapeView();
            _userResized = true;

            Size size = new Size(actualWidth, actualHeight);
            if (landscape)
            {
                _userSizeLandscape = size;
            }
            else
            {
                _userSizePortrait = size;
            }

            LogToFile(string.Format("记录用户调整的窗口尺寸: {0:F0}x{1:F0}（{2}）",
                size.Width, size.Height, landscape ? "横屏" : "竖屏"));

            // 持久化到数据库：下次打开该设备沿用，另一个方向不受影响
            RemoteWindowStore.SaveSize(_deviceKey, landscape, size.Width, size.Height);
        }

        /// <summary>
        /// 读取该设备保存过的窗口尺寸：横竖各一份。有记录就按用户设定显示，
        /// 不再按画面比例自动适配；没有记录（首次使用）仍走原来的自动适配。
        /// </summary>
        private void LoadSavedSize()
        {
            double landscapeWidth, landscapeHeight, portraitWidth, portraitHeight;
            RemoteWindowStore.Load(_deviceKey, out landscapeWidth, out landscapeHeight,
                out portraitWidth, out portraitHeight);

            if (landscapeWidth > 0 && landscapeHeight > 0)
            {
                _userSizeLandscape = new Size(landscapeWidth, landscapeHeight);
            }
            if (portraitWidth > 0 && portraitHeight > 0)
            {
                _userSizePortrait = new Size(portraitWidth, portraitHeight);
            }

            _userResized = _userSizeLandscape.HasValue || _userSizePortrait.HasValue;
            if (_userResized)
            {
                LogToFile(string.Format("已加载保存的窗口尺寸：横屏 {0}，竖屏 {1}",
                    Describe(_userSizeLandscape), Describe(_userSizePortrait)));
            }
        }

        private static string Describe(Size? size)
        {
            return size.HasValue
                ? string.Format("{0:F0}x{1:F0}", size.Value.Width, size.Value.Height)
                : "未设置";
        }

        /// <summary>当前画面是否为横屏：优先用已解码的画面，其次用设备分辨率，最后看窗口形状。</summary>
        private bool IsLandscapeView()
        {
            if (_bitmap != null)
            {
                return _bitmap.PixelWidth > _bitmap.PixelHeight;
            }
            if (_deviceWidth > 0 && _deviceHeight > 0)
            {
                return _deviceWidth > _deviceHeight;
            }
            return Width > Height;
        }

        /// <summary>
        /// 用户手动调整过窗口时，切换横竖屏沿用该方向记录过的尺寸；
        /// 这个方向还没调整过就保持当前大小，避免旋转一次就把用户设定冲掉。
        /// 位置不记录：恢复后按屏幕居中，并把尺寸限制在工作区内，确保不会超出屏幕。
        /// </summary>
        private void ApplyUserSize(bool landscape)
        {
            Size? saved = landscape ? _userSizeLandscape : _userSizePortrait;

            if (saved.HasValue)
            {
                Rect workArea = SystemParameters.WorkArea;

                // 尺寸先夹到工作区：窗口比屏幕还大时，怎么摆都会超出
                double width = Math.Min(Math.Max(MinWidth, saved.Value.Width), Math.Max(MinWidth, workArea.Width));
                double height = Math.Min(Math.Max(MinHeight, saved.Value.Height), Math.Max(MinHeight, workArea.Height));

                Width = width;
                Height = height;

                // 不恢复上次的位置（换显示器后旧坐标会跑到屏幕外），一律居中
                Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
                Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);

                LogToFile(string.Format("沿用用户调整的窗口尺寸: {0:F0}x{1:F0}（{2}，已居中）",
                    Width, Height, landscape ? "横屏" : "竖屏"));
            }
            else
            {
                LogToFile("用户已调整窗口，保持当前尺寸（" + (landscape ? "横屏" : "竖屏") + "）");
            }

            // 窗口尺寸可能没变，仍要让机身按新画面比例铺满，延迟到布局完成再算
            Dispatcher.BeginInvoke(new Action(UpdatePhoneFrame), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 「1:1」按钮：把窗口调整到当前输出画面的像素尺寸（画面 1 像素 = 屏幕 1 像素）。
        /// 受屏幕工作区限制，放不下就按比例缩小，并用浮层说明实际比例。
        /// </summary>
        private void PixelFit_Click(object sender, RoutedEventArgs e)
        {
            if (_bitmap == null)
            {
                ShowToast("画面尚未就绪，请稍候再试");
                return;
            }

            // 最大化/最小化时设置 Width/Height 无效，先还原，等布局完成再调整
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
                Dispatcher.BeginInvoke(new Action(ApplyPixelFit), DispatcherPriority.Loaded);
                return;
            }

            ApplyPixelFit();
        }

        /// <summary>按当前画面像素计算窗口尺寸并应用，结果用浮层提示是否为精确 1:1。</summary>
        private void ApplyPixelFit()
        {
            if (_bitmap == null) return;

            double pixelWidth = _bitmap.PixelWidth;
            double pixelHeight = _bitmap.PixelHeight;
            if (pixelWidth <= 0 || pixelHeight <= 0) return;

            // 高 DPI 下窗口尺寸是 DIP，要换算成物理像素才能做到真正的 1:1
            double dpiX = 1.0;
            double dpiY = 1.0;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                System.Windows.Media.Matrix transform = source.CompositionTarget.TransformToDevice;
                if (transform.M11 > 0) dpiX = transform.M11;
                if (transform.M22 > 0) dpiY = transform.M22;
            }

            double statusHeight = _topBar != null && _topBar.ActualHeight > 0
                ? _topBar.ActualHeight + _topBar.Margin.Top + _topBar.Margin.Bottom
                : 32;
            double navHeight = _navBar != null && _navBar.ActualHeight > 0
                ? _navBar.ActualHeight + _navBar.Margin.Top + _navBar.Margin.Bottom
                : 46;
            navHeight = Math.Max(navHeight, 46);

            double bezel = _phoneFrame.Padding.Left + _phoneFrame.Padding.Right
                + _phoneFrame.BorderThickness.Left + _phoneFrame.BorderThickness.Right;
            double bezelV = _phoneFrame.Padding.Top + _phoneFrame.Padding.Bottom
                + _phoneFrame.BorderThickness.Top + _phoneFrame.BorderThickness.Bottom;

            // 客户区目标：状态栏 + 机身（画面 1:1 + 边框）+ 导航栏
            double clientWidth = pixelWidth / dpiX + bezel;
            double clientHeight = pixelHeight / dpiY + bezelV + statusHeight + navHeight;

            // 本次按像素精确设尺寸，期间不要让自动适配插进来重新算
            _autoFitted = true;

            // 窗口外框与客户区的差值用 Win32 实测：WPF 的 ActualWidth 在布局未完成时
            // 与内容宽不同步，按它算会把边框漏掉，导致宽度少一截、画面缩小后上下留白
            double chromeWidth = 0;
            double chromeHeight = 0;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                RECT windowRect;
                RECT clientRect;
                if (GetWindowRect(hwnd, out windowRect) && GetClientRect(hwnd, out clientRect))
                {
                    chromeWidth = ((windowRect.Right - windowRect.Left) - clientRect.Right) / dpiX;
                    chromeHeight = ((windowRect.Bottom - windowRect.Top) - clientRect.Bottom) / dpiY;
                }
            }

            double targetWidth = clientWidth + Math.Max(0, chromeWidth);
            double targetHeight = clientHeight + Math.Max(0, chromeHeight);

            Rect workArea = SystemParameters.WorkArea;
            double scale = 1.0;
            if (targetWidth > workArea.Width || targetHeight > workArea.Height)
            {
                scale = Math.Min(workArea.Width / targetWidth, workArea.Height / targetHeight);
            }

            double finalWidth = Math.Max(MinWidth, Math.Round(targetWidth * scale));
            double finalHeight = Math.Max(MinHeight, Math.Round(targetHeight * scale));

            Width = finalWidth;
            Height = finalHeight;

            // 位置不记录：调整后按屏幕居中，保证不会超出屏幕
            Left = workArea.Left + Math.Max(0, (workArea.Width - finalWidth) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - finalHeight) / 2);
            UpdateLayout();

            // 这次调整视为用户定制尺寸：记进数据库，之后旋转也沿用
            RememberUserSize();
            Dispatcher.BeginInvoke(new Action(UpdatePhoneFrame), DispatcherPriority.Loaded);

            // 实际显示比例 = 最终窗口尺寸与目标窗口尺寸之比。
            // 不读 _root.ActualWidth：布局回读会有滞后；这个比值本身已包含屏幕缩放，
            // 也包含被最小窗口尺寸撑大的情况（此时会大于 100%）。
            double actual = Math.Min(finalWidth / Math.Max(1, targetWidth),
                finalHeight / Math.Max(1, targetHeight));
            int percent = (int)Math.Round(actual * 100);

            string pixelText = (int)pixelWidth + " × " + (int)pixelHeight;
            if (Math.Abs(percent - 100) <= 1)
            {
                ShowToast("已按 1:1 像素显示（" + pixelText + "）");
            }
            else if (percent < 100)
            {
                ShowToast("屏幕放不下 " + pixelText + "，已缩小到 " + percent + "% 显示");
            }
            else
            {
                ShowToast("窗口已到最小尺寸，画面按 " + percent + "% 显示");
            }

            LogToFile(string.Format(
                "1:1 调整：画面 {0}x{1}，客户区目标 {2:F1}x{3:F1}，边框 {4:F1}x{5:F1}，"
                + "目标窗口 {6:F1}x{7:F1}，缩放 {8:P0}，最终 {9:F0}x{10:F0}，实际比例 {11}%",
                (int)pixelWidth, (int)pixelHeight, clientWidth, clientHeight,
                chromeWidth, chromeHeight, targetWidth, targetHeight, scale,
                finalWidth, finalHeight, percent));
        }

        /// <summary>在画面底部显示一条操作提示，几秒后自动隐藏。</summary>
        private void ShowToast(string message)
        {
            _toastText.Text = message;
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /// <summary>
        /// 按画面宽高比计算手机外框尺寸：外框 = 画面显示区域 + 四周内边距，
        /// 这样画面正好填满机身内部，不会在机身里再出现黑边。
        /// </summary>
        private void UpdatePhoneFrame()
        {
            if (_bitmap == null || _screenHost == null) return;
            if (_screenHost.ActualWidth <= 0 || _screenHost.ActualHeight <= 0) return;

            // 边框厚度也要算进去，否则画面区域比例与视频不一致，Uniform 会留出黑边
            double bezel = _phoneFrame.Padding.Left + _phoneFrame.Padding.Right
                + _phoneFrame.BorderThickness.Left + _phoneFrame.BorderThickness.Right;
            double bezelV = _phoneFrame.Padding.Top + _phoneFrame.Padding.Bottom
                + _phoneFrame.BorderThickness.Top + _phoneFrame.BorderThickness.Bottom;
            double availableWidth = Math.Max(1, _screenHost.ActualWidth - bezel);
            double availableHeight = Math.Max(1, _screenHost.ActualHeight - bezelV);

            double scale = Math.Min(availableWidth / _bitmap.PixelWidth, availableHeight / _bitmap.PixelHeight);
            if (scale <= 0) return;

            double newWidth = _bitmap.PixelWidth * scale + bezel;
            double newHeight = _bitmap.PixelHeight * scale + bezelV;
            // 尺寸没变就不要重新赋值：改 Width/Height 会触发新一轮布局，
            // 而 SizeChanged 回调又会回到这里，形成重复布局 pass
            if (!double.IsNaN(_phoneFrame.Width) && !double.IsNaN(_phoneFrame.Height)
                && Math.Abs(_phoneFrame.Width - newWidth) < 0.5
                && Math.Abs(_phoneFrame.Height - newHeight) < 0.5)
            {
                AutoFitToPhone();
                return;
            }

            _phoneFrame.Width = newWidth;
            _phoneFrame.Height = newHeight;

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

            bool landscape = _bitmap.PixelWidth > _bitmap.PixelHeight;

            // 用户手动调整过窗口：不再按画面比例改尺寸，横竖屏各沿用自己那份
            if (_userResized)
            {
                ApplyUserSize(landscape);
                return;
            }

            // 状态栏固定单行（NoWrap），按实际高度取值，避免多算高度让机身上下出现多余空白
            double statusHeight = _topBar != null && _topBar.ActualHeight > 0
                ? _topBar.ActualHeight + _topBar.Margin.Top + _topBar.Margin.Bottom
                : 32;
            double navHeight = _navBar != null && _navBar.ActualHeight > 0
                ? _navBar.ActualHeight + _navBar.Margin.Top + _navBar.Margin.Bottom
                : 46;
            navHeight = Math.Max(navHeight, 46);
            // 边框厚度也要算进去，否则画面区域比例与视频不一致，Uniform 会留出黑边
            double bezel = _phoneFrame.Padding.Left + _phoneFrame.Padding.Right
                + _phoneFrame.BorderThickness.Left + _phoneFrame.BorderThickness.Right;
            double bezelV = _phoneFrame.Padding.Top + _phoneFrame.Padding.Bottom
                + _phoneFrame.BorderThickness.Top + _phoneFrame.BorderThickness.Bottom;
            const double outerMargin = 16;

            // 横屏窗口会被撑到接近整屏宽度，显得过大：宽度收到六成半左右；
            // 竖屏接近手机比例，仍按高度尽量铺满。
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

            // 原始分辨率按设备物理尺寸采集，只有超过 4K（约 830 万像素）才压缩，
            // 避免 1800x2880 这类 5MP 屏被误降到 0.9 倍；
            // 选择具体档位时仍限制在约 420 万像素，控制编解码与转换开销。
            double maxPixels = targetLongSide > 0 ? 4200000.0 : 8300000.0;
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
                    "-s " + AdbTools.Globals.QuoteSerial(_serial) + " shell \"dumpsys input | grep -m1 'Viewport INTERNAL'\"")
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
        private DateTime _lastBurstRequest = DateTime.MinValue;

        private void RequestOrientationCheck()
        {
            DateTime now = DateTime.UtcNow;
            // 鼠标按下、片段重建会连续触发；burst 未结束时不必反复延长，
            // 避免短时间内堆起多个 adb 方向查询进程
            if (now < _orientationBurstUntil && (now - _lastBurstRequest).TotalMilliseconds < 500) return;
            _lastBurstRequest = now;
            _orientationBurstUntil = now.AddSeconds(2);
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
                var psi = new ProcessStartInfo(_adbPath, "-s " + AdbTools.Globals.QuoteSerial(_serial) + " shell \"" + script + "\"")
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

        /// <summary>
        /// 解析按键 Tag：keyCode[:repeat][:longpress]。
        /// repeat 表示按住时每 180ms 重发一次完整按键（音量键，松开即停、不限次数）；
        /// longpress 表示松开时按按住时长决定短按 / --longpress（电源键）。
        /// 实测设备不会响应"只按下不抬起"的注入事件，因此重复必须每次发完整按键。
        /// </summary>
        private static void ParseNavTag(object tag, out int keyCode,
            out bool repeatOnHold, out bool longPressOnHold)
        {
            repeatOnHold = false;
            longPressOnHold = false;
            string[] parts = (tag as string ?? string.Empty).Split(':');
            int.TryParse(parts[0], out keyCode);
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "repeat") repeatOnHold = true;
                else if (parts[i] == "longpress") longPressOnHold = true;
            }
        }

        /// <summary>画面宿主尺寸变化：重新按画面比例计算机身尺寸。</summary>
        private void RemoteScreen_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePhoneFrame();
        }

        /// <summary>
        /// 画面自身尺寸变化后再更新圆角裁剪。
        /// 不能在宿主 SizeChanged 里直接裁剪：那一刻 _image 还是旧尺寸，
        /// Clip 会偏小，导致画面右侧/底部被切掉。
        /// </summary>
        private void Image_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateScreenClip();
        }

        private double _lastClipWidth;
        private double _lastClipHeight;

        /// <summary>按屏幕内圆角裁剪画面，避免画面直角盖住机身圆角。</summary>
        private void UpdateScreenClip()
        {
            double w = _image.ActualWidth;
            double h = _image.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                if (_image.Clip != null) _image.Clip = null;
                _lastClipWidth = _lastClipHeight = 0;
                return;
            }

            // 尺寸未变时不要重建裁剪几何，避免每次布局都分配对象
            if (_image.Clip != null
                && Math.Abs(w - _lastClipWidth) < 0.5
                && Math.Abs(h - _lastClipHeight) < 0.5) return;

            _lastClipWidth = w;
            _lastClipHeight = h;
            double radius = Math.Min(24.0, Math.Min(w, h) * 0.045);
            _image.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, w, h), radius, radius);
        }

        /// <summary>
        /// 按键按下：音量键立即注入并开始每 180ms 连发；电源键只记录按下时刻。
        /// 普通按键交给 Click 处理。
        /// </summary>
        private void NavButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var button = (Button)sender;
            ParseNavTag(button.Tag, out int keyCode, out bool repeatOnHold, out bool longPressOnHold);
            if (!repeatOnHold && !longPressOnHold) return;

            _navPressStarted[button] = DateTime.UtcNow;
            if (!repeatOnHold) return;

            // 音量：按下立即调一次，之后每 180ms 重复，松开即停（不限次数）。
            // 实测注入"只按下不抬起"的设备不会响应，必须每次发完整按键事件。
            InjectNavKey(keyCode);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += (s2, e2) => InjectNavKey(keyCode);
            timer.Start();
            _navHoldTimers[button] = timer;
        }

        /// <summary>按键松开：停止连发；电源键按按住时长决定短按 / 长按。</summary>
        private void NavButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var button = (Button)sender;
            StopNavHoldTimer(button);

            DateTime releasedAt = DateTime.UtcNow;
            _navLastRelease[button] = releasedAt;

            ParseNavTag(button.Tag, out _, out _, out bool longPressOnHold);
            if (!longPressOnHold) return;

            DateTime pressStarted;
            if (!_navPressStarted.TryGetValue(button, out pressStarted)) pressStarted = releasedAt;
            else _navPressStarted.Remove(button);
            HandlePowerRelease((releasedAt - pressStarted).TotalMilliseconds);
        }

        /// <summary>鼠标移出时停止连发，避免拖走后还在响。</summary>
        private void NavButton_MouseLeave(object sender, MouseEventArgs e)
        {
            StopNavHoldTimer((Button)sender);
        }

        /// <summary>失去鼠标捕获时停止连发。</summary>
        private void NavButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            StopNavHoldTimer((Button)sender);
        }

        /// <summary>
        /// 按键单击：键盘/自动化触发（没有鼠标按下事件）时补一次；
        /// 鼠标点击后紧跟的 Click 要跳过，否则长按松手会多注入一次。
        /// </summary>
        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            DateTime lastReleaseAt;
            if (_navLastRelease.TryGetValue(button, out lastReleaseAt)
                && (DateTime.UtcNow - lastReleaseAt).TotalMilliseconds < 500)
            {
                return;
            }

            ParseNavTag(button.Tag, out int keyCode, out _, out _);
            InjectNavKey(keyCode);
        }

        private void StopNavHoldTimer(Button button)
        {
            DispatcherTimer timer;
            if (!_navHoldTimers.TryGetValue(button, out timer)) return;
            timer.Stop();
            _navHoldTimers.Remove(button);
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

            // 音量键按住会每 180ms 连发一次，日志按 500ms 节流，避免刷屏
            if ((DateTime.UtcNow - _lastInjectLog).TotalMilliseconds >= 500)
            {
                _lastInjectLog = DateTime.UtcNow;
                LogToFile("注入 keyevent " + keyCode + (longPress ? "（长按）" : ""));
            }

            if (longPress) _injector.KeyEventLongPress(keyCode);
            else _injector.KeyEvent(keyCode);
            _lastInject = "keyevent " + keyCode;
        }

        /// <summary>
        /// 把窗口内坐标换算成注入坐标（按等比缩放后的实际显示区域）。
        /// videoX/videoY 是视频帧坐标，控制通道直接使用——它天然跟随旋转与缩放，无需再换算；
        /// deviceX/deviceY 是当前方向下的设备像素坐标，供 adb shell 回退路径使用。
        /// </summary>
        private bool TryMapToDevice(Point point, out int deviceX, out int deviceY,
            out int videoX, out int videoY)
        {
            deviceX = 0;
            deviceY = 0;
            videoX = 0;
            videoY = 0;
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

            // 画面像素位置就是服务端 videoSize 坐标系下的坐标
            videoX = (int)px;
            videoY = (int)py;

            // 视频流是缩放后的尺寸（如 720x1280），而 input 注入必须使用设备物理像素（如 1312x2848）
            if (_deviceWidth <= 0 || _deviceHeight <= 0)
            {
                deviceX = videoX;
                deviceY = videoY;
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

        /// <summary>
        /// 关闭时先取消关闭，用中央卡片提示；等后台清理完成后再放行真正关闭。
        /// 清理会启动多个 adb 进程并等待取流线程退出，全部放在后台线程，
        /// 主界面与远程控制窗口共用同一个 Dispatcher，因此不会短暂无响应。
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_cleanupDone) return;
            e.Cancel = true;
            if (_closing) return;   // 清理进行中，继续显示卡片

            _closing = true;
            try { if (_rotationTimer != null) _rotationTimer.Stop(); } catch { }
            try { if (_stateTimer != null) _stateTimer.Stop(); } catch { }
            EndWheelGesture();

            ShowClosingNotice("正在关闭远程控制", "正在停止取流并释放解码资源…");

            // 兜底：清理异常卡住时也要让用户能关掉窗口
            _closeGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _closeGuard.Tick += (s, ev) => { _closeGuard.Stop(); FinishClose(); };
            _closeGuard.Start();

            IVideoStream stream = _stream;
            H264DecoderMft decoder = _decoder;
            Thread pump = _pumpThread;
            InputInjector injector = _injector;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (stream != null) stream.Stop(); } catch { }
                try { if (pump != null) pump.Join(1500); } catch { }
                try { if (decoder != null) decoder.Dispose(); } catch { }
                try { MfInterop.Shutdown(); } catch { }

                ShowClosingNotice("正在关闭远程控制", "正在清理设备端服务与转发…");
                try { if (stream != null) stream.ReleaseDevice(); } catch { }
                try { if (stream != null) stream.Dispose(); } catch { }
                try { if (injector != null) injector.Dispose(); } catch { }
                try { RestoreDeviceState(); } catch { }

                ShowClosingNotice("清理完成", "窗口即将关闭…");
                Thread.Sleep(300);
                Dispatcher.BeginInvoke(new Action(FinishClose));
            });
        }

        /// <summary>更新关闭过程的中央卡片（可从后台线程调用）。</summary>
        private void ShowClosingNotice(string title, string detail)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closing) return;
                _startupTitle.Text = title;
                _startupSteps.Text = detail;
                _startupPanel.Visibility = Visibility.Visible;
                _phoneFrame.Visibility = Visibility.Visible;
            }));
        }

        /// <summary>清理完成（或超时）后放行关闭，幂等。</summary>
        private void FinishClose()
        {
            if (_cleanupDone) return;
            _cleanupDone = true;
            if (_closeGuard != null) { try { _closeGuard.Stop(); } catch { } }
            try { Close(); } catch { }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            // 资源清理已在 Window_Closing 的后台流程中完成，这里只做轻量收尾
            try { if (_rotationTimer != null) _rotationTimer.Stop(); } catch { }
            try { if (_stateTimer != null) _stateTimer.Stop(); } catch { }
            try { if (_closeGuard != null) _closeGuard.Stop(); } catch { }
            EndWheelGesture();

            // 断开引用，让缓存的显示位图可以回收
            _image.Source = null;
            _bitmap = null;
            _bitmapCache.Clear();
        }
    }
}
