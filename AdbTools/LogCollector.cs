using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows;

namespace AdbTools
{
    /// <summary>
    /// 设备日志快捷收集：崩溃日志、诊断包。
    /// 产物统一放在 %APPDATA%\AdbTools\logs\&lt;序列号&gt;\ 下，便于用户直接反馈。
    /// </summary>
    internal static class LogCollector
    {
        /// <summary>命令超时，dumpsys 在负载高的设备上可能较慢。</summary>
        private const int CommandTimeoutMs = 60000;

        /// <summary>
        /// 取设备日志目录（%APPDATA%\AdbTools\DeviceLogs\设备名称），不存在时创建。
        /// 与程序运行日志 logs 分开；用设备名称而不是序列号，避免目录里出现连接串。
        /// </summary>
        public static string GetDeviceLogDirectory(string deviceName)
        {
            string dir = Path.Combine(Globals.DeviceLogsDirectory, Sanitize(deviceName));
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch
            {
                // 目录不可写时由后续写入报错
            }
            return dir;
        }

        /// <summary>抓取 crash 缓冲区并保存为文本文件。</summary>
        public static void CaptureCrashAsync(string adbPath, string serial, string deviceName,
            Action<bool, string, string> completed)
        {
            RunAsync(() =>
            {
                string dir = GetDeviceLogDirectory(deviceName);
                string file = Path.Combine(dir, "crash-" + Timestamp() + ".txt");
                string output = Run(adbPath, serial, "logcat -b crash -d -v threadtime");
                File.WriteAllText(file,
                    BuildHeader(deviceName, serial, "logcat -b crash -d") + output,
                    new UTF8Encoding(true));
                return file;
            }, completed);
        }

        /// <summary>
        /// 抓取诊断包：logcat 全量/崩溃、dropbox 崩溃记录、内存与 CPU、设备信息，
        /// 打包成 zip 返回路径。
        /// </summary>
        public static void CaptureBundleAsync(string adbPath, string serial, string deviceName,
            Action<bool, string, string> completed)
        {
            RunAsync(() =>
            {
                string dir = GetDeviceLogDirectory(deviceName);
                string stamp = Timestamp();
                string work = Path.Combine(dir, "diagnose-" + stamp);
                if (Directory.Exists(work))
                {
                    try { Directory.Delete(work, true); } catch { }
                }
                Directory.CreateDirectory(work);

                WriteFile(work, "logcat_all.txt",
                    Run(adbPath, serial, "logcat -b all -d -v threadtime"));
                WriteFile(work, "logcat_crash.txt",
                    Run(adbPath, serial, "logcat -b crash -d -v threadtime"));
                WriteFile(work, "dropbox_crash.txt",
                    Run(adbPath, serial, "shell \"dumpsys dropbox --print data_app_crash | tail -n 300\""));
                WriteFile(work, "meminfo.txt",
                    Run(adbPath, serial, "shell \"dumpsys meminfo | head -n 150\""));
                WriteFile(work, "cpuinfo.txt",
                    Run(adbPath, serial, "shell dumpsys cpuinfo"));
                WriteFile(work, "device.txt", BuildDeviceInfo(adbPath, serial));
                WriteFile(work, "meta.txt", BuildHeader(deviceName, serial, "诊断包"));

                string zip = Path.Combine(dir, "diagnose-" + stamp + ".zip");
                if (File.Exists(zip))
                {
                    try { File.Delete(zip); } catch { }
                }
                ZipFile.CreateFromDirectory(work, zip, CompressionLevel.Optimal, false);

                // 打包完成后清理临时目录，保留 zip
                try { Directory.Delete(work, true); } catch { }
                return zip;
            }, completed);
        }

        /// <summary>清空设备端指定缓冲区的日志。</summary>
        public static void ClearBuffer(string adbPath, string serial, string buffer)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string args = string.Equals(buffer, "all", StringComparison.OrdinalIgnoreCase)
                    ? "logcat -c"
                    : "logcat -b " + buffer + " -c";
                Run(adbPath, serial, args);
            });
        }

        private static void WriteFile(string directory, string name, string content)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, name), content, new UTF8Encoding(true));
            }
            catch
            {
                // 单个文件失败不影响整个诊断包
            }
        }

        private static string BuildHeader(string deviceName, string serial, string content)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("AdbTools " + App.Version);
            sb.AppendLine("抓取时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("设备名称：" + (deviceName ?? "-"));
            sb.AppendLine("序列号：" + (serial ?? "-"));
            sb.AppendLine("内容：" + content);
            sb.AppendLine(new string('-', 60));
            sb.AppendLine();
            return sb.ToString();
        }

        private static string BuildDeviceInfo(string adbPath, string serial)
        {
            StringBuilder sb = new StringBuilder();
            string[] props =
            {
                "ro.product.brand",
                "ro.product.manufacturer",
                "ro.product.model",
                "ro.build.version.release",
                "ro.build.version.sdk",
                "ro.build.display.id"
            };

            foreach (string prop in props)
            {
                sb.AppendLine(prop + ": " + Run(adbPath, serial, "shell getprop " + prop).Trim());
            }

            sb.AppendLine("wm size: " + Run(adbPath, serial, "shell wm size").Trim());
            sb.AppendLine("wm density: " + Run(adbPath, serial, "shell wm density").Trim());
            return sb.ToString();
        }

        /// <summary>执行一条 adb 命令并返回标准输出（失败时返回错误信息，不抛异常）。</summary>
        private static string Run(string adbPath, string serial, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(adbPath,
                    "-s " + Globals.QuoteSerial(serial) + " " + arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process process = new Process { StartInfo = psi })
                {
                    StringBuilder error = new StringBuilder();
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) error.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginErrorReadLine();
                    string output = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(CommandTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                    }

                    if (string.IsNullOrEmpty(output) && error.Length > 0)
                    {
                        output = error.ToString();
                    }
                    return output ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                return "命令执行失败：" + ex.Message + Environment.NewLine;
            }
        }

        private static void RunAsync(Func<string> work, Action<bool, string, string> completed)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null;
                string error = null;
                try
                {
                    path = work();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    completed?.Invoke(error == null, error, path);
                }));
            });
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            // Windows 目录名不能以点或空格结尾，控制面板里也会显示得很奇怪
            value = value.Trim().TrimEnd('.', ' ');
            if (value.Length == 0) return "unknown";
            if (value.Length > 60) value = value.Substring(0, 60).TrimEnd('.', ' ');
            return value.Length == 0 ? "unknown" : value;
        }
    }
}
