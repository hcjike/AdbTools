using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RemoteProbe
{
    /// <summary>
    /// 通过 `adb exec-out screenrecord --output-format=h264 -` 持续取流。
    /// screenrecord 单次最长 180 秒，到点退出后自动重启，保证长时间可用。
    /// </summary>
    internal sealed class AdbVideoStream : IDisposable
    {
        private readonly string _adbPath;
        private readonly string _serial;
        private readonly string _size;
        private readonly int _bitRate;
        private readonly int _timeLimitSeconds;
        private volatile bool _stopped;

        public AdbVideoStream(string adbPath, string serial, string size, int bitRate, int timeLimitSeconds = 175)
        {
            _adbPath = adbPath;
            _serial = serial;
            _size = size;
            _bitRate = bitRate;
            _timeLimitSeconds = timeLimitSeconds;
        }

        /// <summary>日志回调（在读取线程中触发）。</summary>
        public event Action<string> Log;

        /// <summary>每次重启片段的次数（用于诊断）。</summary>
        public int SegmentCount { get; private set; }

        /// <summary>唤醒屏幕并保持常亮，避免息屏后取不到任何帧。</summary>
        public void WakeUpDevice()
        {
            RunAdb("shell input keyevent 224");
            RunAdb("shell svc power stayon true");
        }

        /// <summary>阻塞读取，直到调用 Stop；每次片段结束会自动重启。</summary>
        public void Pump(Action<byte[], int> onData)
        {
            while (!_stopped)
            {
                SegmentCount++;
                Process process = null;
                try
                {
                    process = StartProcess();
                    Log?.Invoke("取流片段 #" + SegmentCount + " 已启动");
                    Stream output = process.StandardOutput.BaseStream;
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while (!_stopped && (read = output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        onData(buffer, read);
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopped) Log?.Invoke("取流异常：" + ex.Message);
                }
                finally
                {
                    if (process != null)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        try { process.Dispose(); } catch { }
                    }
                }

                if (_stopped) break;
                Thread.Sleep(300); // 片段间短暂停顿，避免 adb 端 socket 未释放
            }
        }

        private Process StartProcess()
        {
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
