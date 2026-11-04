using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 通过 `adb exec-out screenrecord --output-format=h264 -` 持续取流。
    /// screenrecord 单次最长 180 秒，到点退出后自动重启，保证长时间可用。
    /// </summary>
    internal sealed class AdbVideoStream : IVideoStream
    {
        private readonly string _adbPath;
        private readonly string _serial;
        private volatile string _size;
        private volatile int _bitRate;
        private readonly int _timeLimitSeconds;
        private volatile bool _stopped;
        private volatile bool _restartRequested;
        private Process _currentProcess;

        public AdbVideoStream(string adbPath, string serial, string size, int bitRate, int timeLimitSeconds = 175)
        {
            _adbPath = adbPath;
            _serial = serial;
            _size = size;
            _bitRate = bitRate;
            _timeLimitSeconds = timeLimitSeconds;
        }

        /// <summary>当前请求的采集尺寸（如 590x1280），可在运行中修改后调用 Restart 生效。</summary>
        public string Size
        {
            get { return _size; }
            set { if (!string.IsNullOrEmpty(value)) _size = value; }
        }

        /// <summary>
        /// 暂停看门狗的“无数据”判定：设备锁屏/息屏时本来就几乎不产帧，
        /// 此时不应反复重连（否则日志刷屏且白白占用编码器）。
        /// </summary>
        private volatile bool _suppressWatchdog;

        /// <summary>暂停看门狗的“无数据”判定（实现 IVideoStream）。</summary>
        public bool SuppressWatchdog
        {
            get { return _suppressWatchdog; }
            set { _suppressWatchdog = value; }
        }

        /// <summary>
        /// 暂停取流：设备锁屏/息屏期间 screenrecord 往往直接退出，
        /// 置为 true 后不再重启片段，等方法结束或设备唤醒后继续。
        /// </summary>
        private volatile bool _paused;

        /// <summary>暂停/恢复取流（实现 IVideoStream）。</summary>
        public bool Paused
        {
            get { return _paused; }
            set { _paused = value; }
        }

        /// <summary>码率（bps），切换分辨率时同步调整，下一次片段生效。</summary>
        public int BitRate
        {
            get { return _bitRate; }
            set { if (value > 0) _bitRate = value; }
        }

        /// <summary>
        /// 强制结束当前片段，Pump 会用新尺寸重建取流（用于横竖屏切换）。
        /// 若此刻恰好没有进程（片段刚结束/尚未启动），也会记下待重启标记，
        /// 由 Pump 立即开新片段，避免改尺寸后要等到下一次自然重连才生效。
        /// </summary>
        public void Restart()
        {
            _restartRequested = true;
            Process process = _currentProcess;
            if (process == null) return;
            try { process.Kill(); } catch { }
        }

        /// <summary>日志回调（在读取线程中触发）。</summary>
        public event Action<string> Log;

        /// <summary>每次重启片段的次数（用于诊断）。</summary>
        public int SegmentCount { get; private set; }

        /// <summary>唤醒屏幕并保持常亮，避免息屏后取不到任何帧。</summary>
        /// <summary>
        /// 取流前准备：清理残留的 screenrecord（上次异常退出可能留下编码进程占着硬件编码器）、
        /// 点亮屏幕、解除锁屏并保持常亮。合并成一条 shell 脚本执行，避免多次 adb 往返拖慢首帧。
        /// </summary>
        public void WakeUpDevice()
        {
            RunShellScript(
                "pkill -f screenrecord",
                "input keyevent 224",
                "wm dismiss-keyguard",
                "svc power stayon true");
            Thread.Sleep(300);
        }

        /// <summary>执行一段设备端 shell 脚本（多条命令用分号连接，单次 adb 调用）。</summary>
        private void RunShellScript(params string[] commands)
        {
            if (commands == null || commands.Length == 0) return;
            RunAdb("shell \"" + string.Join("; ", commands) + "\"");
        }

        /// <summary>关闭窗口时清理设备端残留的 screenrecord 进程。</summary>
        public void ReleaseDevice()
        {
            RunAdb("shell pkill -f screenrecord");
        }

        /// <summary>阻塞读取，直到调用 Stop；每次片段结束会自动重启。</summary>
        public void Pump(Action<byte[], int> onData)
        {
            while (!_stopped)
            {
                // 锁屏/息屏期间不重启片段：避免“取流结束→准备重连”无限循环
                if (_paused)
                {
                    Thread.Sleep(200);
                    continue;
                }

                _restartRequested = false;
                SegmentCount++;
                DateTime segmentStarted = DateTime.UtcNow;
                Process process = null;
                Thread watchdog = null;
                int[] watchdogStop = null;
                try
                {
                    process = StartProcess();
                    _currentProcess = process;
                    Log?.Invoke("取流片段 #" + SegmentCount + " 已启动");
                    Stream output = process.StandardOutput.BaseStream;
                    byte[] buffer = new byte[64 * 1024];
                    long totalBytes = 0;
                    var heartbeat = Stopwatch.StartNew();

                    // 用阻塞 Read，配合独立看门狗线程：ReadAsync + Wait 在重定向管道上
                    // 偶尔会“假死”（数据到了但任务不完成），阻塞读最接近实测可用的原型。
                    var lastDataTick = new long[] { Environment.TickCount };
                    int receivedFlag = 0;
                    Process watched = process;
                    watchdogStop = new int[1];
                    int[] stopFlag = watchdogStop;

                    // 屏幕静止时本来就不产帧：只在“一个字节都没收到”时用较短阈值快速重试，
                    // 收到过数据后给足 60 秒，避免静止画面被反复重连。
                    watchdog = new Thread(() =>
                    {
                        while (!_stopped && Volatile.Read(ref stopFlag[0]) == 0)
                        {
                            Thread.Sleep(200);
                            try
                            {
                                // 进程对象可能已被主线程释放，访问 HasExited 会抛异常，直接退出监视
                                if (watched.HasExited) return;
                            }
                            catch { return; }

                            // 首次启动给足冷启动时间；断流重连时缩短，尽快恢复画面
                            int timeout = Volatile.Read(ref receivedFlag) == 0
                                ? (SegmentCount <= 1 ? 8000 : 4000)
                                : 60000;
                            if (_suppressWatchdog)
                            {
                                // 设备锁屏/息屏期间不判超时，避免无意义的重连
                                Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount);
                                continue;
                            }
                            if (unchecked(Environment.TickCount - Interlocked.Read(ref lastDataTick[0])) > timeout)
                            {
                                Log?.Invoke("取流停滞（" + timeout + "ms 无数据），正在重连…");
                                try { watched.Kill(); } catch { }
                                return;
                            }
                        }
                    }) { IsBackground = true, Name = "stream-watchdog" };
                    watchdog.Start();

                    int read;
                    while (!_stopped && (read = output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount);
                        Interlocked.Exchange(ref receivedFlag, 1);
                        totalBytes += read;
                        onData(buffer, read);

                        if (heartbeat.ElapsedMilliseconds >= 2000)
                        {
                            Log?.Invoke("已接收 " + (totalBytes / 1024) + " KB");
                            heartbeat.Restart();
                        }
                    }

                    // 锁屏/息屏暂停导致的中断不提示“准备重连”
                    if (!_stopped && !_paused)
                    {
                        // 正常码流一秒就有几十 KB；只有几十字节说明 screenrecord 直接报错退出
                        // （尺寸不支持/编码器不可用），明确报“取流失败”供上层降档
                        if (totalBytes < 8192)
                        {
                            Log?.Invoke("取流失败：本片段仅收到 " + totalBytes + " 字节（用时 "
                                + (int)heartbeat.Elapsed.TotalMilliseconds + "ms）");
                        }
                        else
                        {
                            Log?.Invoke("取流结束，准备重连");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopped) Log?.Invoke("取流异常：" + ex.Message);
                }
                finally
                {
                    _currentProcess = null;

                    // 必须先停掉看门狗线程再释放进程对象，否则它在后台访问 HasExited 会抛
                    // InvalidOperationException（没有与此对象关联的进程）
                    if (watchdogStop != null) Interlocked.Exchange(ref watchdogStop[0], 1);
                    if (watchdog != null)
                    {
                        try { watchdog.Join(600); } catch { }
                    }

                    if (process != null)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        try { process.Dispose(); } catch { }
                    }
                }

                if (_stopped) break;
                if (_restartRequested) continue; // 有明确的重新取流请求，立刻重建

                // 片段很快就结束（尺寸不支持时 screenrecord 直接退出）：把间隔拉长，
                // 避免在设备上疯狂重启 screenrecord
                Thread.Sleep((DateTime.UtcNow - segmentStarted).TotalMilliseconds < 1000 ? 1200 : 300);
            }
        }

        private Process StartProcess()
        {
            // 上一个片段被强制结束时，设备端的 screenrecord 可能还占着硬件编码器，
            // 不清掉会让新片段长时间拿不到编码器（表现为旋转后画面迟迟不回来）
            if (SegmentCount > 1)
            {
                RunAdb("shell pkill -f screenrecord");
            }

            string args = string.Format(CultureInfo.InvariantCulture,
                "-s {0} exec-out screenrecord --size {1} --bit-rate {2} --time-limit {3} --output-format=h264 -",
                _serial, _size, _bitRate, _timeLimitSeconds);

            var psi = new ProcessStartInfo(_adbPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Log?.Invoke("adb: " + e.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
            return process;
        }

        private void RunAdb(string adbArgs)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + AdbTools.Globals.QuoteSerial(_serial) + " " + adbArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }

        public void Stop()
        {
            _stopped = true;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
