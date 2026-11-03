using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 输入注入：保持一个常驻的 `adb shell` 进程，把命令写入其 stdin，
    /// 避免每次点击都重新启动 adb + Java 进程（单次可省 60~100ms）。
    /// </summary>
    internal sealed class InputInjector : IDisposable
    {
        private readonly string _adbPath;
        private readonly string _serial;
        private Process _process;
        private readonly object _writeLock = new object();
        private volatile bool _disposed;

        /// <summary>可用于原始电源键注入的输入设备路径；为空表示当前无权限（多为无 root 设备）。</summary>
        private string _powerDevice;

        /// <summary>sendevent 是否需要加 su -c 前缀。</summary>
        private bool _powerUseSu;

        /// <summary>设备是否支持 `input motionevent`（Android 11 / SDK 30 及以上）。</summary>
        public bool SupportsMotionEvent { get; private set; }

        /// <summary>
        /// 可选的 scrcpy 控制通道。可用时触摸与按键直接走它：每次注入只写一次本地 TCP，
        /// 不必启动 adb shell + Java 进程，延迟从百毫秒级降到几毫秒，拖拽才能跟手。
        /// </summary>
        public ScrcpyVideoStream Control { get; set; }

        /// <summary>设备物理分辨率（仅 adb shell 回退路径使用；控制通道按视频坐标注入）。</summary>
        public int DeviceWidth { get; set; }
        public int DeviceHeight { get; set; }

        /// <summary>视频帧的可见尺寸（服务端 videoSize），控制通道注入时使用该坐标系。</summary>
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }

        /// <summary>
        /// 是否支持实时触摸（按下/移动/抬起逐条注入）。
        /// 不支持时只能等抬手后用一次 tap / swipe 完成，拖动过程画面不会跟随。
        /// </summary>
        public bool SupportsLiveTouch
        {
            get
            {
                ScrcpyVideoStream control = Control;
                return control != null && control.ControlAvailable
                    && VideoWidth > 0 && VideoHeight > 0;
            }
        }

        public InputInjector(string adbPath, string serial)
        {
            _adbPath = adbPath;
            _serial = serial;
            SupportsMotionEvent = QuerySdk() >= 30;
            Start();
            DetectPowerDevice();
        }

        private int QuerySdk()
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell getprop ro.build.version.sdk")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    int sdk;
                    return int.TryParse(text, out sdk) ? sdk : 0;
                }
            }
            catch { return 0; }
        }

        private void Start()
        {
            var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = psi };
            _process.Start();

            // 持续读取输出，避免管道写满导致 shell 阻塞
            var drain = new Thread(() =>
            {
                try { _process.StandardOutput.ReadToEnd(); } catch { }
            }) { IsBackground = true, Name = "adb-shell-out" };
            drain.Start();

            var drainErr = new Thread(() =>
            {
                try { _process.StandardError.ReadToEnd(); } catch { }
            }) { IsBackground = true, Name = "adb-shell-err" };
            drainErr.Start();
        }

        private void Send(string command)
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                try
                {
                    if (_process.HasExited) Restart();
                    _process.StandardInput.WriteLine(command);
                    _process.StandardInput.Flush();
                }
                catch
                {
                    try { Restart(); } catch { }
                }
            }
        }

        private void Restart()
        {
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            try { _process.Dispose(); } catch { }
            Start();
        }

        public void Tap(int x, int y)
        {
            Send(string.Format(CultureInfo.InvariantCulture, "input tap {0} {1}", x, y));
        }

        /// <summary>
        /// 实时按下。x/y 为**视频帧坐标**（画面像素位置），与旋转、缩放无关，
        /// 服务端要求的坐标系就是 videoSize，直接使用即可。返回 false 表示不可用。
        /// </summary>
        public bool TouchDown(int x, int y)
        {
            return Touch(0, x, y);
        }

        /// <summary>实时移动（视频帧坐标）。</summary>
        public bool TouchMove(int x, int y)
        {
            return Touch(2, x, y);
        }

        /// <summary>实时抬起（视频帧坐标）。</summary>
        public bool TouchUp(int x, int y)
        {
            return Touch(1, x, y);
        }

        private bool Touch(int action, int x, int y)
        {
            ScrcpyVideoStream control = Control;
            if (control == null || !control.ControlAvailable) return false;

            int vw = VideoWidth, vh = VideoHeight;
            if (vw <= 0 || vh <= 0) return false;

            if (x < 0) x = 0; else if (x >= vw) x = vw - 1;
            if (y < 0) y = 0; else if (y >= vh) y = vh - 1;
            return control.SendTouch(action, x, y, vw, vh);
        }

        public void Swipe(int x1, int y1, int x2, int y2, int durationMs)
        {
            Send(string.Format(CultureInfo.InvariantCulture, "input swipe {0} {1} {2} {3} {4}", x1, y1, x2, y2, durationMs));
        }

        public void MotionDown(int x, int y)
        {
            Send(string.Format(CultureInfo.InvariantCulture, "input motionevent DOWN {0} {1}", x, y));
        }

        public void MotionMove(int x, int y)
        {
            Send(string.Format(CultureInfo.InvariantCulture, "input motionevent MOVE {0} {1}", x, y));
        }

        public void MotionUp(int x, int y)
        {
            Send(string.Format(CultureInfo.InvariantCulture, "input motionevent UP {0} {1}", x, y));
        }

        public void KeyEvent(int keyCode)
        {
            // 控制通道可用时直接注入按下/抬起，避免每条按键都起一次 Java 进程
            ScrcpyVideoStream control = Control;
            if (control != null && control.ControlAvailable
                && control.SendKey(0, keyCode) && control.SendKey(1, keyCode))
            {
                return;
            }
            Send("input keyevent " + keyCode.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>长按按键（系统自带的 --longpress，约 500ms 按下）。</summary>
        public void KeyEventLongPress(int keyCode)
        {
            Send("input keyevent --longpress " + keyCode.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 是否支持"按住指定时长"的电源键注入：需要设备端能写 /dev/input 事件（多数无 root 设备不支持）。
        /// 不支持时只能退回 --longpress。
        /// </summary>
        public bool SupportsPowerHold { get { return !string.IsNullOrEmpty(_powerDevice); } }

        /// <summary>按下电源键不松开（sendevent 原始事件，必要时走 su）。</summary>
        public void PowerDown()
        {
            if (!SupportsPowerHold) return;
            RunPowerEvent("1 116 1");
        }

        /// <summary>松开电源键（sendevent 原始事件，必要时走 su）。</summary>
        public void PowerUp()
        {
            if (!SupportsPowerHold) return;
            RunPowerEvent("1 116 0");
        }

        private void RunPowerEvent(string args)
        {
            string cmd = "sendevent " + _powerDevice + " " + args + "; sendevent " + _powerDevice + " 0 0 0";
            Send(_powerUseSu ? "su -c \"" + cmd + "\"" : cmd);
        }

        /// <summary>查找带 KEY_POWER 且当前用户可写的输入设备（如 /dev/input/event4）。</summary>
        private void DetectPowerDevice()
        {
            try
            {
                string text;
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " shell getevent -pl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(6000);
                }

                // 优先挑名字像电源键的设备（pmic_pwrkey / gpio-keys / power）
                string fallback = null;
                foreach (var chunk in text.Split(new[] { "add device" }, StringSplitOptions.None))
                {
                    if (chunk.IndexOf("KEY_POWER", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    int start = chunk.IndexOf("/dev/input/event", StringComparison.Ordinal);
                    if (start < 0) continue;

                    string dev = chunk.Substring(start).Trim();
                    int end = dev.IndexOfAny(new[] { ' ', '\r', '\n', '"' });
                    if (end > 0) dev = dev.Substring(0, end);

                    bool preferred = chunk.IndexOf("pwrkey", StringComparison.OrdinalIgnoreCase) >= 0
                        || chunk.IndexOf("gpio-keys", StringComparison.OrdinalIgnoreCase) >= 0
                        || chunk.IndexOf("power", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (preferred)
                    {
                        if (IsWritable(dev)) { _powerDevice = dev; return; }
                    }
                    else if (fallback == null)
                    {
                        fallback = dev;
                    }
                }

                if (fallback != null && IsWritable(fallback)) _powerDevice = fallback;
            }
            catch { }
        }

        /// <summary>
        /// 设备文件是否可写：先试 adb shell 自身权限，不行再试 su（部分已 root 设备需要 su）。
        /// 命中 su 时会把后续 sendevent 命令加上 su -c 前缀。
        /// </summary>
        private bool IsWritable(string devicePath)
        {
            if (RunCommand("shell test -w " + devicePath) == 0)
            {
                _powerUseSu = false;
                return true;
            }

            if (RunCommand("shell \"su -c 'test -w " + devicePath + "'\"") == 0)
            {
                _powerUseSu = true;
                return true;
            }

            return false;
        }

        private int RunCommand(string adbArgs)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + _serial + " " + adbArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch { return -1; }
        }

        /// <summary>输入文本（仅 ASCII）；空格与常见 shell 特殊字符会被转义。</summary>
        public void Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sb = new StringBuilder("input text ");
            foreach (char c in text)
            {
                if (c < 32 || c > 126) continue; // 非 ASCII 无法通过 input text 发送
                switch (c)
                {
                    case ' ': sb.Append("%s"); break;
                    case '(': case ')': case '<': case '>': case '|': case ';': case '&':
                    case '*': case '\\': case '"': case '\'': case '`': case '~':
                        sb.Append('\\').Append(c); break;
                    default: sb.Append(c); break;
                }
            }
            if (sb.Length > "input text ".Length) Send(sb.ToString());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _process.StandardInput.WriteLine("exit");
                _process.StandardInput.Flush();
            }
            catch { }
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            try { _process.Dispose(); } catch { }
        }
    }
}
