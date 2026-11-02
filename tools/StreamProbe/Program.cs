using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace StreamProbe
{
    /// <summary>
    /// M0 验证工具：用 `adb exec-out screenrecord --output-format=h264 -` 取 H.264 裸流，
    /// 统计帧率、码率、关键帧数量与首帧到达时间，判断设备是否满足实时远程控制的前置条件。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("参数错误：" + ex.Message);
                Options.PrintUsage();
                return 1;
            }
            if (options == null)
            {
                Options.PrintUsage();
                return 0;
            }

            Console.WriteLine("adb      : " + options.AdbPath);
            Console.WriteLine("序列号   : " + (string.IsNullOrEmpty(options.Serial) ? "(默认设备)" : options.Serial));
            Console.WriteLine("请求参数 : " + options.Size + ", " + options.BitRate + " bps, " + options.Seconds + " s");

            string sdk = RunAdb(options.AdbPath, options.Serial, "shell getprop ro.build.version.sdk");
            string wmSize = RunAdb(options.AdbPath, options.Serial, "shell wm size");
            Console.WriteLine("系统 SDK : " + sdk);
            Console.WriteLine("屏幕尺寸 : " + OneLine(wmSize));

            int sdkInt;
            if (int.TryParse(sdk, out sdkInt) && sdkInt < 24)
            {
                Console.WriteLine("警告：Android 7.0 以下对 screenrecord 输出到 stdout 支持不稳定。");
            }
            Console.WriteLine();

            string arguments = BuildArgs(options);
            Console.WriteLine("执行     : " + options.AdbPath + " " + arguments);
            Console.WriteLine();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo(options.AdbPath, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (stderr) stderr.AppendLine(e.Data);
            };

            var splitter = new NalSplitter();
            FileStream outFile = null;
            if (!string.IsNullOrEmpty(options.OutPath))
            {
                outFile = new FileStream(options.OutPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                Console.WriteLine("原始码流 : " + options.OutPath);
                Console.WriteLine();
            }

            var clock = Stopwatch.StartNew();
            var buffer = new byte[64 * 1024];
            long totalBytes = 0;
            long windowBytes = 0;
            int lastFrames = 0;
            double windowStart = 0;
            bool forcedStop = false;

            try
            {
                process.Start();
                process.BeginErrorReadLine();

                Stream stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytes += read;
                    windowBytes += read;
                    splitter.Feed(buffer, read);
                    if (outFile != null) outFile.Write(buffer, 0, read);

                    double elapsed = clock.Elapsed.TotalSeconds;
                    if (elapsed - windowStart >= 1.0)
                    {
                        int framesInWindow = splitter.Frames - lastFrames;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[{0,4:F0}s] {1,7:F0} KB/s  帧 {2,4} ({3,5:F1} fps)  关键帧 {4}",
                            elapsed,
                            windowBytes / 1024.0 / (elapsed - windowStart),
                            framesInWindow,
                            framesInWindow / (elapsed - windowStart),
                            splitter.KeyFrames));

                        lastFrames = splitter.Frames;
                        windowBytes = 0;
                        windowStart = elapsed;
                    }

                    if (elapsed > options.Seconds + 10)
                    {
                        forcedStop = true;
                        try { process.Kill(); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("读取视频流失败：" + ex.Message);
            }
            finally
            {
                if (outFile != null) outFile.Dispose();
                try { if (!process.HasExited) process.WaitForExit(3000); } catch { }
                try { process.Dispose(); } catch { }
            }

            double seconds = clock.Elapsed.TotalSeconds;
            double avgFps = splitter.Frames / Math.Max(seconds, 0.001);
            double mbps = totalBytes * 8.0 / 1024.0 / 1024.0 / Math.Max(seconds, 0.001);

            Console.WriteLine();
            Console.WriteLine("===== 结果 =====");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "总时长   : {0:F1} s", seconds));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "总数据   : {0:F0} KB ({1:F2} Mbps)", totalBytes / 1024.0, mbps));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "帧数     : {0}  平均帧率 {1:F1} fps", splitter.Frames, avgFps));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "关键帧   : {0}  SPS {1}  PPS {2}", splitter.KeyFrames, splitter.SpsCount, splitter.PpsCount));
            if (splitter.FirstKeyFrameMs >= 0)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "首帧到达 : {0:F0} ms", splitter.FirstKeyFrameMs));
            }
            if (forcedStop)
            {
                Console.WriteLine("注意     : 达兜底超时后强制结束，screenrecord 可能未遵守 --time-limit");
            }
            lock (stderr)
            {
                string err = stderr.ToString().Trim();
                if (err.Length > 0) Console.WriteLine("stderr   : " + OneLine(err));
            }

            Console.WriteLine();
            if (splitter.Frames == 0)
            {
                Console.WriteLine("结论：未取到有效视频帧，该设备/系统对 `--output-format=h264 -` 支持不足，需要回退方案。");
                return 2;
            }
            if (avgFps < 15)
            {
                Console.WriteLine("结论：帧率偏低，建议降低 --size / --bit-rate 后复测，或检查 WiFi 带宽。");
                return 3;
            }
            Console.WriteLine("结论：帧率达标，可以进入 M1（Media Foundation 解码 + WPF 显示）实现。");
            return 0;
        }

        private static string BuildArgs(Options options)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(options.Serial)) sb.Append("-s ").Append(options.Serial).Append(' ');
            sb.Append("exec-out screenrecord");
            sb.Append(" --size ").Append(options.Size);
            sb.Append(" --bit-rate ").Append(options.BitRate.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --time-limit ").Append(options.Seconds.ToString(CultureInfo.InvariantCulture));
            sb.Append(" --output-format=h264 -");
            return sb.ToString();
        }

        private static string OneLine(string text)
        {
            return (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string RunAdb(string adb, string serial, string adbArgs)
        {
            try
            {
                string args = (string.IsNullOrEmpty(serial) ? string.Empty : "-s " + serial + " ") + adbArgs;
                var psi = new ProcessStartInfo(adb, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } }
                    return output.Trim();
                }
            }
            catch (Exception ex)
            {
                return "(查询失败: " + ex.Message + ")";
            }
        }

        /// <summary>命令行参数。</summary>
        private sealed class Options
        {
            public string AdbPath = "adb";
            public string Serial;
            public int Seconds = 20;
            public string Size = "720x1280";
            public int BitRate = 4000000;
            public string OutPath;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i].ToLowerInvariant();
                    switch (key)
                    {
                        case "-h":
                        case "--help":
                            return null;
                        case "--adb":
                            o.AdbPath = Require(args, ref i);
                            break;
                        case "--serial":
                            o.Serial = Require(args, ref i);
                            break;
                        case "--seconds":
                            o.Seconds = int.Parse(Require(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--size":
                            o.Size = Require(args, ref i);
                            break;
                        case "--bit-rate":
                            o.BitRate = int.Parse(Require(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--out":
                            o.OutPath = Require(args, ref i);
                            break;
                        default:
                            throw new ArgumentException("未知参数 " + args[i]);
                    }
                }
                return o;
            }

            private static string Require(string[] args, ref int i)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("参数 " + args[i] + " 缺少取值");
                return args[++i];
            }

            public static void PrintUsage()
            {
                Console.WriteLine("用法: StreamProbe [--adb <adb.exe>] [--serial <序列号>] [--seconds 20]");
                Console.WriteLine("                  [--size 720x1280] [--bit-rate 4000000] [--out sample.h264]");
                Console.WriteLine("说明: 通过 adb exec-out screenrecord 取 H.264 裸流，统计帧率/码率/首帧延迟。");
            }
        }

        /// <summary>Annex-B 码流拆分器：统计 SPS/PPS/关键帧/普通帧。</summary>
        private sealed class NalSplitter
        {
            private byte[] _buffer = new byte[256 * 1024];
            private int _length;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public int Frames { get; private set; }
            public int KeyFrames { get; private set; }
            public int SpsCount { get; private set; }
            public int PpsCount { get; private set; }
            public double FirstKeyFrameMs { get; private set; } = -1;

            public void Feed(byte[] data, int count)
            {
                EnsureCapacity(_length + count);
                Buffer.BlockCopy(data, 0, _buffer, _length, count);
                _length += count;
                Parse();
            }

            private void EnsureCapacity(int needed)
            {
                if (needed <= _buffer.Length) return;
                int size = _buffer.Length;
                while (size < needed) size *= 2;
                Array.Resize(ref _buffer, size);
            }

            private void Parse()
            {
                int consumed = 0;
                while (true)
                {
                    int start = FindStartCode(consumed);
                    if (start < 0)
                    {
                        // 没有起始码：保留尾部 2 字节，避免起始码被截断
                        consumed = Math.Max(consumed, _length - 2);
                        break;
                    }

                    int next = FindStartCode(start + 3);
                    if (next < 0)
                    {
                        consumed = start;
                        break;
                    }

                    HandleNal(start + 3, next - (start + 3));
                    consumed = next;
                }

                if (consumed > 0)
                {
                    _length -= consumed;
                    if (_length > 0) Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _length);
                }
            }

            private int FindStartCode(int from)
            {
                for (int i = Math.Max(from, 0); i + 2 < _length; i++)
                {
                    if (_buffer[i] == 0 && _buffer[i + 1] == 0 && _buffer[i + 2] == 1) return i;
                }
                return -1;
            }

            private void HandleNal(int offset, int length)
            {
                if (length <= 0) return;
                int type = _buffer[offset] & 0x1F;
                switch (type)
                {
                    case 1:
                        Frames++;
                        break;
                    case 5:
                        Frames++;
                        KeyFrames++;
                        if (FirstKeyFrameMs < 0) FirstKeyFrameMs = _clock.Elapsed.TotalMilliseconds;
                        break;
                    case 7:
                        SpsCount++;
                        break;
                    case 8:
                        PpsCount++;
                        break;
                }
            }
        }
    }
}
