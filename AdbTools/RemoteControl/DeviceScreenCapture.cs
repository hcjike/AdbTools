using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 设备端截屏：走 `screencap`，拿到的是设备当前分辨率下的原始画面（与采集档位无关）。
    /// 主路径为 PNG（设备端压缩），失败时退回 raw 像素在本地编码 PNG，
    /// 避免老版本 adb 在文本通道里把 \n 换成 \r\n 导致 PNG 损坏。
    /// </summary>
    internal static class DeviceScreenCapture
    {
        /// <summary>设备端临时文件目录：shell 可写，且不受 scoped storage 与厂商目录策略影响。</summary>
        private const string RemoteDirectory = "/data/local/tmp/";

        /// <summary>截图结果：失败时 Path 为空，Error 说明原因。</summary>
        internal sealed class Result
        {
            public string Path { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Error { get; set; }

            public bool Success
            {
                get { return !string.IsNullOrEmpty(Path); }
            }

            public static Result Ok(string path, int width, int height)
            {
                return new Result { Path = path, Width = width, Height = height };
            }

            public static Result Fail(string error)
            {
                return new Result { Error = error };
            }
        }

        /// <summary>
        /// 截屏并保存到该设备的产物目录（调用方负责在后台线程执行）。
        /// 优先 PNG，失败再试 raw 像素，两次都失败时返回第一次的错误。
        /// </summary>
        public static Result Capture(string adbPath, string serial, string deviceName)
        {
            Result png = CapturePng(adbPath, serial, deviceName);
            if (png.Success) return png;

            Result raw = CaptureRaw(adbPath, serial, deviceName);
            if (raw.Success) return raw;

            return png;
        }

        /// <summary>主路径：设备端编码 PNG，pull 回本地（二进制通道不会损坏数据）。</summary>
        private static Result CapturePng(string adbPath, string serial, string deviceName)
        {
            string directory = RemoteCaptureStorage.GetScreenshotDirectory(deviceName);
            string token = NewToken();
            string remote = RemoteDirectory + "adbtools_cap_" + token + ".png";
            string temp = Path.Combine(directory, "shot_" + token + ".tmp");

            try
            {
                string output = Run(adbPath, serial, "shell screencap -p " + remote, 20000);
                string pull = Run(adbPath, serial, "pull " + remote + " \"" + temp + "\"", 30000);
                Run(adbPath, serial, "shell rm -f " + remote, 8000);

                if (!File.Exists(temp) || new FileInfo(temp).Length < 64)
                {
                    TryDelete(temp);
                    return Result.Fail("设备端截图失败：" + FirstLine(output + " " + pull));
                }

                int width, height;
                if (!TryReadPngSize(temp, out width, out height))
                {
                    // 典型的 CRLF 破坏：换个通道用 raw 重试
                    TryDelete(temp);
                    return Result.Fail("截图数据不是有效 PNG（adb 通道可能发生了换行转换）");
                }

                string path = RemoteCaptureStorage.BuildScreenshotPath(deviceName, width, height);
                File.Move(temp, path);
                return Result.Ok(path, width, height);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                return Result.Fail(ex.Message);
            }
        }

        /// <summary>
        /// 兜底路径：`screencap` 输出原始像素（12 字节头 + RGBA_8888），
        /// 在本地换成 BGRA 再编码 PNG。大屏设备上比在设备端做 PNG 压缩更快，
        /// 也不会和编码器抢 CPU 影响远程画面。
        /// </summary>
        private static Result CaptureRaw(string adbPath, string serial, string deviceName)
        {
            string directory = RemoteCaptureStorage.GetScreenshotDirectory(deviceName);
            string token = NewToken();
            string remote = RemoteDirectory + "adbtools_cap_" + token + ".raw";
            string temp = Path.Combine(directory, "shot_" + token + ".raw.tmp");

            try
            {
                string output = Run(adbPath, serial, "shell screencap " + remote, 25000);
                string pull = Run(adbPath, serial, "pull " + remote + " \"" + temp + "\"", 40000);
                Run(adbPath, serial, "shell rm -f " + remote, 8000);

                if (!File.Exists(temp) || new FileInfo(temp).Length < 12)
                {
                    TryDelete(temp);
                    return Result.Fail("设备端 raw 截图失败：" + FirstLine(output + " " + pull));
                }

                byte[] data = File.ReadAllBytes(temp);
                TryDelete(temp);

                if (data.Length < 12) return Result.Fail("raw 截图数据过短");

                int width = BitConverter.ToInt32(data, 0);
                int height = BitConverter.ToInt32(data, 4);
                int format = BitConverter.ToInt32(data, 8);

                long needed = 12L + (long)width * height * 4;
                if (width <= 0 || height <= 0 || data.Length < needed)
                {
                    return Result.Fail("raw 截图尺寸异常：" + width + "x" + height
                        + "（format=" + format + "，数据 " + data.Length + " 字节）");
                }

                byte[] bgra = new byte[width * height * 4];
                ConvertRgbaToBgra(data, 12, bgra, width * height);

                var source = BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Bgra32, null, bgra, width * 4);
                source.Freeze();

                string path = RemoteCaptureStorage.BuildScreenshotPath(deviceName, width, height);
                SavePng(source, path);
                return Result.Ok(path, width, height);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                return Result.Fail(ex.Message);
            }
        }

        /// <summary>screencap 的像素格式是 RGBA_8888，WPF 需要 BGRA32，这里顺带把 alpha 拉满。</summary>
        private static unsafe void ConvertRgbaToBgra(byte[] source, int offset, byte[] target, int pixelCount)
        {
            fixed (byte* src = source)
            fixed (byte* dst = target)
            {
                byte* s = src + offset;
                byte* d = dst;
                for (int i = 0; i < pixelCount; i++)
                {
                    d[0] = s[2];
                    d[1] = s[1];
                    d[2] = s[0];
                    d[3] = 255;
                    s += 4;
                    d += 4;
                }
            }
        }

        /// <summary>把当前显示的画面编码成 PNG（设备端截图彻底失败时的最后兜底）。</summary>
        public static string SaveCurrentFrame(BitmapSource source, string deviceName)
        {
            if (source == null) return null;
            try
            {
                string path = RemoteCaptureStorage.BuildScreenshotPath(deviceName,
                    source.PixelWidth, source.PixelHeight);
                SavePng(source, path);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static void SavePng(BitmapSource source, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
        }

        /// <summary>从 PNG 的 IHDR 里读宽高，同时校验签名（避免把损坏的字节当成图片存下来）。</summary>
        private static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var header = new byte[24];
                    if (stream.Read(header, 0, header.Length) < header.Length) return false;

                    byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                    for (int i = 0; i < magic.Length; i++)
                    {
                        if (header[i] != magic[i]) return false;
                    }

                    width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return width > 0 && height > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>执行一条 adb 命令，返回标准输出与错误输出的合并文本（失败不抛异常）。</summary>
        private static string Run(string adbPath, string serial, string arguments, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(adbPath,
                    "-s " + Common.Globals.QuoteSerial(serial) + " " + arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(timeoutMs);
                    return (output ?? string.Empty) + (error ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                return "命令执行失败：" + ex.Message;
            }
        }

        private static string NewToken()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "未知原因";
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : "未知原因";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
