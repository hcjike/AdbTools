using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdbTools
{
    /// <summary>日志行：文本 + 按级别着色的前景色。</summary>
    public sealed class LogLine
    {
        public LogLine(string text, Brush foreground)
        {
            Text = text;
            Foreground = foreground;
        }

        public string Text { get; }
        public Brush Foreground { get; }
    }

    /// <summary>
    /// 独立的设备日志窗口：流式读取 logcat，支持缓冲区切换、级别与关键字过滤、
    /// 暂停/继续、清空、保存到日志目录。
    /// </summary>
    public partial class DeviceLogWindow : Window
    {
        private const int MaxVisibleLines = 5000;
        private const int MaxRawLines = 20000;

        // 与 Android Studio logcat 对齐的级别配色（Holo 体系）
        private static readonly Brush VerboseBrush = Frozen("#FFA8A8A8");   // V 灰
        private static readonly Brush DebugBrush = Frozen("#FF33B5E5");     // D 蓝
        private static readonly Brush InfoBrush = Frozen("#FF99CC00");      // I 绿
        private static readonly Brush WarningBrush = Frozen("#FFFFBB33");   // W 橙
        private static readonly Brush ErrorBrush = Frozen("#FFFF4444");     // E 红
        private static readonly Brush AssertBrush = Frozen("#FFAA66CC");    // F/A 紫

        private static readonly Brush RunningBrush = Frozen("#FF34C38F");
        private static readonly Brush PausedBrush = Frozen("#FFF0B45A");

        private readonly string _adbPath;
        private readonly string _serial;
        private readonly string _deviceName;

        private readonly ObservableCollection<LogLine> _visible = new ObservableCollection<LogLine>();
        private readonly List<string> _raw = new List<string>();
        private readonly List<LogLine> _pending = new List<LogLine>();
        private readonly object _rawLock = new object();
        private readonly object _pendingLock = new object();

        private Process _process;
        private Thread _reader;
        private volatile bool _closing;
        private bool _paused;
        private string _buffer = "main";
        private int _minLevel;
        private string _keyword = string.Empty;

        /// <summary>状态栏临时提示（复制成功等），到期后自动恢复常规状态。</summary>
        private string _flashMessage;
        private DateTime _flashUntil;

        private DispatcherTimer _flushTimer;
        private DispatcherTimer _filterTimer;

        public DeviceLogWindow(string adbPath, string serial, string deviceName, string initialKeyword = null)
        {
            InitializeComponent();

            _adbPath = adbPath;
            _serial = serial;
            _deviceName = deviceName;
            Title = "设备日志 - " + deviceName;

            logList.ItemsSource = _visible;

            InitBuffers();
            InitLevels();

            if (!string.IsNullOrEmpty(initialKeyword))
            {
                filterBox.Text = initialKeyword;
                _keyword = initialKeyword.Trim();
            }

            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _flushTimer.Tick += (s, e) => FlushPending();

            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _filterTimer.Tick += (s, e) =>
            {
                _filterTimer.Stop();
                _keyword = filterBox.Text.Trim();
                RebuildView();
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartStream();
            _flushTimer.Start();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _closing = true;
            try { _flushTimer?.Stop(); } catch { }
            try { _filterTimer?.Stop(); } catch { }
            KillProcess();
        }

        private void InitBuffers()
        {
            AddBuffer("main", "main 主日志");
            AddBuffer("system", "system 系统");
            AddBuffer("crash", "crash 崩溃");
            AddBuffer("events", "events 事件");
            AddBuffer("all", "全部缓冲");
            bufferBox.SelectedIndex = 0;
            UpdateBufferTooltip();
        }

        private void AddBuffer(string tag, string text)
        {
            var item = new ComboBoxItem
            {
                Content = text,
                Tag = tag,
                ToolTip = GetBufferTip(tag)
            };
            // 提示文案较长，延长显示时间，避免还没看完就消失
            ToolTipService.SetShowDuration(item, 20000);
            bufferBox.Items.Add(item);
        }

        private void InitLevels()
        {
            AddLevel(0, "全部级别");
            AddLevel(1, "Debug 及以上");
            AddLevel(2, "Info 及以上");
            AddLevel(3, "Warn 及以上");
            AddLevel(4, "Error 及以上");
            levelBox.SelectedIndex = 0;
            UpdateLevelTooltip();
        }

        private void AddLevel(int level, string text)
        {
            var item = new ComboBoxItem
            {
                Content = text,
                Tag = level,
                ToolTip = GetLevelTip(level)
            };
            ToolTipService.SetShowDuration(item, 20000);
            levelBox.Items.Add(item);
        }

        /// <summary>按当前缓冲更新组合框本身的悬停说明。</summary>
        private void UpdateBufferTooltip()
        {
            ComboBoxItem item = bufferBox.SelectedItem as ComboBoxItem;
            bufferBox.ToolTip = GetBufferTip(item?.Tag as string ?? "main");
        }

        /// <summary>按当前级别更新组合框本身的悬停说明。</summary>
        private void UpdateLevelTooltip()
        {
            levelBox.ToolTip = GetLevelTip(_minLevel);
        }

        /// <summary>缓冲项的详细说明，下拉项与选中框共用。</summary>
        private static string GetBufferTip(string tag)
        {
            switch (tag)
            {
                case "system":
                    return "system（系统）：系统服务与框架层输出，排查权限、服务异常时使用";
                case "crash":
                    return "crash（崩溃）：只含崩溃堆栈，体积小，定位闪退最快";
                case "events":
                    return "events（事件）：Activity 启动、ANR、进程死亡等系统事件";
                case "all":
                    return "全部缓冲：包含 main / system / crash / events，最全但体积最大";
                default:
                    return "main（主日志）：应用与系统服务的常规输出，排查异常最常用";
            }
        }

        /// <summary>级别项的详细说明，下拉项与选中框共用。</summary>
        private static string GetLevelTip(int level)
        {
            switch (level)
            {
                case 1:
                    return "Debug 及以上：过滤 Verbose 日志，减少噪音";
                case 2:
                    return "Info 及以上：只看常规信息、警告与错误";
                case 3:
                    return "Warn 及以上：快速定位异常与失败";
                case 4:
                    return "Error 及以上：只看明确的错误与致命异常";
                default:
                    return "全部级别：Verbose / Debug / Info / Warn / Error / Fatal";
            }
        }

        private void Buffer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            ComboBoxItem item = bufferBox.SelectedItem as ComboBoxItem;
            string buffer = item?.Tag as string ?? "main";
            UpdateBufferTooltip();
            if (string.Equals(buffer, _buffer, StringComparison.Ordinal)) return;

            _buffer = buffer;
            ClearLocal();
            StartStream();
        }

        private void Level_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            ComboBoxItem item = levelBox.SelectedItem as ComboBoxItem;
            _minLevel = item?.Tag is int level ? level : 0;
            UpdateLevelTooltip();
            RebuildView();
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 构造阶段设置初始关键字时定时器还没建好，直接记录即可
            if (_filterTimer == null)
            {
                _keyword = filterBox.Text.Trim();
                return;
            }

            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            pauseButton.Content = _paused ? "继续" : "暂停";
            UpdateStatus();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "将清空当前显示，并同时清除设备端「" + _buffer + "」缓冲区的日志。\r\n是否继续？",
                "清空日志", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            ClearLocal();
            LogCollector.ClearBuffer(_adbPath, _serial, _buffer);
            UpdateStatus("已请求清空设备端日志缓冲");
        }

        private void LogList_KeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            if (e.Key == Key.C)
            {
                CopyLines(false);
                e.Handled = true;
            }
            else if (e.Key == Key.A)
            {
                logList.SelectAll();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 复制日志：all 为 true 时复制当前显示的全部行；
        /// 否则复制选中行，未选中任何行时等同于复制全部。
        /// </summary>
        private void CopyLines(bool all)
        {
            StringBuilder builder = new StringBuilder();
            int count = 0;

            if (all || logList.SelectedItems.Count == 0)
            {
                foreach (LogLine line in _visible)
                {
                    builder.AppendLine(line.Text);
                    count++;
                }
            }
            else
            {
                // 按显示顺序复制，避免跟随点击顺序打乱日志
                var selected = new HashSet<LogLine>();
                foreach (object item in logList.SelectedItems)
                {
                    LogLine line = item as LogLine;
                    if (line != null) selected.Add(line);
                }

                foreach (LogLine line in _visible)
                {
                    if (!selected.Contains(line)) continue;
                    builder.AppendLine(line.Text);
                    count++;
                }
            }

            if (count == 0) return;

            try
            {
                Clipboard.SetText(builder.ToString());
                Flash("已复制 " + count + " 行到剪贴板");
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>在状态栏短暂显示一条提示，2 秒后恢复常规状态。</summary>
        private void Flash(string message)
        {
            _flashMessage = message;
            _flashUntil = DateTime.UtcNow.AddSeconds(2);
            UpdateStatus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = LogCollector.GetDeviceLogDirectory(_deviceName);
                string name = "logcat-" + _buffer + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                string path = Path.Combine(dir, name);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AdbTools " + App.Version);
                sb.AppendLine("抓取时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("设备名称：" + _deviceName);
                sb.AppendLine("序列号：" + _serial);
                sb.AppendLine("缓冲：" + _buffer);
                sb.AppendLine("关键字：" + (_keyword.Length > 0 ? _keyword : "-"));
                sb.AppendLine(new string('-', 60));
                foreach (LogLine line in _visible)
                {
                    sb.AppendLine(line.Text);
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

                if (MessageBoxResult.Yes == MessageBox.Show(
                    "已保存到：\r\n" + path + "\r\n\r\n是否打开所在文件夹？",
                    "保存成功", MessageBoxButton.YesNo, MessageBoxImage.Information))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", LogCollector.GetDeviceLogDirectory(_deviceName));
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            ClearLocal();
            StartStream();
        }

        private void StartStream()
        {
            KillProcess();

            string arguments = "-s " + Globals.QuoteSerial(_serial)
                + " logcat -b " + _buffer + " -v threadtime";
            var psi = new ProcessStartInfo(_adbPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process process = new Process { StartInfo = psi };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) AddLine("[adb] " + e.Data);
            };

            try
            {
                process.Start();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                UpdateStatus("启动日志流失败：" + ex.Message);
                try { process.Dispose(); } catch { }
                return;
            }

            _process = process;
            UpdateStatus("日志流已启动");

            _reader = new Thread(() =>
            {
                try
                {
                    string line;
                    while (!_closing && (line = process.StandardOutput.ReadLine()) != null)
                    {
                        AddLine(line);
                    }
                }
                catch
                {
                    // 关闭窗口/重连时进程被结束，读取会抛异常，属正常退出
                }

                if (!_closing)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        UpdateStatus("日志流已结束，设备可能已断开，可点「重连」")));
                }
            })
            {
                IsBackground = true,
                Name = "device-logcat"
            };
            _reader.Start();
        }

        private void KillProcess()
        {
            Process process = _process;
            _process = null;
            if (process == null) return;

            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
        }

        private void AddLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            lock (_rawLock)
            {
                _raw.Add(line);
                if (_raw.Count > MaxRawLines) _raw.RemoveRange(0, _raw.Count - MaxRawLines);
            }

            int level = GetLevel(line);
            if (!Matches(line, level)) return;

            LogLine item = new LogLine(line, BrushForLevel(level, line));
            lock (_pendingLock)
            {
                _pending.Add(item);
                if (_pending.Count > MaxRawLines) _pending.RemoveRange(0, _pending.Count - MaxRawLines);
            }
        }

        private bool Matches(string line, int level)
        {
            if (level < _minLevel) return false;
            if (_keyword.Length > 0
                && line.IndexOf(_keyword, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static Brush BrushForLevel(int level, string line)
        {
            if (line.StartsWith("[adb]", StringComparison.Ordinal)) return ErrorBrush;
            // 缓冲区分隔行（--------- beginning of main）用灰色，不抢眼
            if (line.StartsWith("---------", StringComparison.Ordinal)) return VerboseBrush;

            switch (level)
            {
                case 0:
                    return VerboseBrush;
                case 1:
                    return DebugBrush;
                case 3:
                    return WarningBrush;
                case 4:
                    return ErrorBrush;
                case 5:
                    return AssertBrush;
                default:
                    return InfoBrush;
            }
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 从 threadtime 行中取日志级别：日期 时间 PID TID 级别 ...
        /// 取不到（如 "--------- beginning of main"）时按 I 处理。
        /// </summary>
        private static int GetLevel(string line)
        {
            int token = 0;
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && line[i] == ' ') i++;
                if (i >= line.Length) break;

                int start = i;
                while (i < line.Length && line[i] != ' ') i++;
                token++;

                if (token == 5)
                {
                    switch (char.ToUpperInvariant(line[start]))
                    {
                        case 'V': return 0;
                        case 'D': return 1;
                        case 'I': return 2;
                        case 'W': return 3;
                        case 'E': return 4;
                        case 'F': return 5;
                        case 'A': return 5;
                        default: return 2;
                    }
                }
            }
            // 无级别行（堆栈续行等）按 Info 处理，保证开启级别过滤后仍能看到堆栈内容
            return 2;
        }

        private void FlushPending()
        {
            if (_paused) return;

            LogLine[] lines;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                lines = _pending.ToArray();
                _pending.Clear();
            }

            foreach (LogLine line in lines)
            {
                _visible.Add(line);
            }
            while (_visible.Count > MaxVisibleLines) _visible.RemoveAt(0);

            UpdateStatus();
            ScrollToBottom();
        }

        private void RebuildView()
        {
            lock (_pendingLock) _pending.Clear();

            string[] snapshot;
            lock (_rawLock) snapshot = _raw.ToArray();

            _visible.Clear();
            int start = Math.Max(0, snapshot.Length - MaxVisibleLines);
            for (int i = start; i < snapshot.Length; i++)
            {
                int level = GetLevel(snapshot[i]);
                if (Matches(snapshot[i], level))
                {
                    _visible.Add(new LogLine(snapshot[i], BrushForLevel(level, snapshot[i])));
                }
            }

            UpdateStatus();
            ScrollToBottom();
        }

        /// <summary>
        /// 只纵向滚动到底部：ScrollIntoView 会连带把横向偏移一起拉走，
        /// 导致长行日志的横向滚动条看起来位置异常。
        /// </summary>
        private void ScrollToBottom()
        {
            if (autoScrollBox.IsChecked != true) return;

            ScrollViewer viewer = FindScrollViewer(logList);
            if (viewer != null)
            {
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                ScrollViewer viewer = child as ScrollViewer;
                if (viewer != null) return viewer;

                ScrollViewer nested = FindScrollViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void ClearLocal()
        {
            lock (_rawLock) _raw.Clear();
            lock (_pendingLock) _pending.Clear();
            _visible.Clear();
            UpdateStatus();
        }

        private void UpdateStatus(string hint = null)
        {
            int rawCount;
            lock (_rawLock) rawCount = _raw.Count;

            statusDot.Fill = _paused
                ? PausedBrush
                : (hint != null && hint.IndexOf("结束", StringComparison.Ordinal) >= 0
                    ? ErrorBrush
                    : RunningBrush);

            string text = hint ??
                ("缓冲 " + _buffer + "    共接收 " + rawCount + " 行，显示 " + _visible.Count + " 行");
            if (hint == null && _paused) text += "（已暂停）";

            if (hint == null)
            {
                if (_flashMessage != null && DateTime.UtcNow < _flashUntil)
                {
                    text = _flashMessage + "    " + text;
                }
                else
                {
                    _flashMessage = null;
                }
            }

            statusText.Text = text;
        }
    }
}
