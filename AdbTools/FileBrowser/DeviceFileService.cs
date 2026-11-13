using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 设备端文件操作：列目录（解析 <c>ls -al</c>）、上传、下载、新建目录、重命名、删除。
    /// 所有方法都是阻塞调用，必须放在后台线程执行；传输过程可通过 <see cref="Cancel"/> 中断。
    /// </summary>
    internal sealed class DeviceFileService
    {
        private readonly string _adbPath;
        private readonly string _serial;

        /// <summary>当前正在执行的传输进程，取消时直接结束它。</summary>
        private Process _transferProcess;
        private volatile bool _cancelled;

        /// <summary>
        /// 解析 ls -al 的一行：类型 + 权限 + 链接数 + 属主 + 属组 + 大小 + 日期 + 时间 + 名称。
        /// 名称里可能有空格，所以最后一段用贪婪匹配整体取出。
        /// </summary>
        private static readonly Regex ListingLine = new Regex(
            @"^(?<type>[-dlcbps])(?<perm>[rwxsStTlL\-\.\+]{9}\S*)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>[\d,]+)\s+(?<date>\S+)\s+(?<time>\S+)\s+(?<name>.+)$",
            RegexOptions.Compiled);

        public DeviceFileService(string adbPath, string serial)
        {
            _adbPath = (adbPath ?? string.Empty).Trim('"');
            _serial = serial;
        }

        #region 《列目录》

        /// <summary>
        /// 列出目录内容；失败时返回空列表并通过 <paramref name="error"/> 说明原因。
        /// </summary>
        public List<DeviceFileEntry> List(string path, out string error)
        {
            error = null;
            string target = Normalize(path);

            // 结尾加斜杠：/sdcard 这类符号链接只有带斜杠时才会列出目录内容，而不是打印链接本身
            string lsTarget = target == "/" ? "/" : target + "/";

            string output = RunShell("LC_ALL=C ls -al " + Quote(lsTarget), 15000, out error);
            if (output == null)
            {
                if (string.IsNullOrEmpty(error)) error = "读取目录失败";
                return new List<DeviceFileEntry>();
            }

            List<DeviceFileEntry> entries = ParseListing(target, output);

            // 目录里有符号链接时再跑一次 ls -alL：解引用后能判断链接指向目录还是文件，
            // 列表里据此显示「文件夹 + 链接」图标，双击也能直接进入而不必现场再查一次。
            if (entries.Exists(o => o.IsLink))
            {
                string resolveError;
                string resolved = RunShell("LC_ALL=C ls -alL " + Quote(lsTarget), 15000, out resolveError);
                if (resolved != null) ApplyLinkTargets(entries, resolved);
            }

            if (entries.Count == 0)
            {
                if (output.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "没有访问权限";
                else if (output.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "目录不存在或已被删除";
            }
            return entries;
        }

        private static List<DeviceFileEntry> ParseListing(string parent, string output)
        {
            var entries = new List<DeviceFileEntry>();

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("total ", StringComparison.Ordinal)) continue;

                Match match = ListingLine.Match(line);
                if (!match.Success) continue;   // adb 提示、shell 回显等非列表行

                string name = match.Groups["name"].Value;
                bool isLink = match.Groups["type"].Value == "l";
                string linkTarget = null;

                if (isLink)
                {
                    int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow > 0)
                    {
                        linkTarget = name.Substring(arrow + 4);
                        name = name.Substring(0, arrow);
                    }
                }

                // '.' 与 '..' 不展示，返回上级用工具栏按钮
                if (name == "." || name == "..") continue;

                long size = 0;
                string sizeText = match.Groups["size"].Value;
                if (sizeText.IndexOf(',') < 0)
                {
                    long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                }

                entries.Add(new DeviceFileEntry
                {
                    Name = name,
                    FullPath = JoinDevicePath(parent, name),
                    IsDirectory = match.Groups["type"].Value == "d",
                    IsLink = isLink,
                    LinkTarget = linkTarget,
                    Size = size,
                    Modified = ParseTime(match.Groups["date"].Value, match.Groups["time"].Value),
                    Permissions = match.Groups["perm"].Value
                });
            }

            // 文件夹在前，再按名称排序（与资源管理器习惯一致）
            entries.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return entries;
        }

        /// <summary>
        /// 从 <c>ls -alL</c> 的输出里挑出解引用后为目录的名称，回填到对应的符号链接条目上。
        /// 目标不可访问时 -L 仍会显示为 'l' 且带 ' -> '，这类保持"未知"。
        /// </summary>
        private static void ApplyLinkTargets(List<DeviceFileEntry> entries, string resolvedOutput)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var resolvedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (string raw in resolvedOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith("total ", StringComparison.Ordinal)) continue;

                Match match = ListingLine.Match(line);
                if (!match.Success) continue;   // 目标不可访问时是 "?????" 行，解析不出来，保持未知

                string name = match.Groups["name"].Value;
                int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) name = name.Substring(0, arrow);

                string type = match.Groups["type"].Value;
                if (type == "d")
                {
                    directories.Add(name);
                }
                else if (type != "l")
                {
                    // -L 后仍是 'l' 说明目标没解析成功（无权限等），这种保持未知
                    resolvedNames.Add(name);
                }
            }

            foreach (DeviceFileEntry entry in entries)
            {
                if (!entry.IsLink) continue;

                if (directories.Contains(entry.Name)) entry.LinkTargetIsDirectory = true;
                else if (resolvedNames.Contains(entry.Name)) entry.LinkTargetIsDirectory = false;
                else entry.LinkTargetIsDirectory = null;   // 目标无权限等，双击时再现场确认
            }
        }

        /// <summary>ls 的时间有两种形态：<c>2024-05-11 10:22</c> 与 <c>May 11 10:22</c>（跨年时带年份）。</summary>
        private static DateTime ParseTime(string date, string time)
        {
            string text = date + " " + time;
            string[] formats =
            {
                "yyyy-MM-dd HH:mm",
                "yyyy-MM-dd HH:mm:ss",
                "MMM d HH:mm",
                "MMM d yyyy",
                "MMM dd HH:mm",
                "MMM dd yyyy"
            };

            DateTime parsed;
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                // 没写年份的格式按当年处理
                if (parsed.Year == DateTime.MinValue.Year)
                {
                    parsed = new DateTime(DateTime.Now.Year, parsed.Month, parsed.Day,
                        parsed.Hour, parsed.Minute, parsed.Second);
                }
                return parsed;
            }
            return DateTime.MinValue;
        }

        #endregion

        #region 《上传 / 下载》

        /// <summary>
        /// 上传（push）或下载（pull）文件 / 文件夹。adb 自身支持目录递归，这里不需要遍历。
        /// </summary>
        /// <param name="upload">true = 本地到设备，false = 设备到本地</param>
        /// <param name="localPath">本地路径</param>
        /// <param name="devicePath">设备路径</param>
        /// <param name="message">失败原因或取消原因</param>
        public bool Transfer(bool upload, string localPath, string devicePath, out string message)
        {
            message = null;
            _cancelled = false;

            // 注意：push/pull 的参数由 Windows 侧 adb 解析，必须用双引号；
            // 单引号只对设备端 shell 有意义，会变成路径的一部分。
            string args = upload
                ? "push " + HostQuote(localPath) + " " + HostQuote(devicePath)
                : "pull " + HostQuote(devicePath) + " " + HostQuote(localPath);

            try
            {
                using (Process process = new Process { StartInfo = CreateInfo(args) })
                {
                    process.Start();
                    _transferProcess = process;

                    // stderr 必须异步读，否则输出较多时会互相阻塞
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    string errorText = errorTask.Result;

                    _transferProcess = null;

                    if (_cancelled)
                    {
                        message = "已取消";
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        message = PickError(errorText) ?? PickError(output) ?? "传输失败";
                        return false;
                    }

                    // adb 个别情况下退出码为 0 但输出里带错误（例如目标只读）；
                    // 不能用简单的 "error" 判断——传输统计行也会写到 stderr。
                    string errorLine = PickError(errorText);
                    if (errorLine != null)
                    {
                        message = errorLine;
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _transferProcess = null;
                message = ex.Message;
                return false;
            }
        }

        /// <summary>中断正在进行的传输（杀掉 adb 进程，设备端可能残留半个文件）。</summary>
        public void Cancel()
        {
            _cancelled = true;
            Process process = _transferProcess;
            if (process == null) return;

            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // 进程已退出
            }
        }

        #endregion

        #region 《增删改》

        public bool Mkdir(string path, out string message)
        {
            return RunShellOk("mkdir -p " + Quote(path), out message);
        }

        /// <summary>重命名：同一目录下 mv 到新名称。</summary>
        public bool Rename(string path, string newName, out string message)
        {
            message = null;
            if (string.IsNullOrEmpty(newName) || newName.IndexOf('/') >= 0)
            {
                message = "名称不能包含 / 且不能为空";
                return false;
            }

            string parent = ParentPath(path);
            return RunShellOk("mv " + Quote(path) + " " + Quote(JoinDevicePath(parent, newName)), out message);
        }

        public bool Delete(string path, out string message)
        {
            return RunShellOk("rm -rf " + Quote(path), out message);
        }

        /// <summary>
        /// 判断是否是目录（用于决定进入还是下载）。
        /// 用 -L 解引用符号链接：/sdcard 这类链接指向目录时要能进入，而不是当成文件下载。
        /// </summary>
        public bool IsDirectory(string path)
        {
            string error;
            string output = RunShell("LC_ALL=C ls -ldL " + Quote(path), 8000, out error);
            return output != null && output.TrimStart().StartsWith("d", StringComparison.Ordinal);
        }

        #endregion

        #region 《命令执行》

        private bool RunShellOk(string command, out string message)
        {
            string error;
            string output = RunShell(command, 15000, out error);

            if (output == null)
            {
                message = error ?? "命令执行失败";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                message = FirstLine(error);
                return false;
            }

            if (output.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = "没有访问权限";
                return false;
            }
            if (output.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = "文件或目录不存在";
                return false;
            }
            if (output.IndexOf("Read-only file system", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = "该分区只读，无法修改";
                return false;
            }

            message = null;
            return true;
        }

        /// <summary>执行一条设备端 shell 命令（自动包成 adb shell "..."）。</summary>
        private string RunShell(string command, int timeoutMs, out string error)
        {
            error = null;
            try
            {
                string args = "shell \"" + command.Replace("\"", "\\\"") + "\"";
                using (Process process = new Process { StartInfo = CreateInfo(args) })
                {
                    process.Start();

                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        error = "命令执行超时";
                        return null;
                    }

                    string errorText = errorTask.Result;
                    if (!string.IsNullOrWhiteSpace(errorText)) error = errorText.Trim();
                    return output + errorText;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private ProcessStartInfo CreateInfo(string arguments)
        {
            return new ProcessStartInfo(_adbPath, "-s " + Globals.QuoteSerial(_serial) + " " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? null : lines[0].Trim();
        }

        /// <summary>从输出里挑出真正的错误行（adb 的进度统计也会写到 stderr，不能整段当错误）。</summary>
        private static string PickError(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.IndexOf("adb: error", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("failed to", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Read-only file system", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }
            return null;
        }

        /// <summary>Windows 侧 adb 参数用双引号包裹（内部双引号按命令行规则转义）。</summary>
        private static string HostQuote(string path)
        {
            return "\"" + (path ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        #endregion

        #region 《路径工具》

        /// <summary>设备路径用单引号包裹，内部单引号按 shell 规则转义。</summary>
        internal static string Quote(string path)
        {
            return "'" + (path ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/sdcard";
            string value = path.Trim();
            if (value.Length > 1) value = value.TrimEnd('/');
            return value;
        }

        internal static string JoinDevicePath(string parent, string name)
        {
            string basePath = Normalize(parent);
            if (basePath == "/") return "/" + name;
            return basePath + "/" + name;
        }

        /// <summary>取设备的父目录；已在根目录时返回根目录。</summary>
        internal static string ParentPath(string path)
        {
            string value = Normalize(path);
            if (value == "/") return "/";

            int index = value.LastIndexOf('/');
            if (index <= 0) return "/";
            return value.Substring(0, index);
        }

        #endregion
    }
}
