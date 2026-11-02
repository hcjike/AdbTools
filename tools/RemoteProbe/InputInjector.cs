using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RemoteProbe
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

        /// <summary>设备是否支持 `input motionevent`（Android 11 / SDK 30 及以上）。</summary>
        public bool SupportsMotionEvent { get; private set; }

        public InputInjector(string adbPath, string serial)
        {
            _adbPath = adbPath;
            _serial = serial;
            SupportsMotionEvent = QuerySdk() >= 30;
            Start();
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
            Send("input keyevent " + keyCode.ToString(CultureInfo.InvariantCulture));
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
