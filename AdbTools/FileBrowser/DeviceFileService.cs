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
    /// <summary>同名冲突的处理方式。</summary>
    internal enum ConflictAction
    {
        /// <summary>覆盖目标。</summary>
        Overwrite,

        /// <summary>跳过，不传输。</summary>
        Skip,

        /// <summary>保留两者：自动生成不重名的新名字。</summary>
        KeepBoth
    }

    /// <summary>单个条目的传输结果。</summary>
    internal enum TransferOutcome
    {
        /// <summary>新增（目标原本没有同名项）。</summary>
        Success,

        /// <summary>覆盖了目标处的同名项。</summary>
        Overwritten,

        /// <summary>保留两者：目标没被覆盖，另存为不重名的新名字。</summary>
        KeptBoth,

        Skipped,
        Failed
    }

    /// <summary>
    /// 一个待传输条目。上传时 <see cref="LocalPath"/> 是源、<see cref="DevicePath"/> 是目标；下载时相反。
    /// </summary>
    internal sealed class TransferEntry
    {
        public bool Upload { get; set; }
        public string LocalPath { get; set; }
        public string DevicePath { get; set; }
        public bool IsDirectory { get; set; }
    }

    /// <summary>冲突弹窗需要展示的信息（源与目标两侧的大小、时间）。</summary>
    internal sealed class ConflictInfo
    {
        public bool Upload { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public long SourceSize { get; set; }
        public DateTime SourceModified { get; set; }
        public long TargetSize { get; set; }
        public DateTime TargetModified { get; set; }

        /// <summary>目标处的同名项是目录（类型不一致，只能跳过或保留两者）。</summary>
        public bool TargetIsDirectory { get; set; }
    }

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
        /// 以某个应用的身份访问其私有目录（run-as）。为空表示普通 shell。
        /// 设置后所有命令都会加上 run-as 前缀，只能访问该应用自己的数据目录。
        /// </summary>
        public string RunAsPackage { get; set; }

        /// <summary>
        /// 传输进度回调（后台线程调用，调用方自己切回 UI 线程）。
        /// 目前用于应用私有目录的整目录下载：逐文件取回，需要让用户看到进度。
        /// </summary>
        public Action<string> Progress { get; set; }

        /// <summary>
        /// 同名冲突决策回调（后台线程调用，调用方自己切回 UI 线程）。
        /// 未设置时按覆盖处理。
        /// </summary>
        public Func<ConflictInfo, ConflictAction> ResolveConflict { get; set; }

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

            string output = RunShell("LC_ALL=C " + AsRunAs("ls -al " + Quote(lsTarget)), 15000, out error);
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
                string resolved = RunShell("LC_ALL=C " + AsRunAs("ls -alL " + Quote(lsTarget)), 15000, out resolveError);
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

            // 取消标志由 BeginBatch() 统一重置：这里若重置，逐文件/逐应用循环里
            // 每次调用都会把用户刚发出的取消请求抹掉，取消要等到整批结束才生效。
            if (_cancelled)
            {
                message = "已取消";
                return false;
            }

            // 应用私有目录不能走 adb push/pull（那是 adbd 的身份）：
            // 读用 exec-out 以应用身份 cat，写先 push 到 /data/local/tmp 再 run-as cp 进目录。
            if (!string.IsNullOrEmpty(RunAsPackage))
            {
                return upload
                    ? UploadAsRunAs(localPath, devicePath, out message)
                    : DownloadAsRunAs(devicePath, localPath, out message);
            }

            return upload
                ? PushRaw(localPath, devicePath, out message)
                : PullRaw(devicePath, localPath, out message);
        }

        /// <summary>直接调用 adb push（本地 → 设备），输出只用来判断成败。</summary>
        private bool PushRaw(string localPath, string devicePath, out string message)
        {
            // adb push 不创建空目录，空文件夹单独 mkdir 落地（run-as 场景由调用方处理）
            if (string.IsNullOrEmpty(RunAsPackage)
                && Directory.Exists(localPath) && IsEmptyLocalDirectory(localPath))
            {
                return Mkdir(devicePath, out message);
            }

            // 注意：push/pull 的参数由 Windows 侧 adb 解析，必须用双引号；
            // 单引号只对设备端 shell 有意义，会变成路径的一部分。
            return RunTransferProcess("push " + HostQuote(localPath) + " " + HostQuote(devicePath), out message);
        }

        /// <summary>本地是否为存在且内容为空的目录。</summary>
        private static bool IsEmptyLocalDirectory(string path)
        {
            try
            {
                foreach (string unused in Directory.EnumerateFileSystemEntries(path))
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 下载（pull）。Windows 侧 adb 无法创建含逗号的本地目录层（会报 "Not a directory"），
        /// 所以先拉到不含逗号的临时路径；若是目录则再移动/合并到最终目标。
        /// </summary>
        private bool PullRaw(string devicePath, string localPath, out string message)
        {
            message = null;

            string trimmed = (localPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)) parent = Path.GetTempPath();

            string tempRoot = Path.Combine(parent, ".adbtools_tmp_" + Guid.NewGuid().ToString("N"));

            try
            {
                bool isDirectory = IsDirectory(devicePath);

                if (isDirectory)
                {
                    // 目录里有含逗号的名字时，adb 建不出对应的本地目录层
                    // （会报 Not a directory，或静默丢层），直接走逐文件拉取。
                    bool useTreePull = HasCommaInTree(devicePath);

                    if (!useTreePull)
                    {
                        // 整目录拉取一次（大多数名字不含逗号，一次完成最快）。
                        // 源路径加 "/." 表示只拉内容，避免 adb 用「源目录名」额外建一层。
                        string pullSource = devicePath.TrimEnd('/') + "/.";
                        bool fastOk = RunTransferProcess(
                            "pull " + HostQuote(pullSource) + " " + HostQuote(tempRoot), out message)
                            && Directory.Exists(tempRoot);

                        if (!fastOk)
                        {
                            // 意外失败也退回逐文件拉取，避免因个别名字再次失败
                            useTreePull = !_cancelled;
                        }
                    }

                    if (useTreePull)
                    {
                        // 本地目录由我们自己创建（与 Android Studio 的做法一致）
                        TryDeleteDirectory(tempRoot);
                        message = null;
                        if (!PullTree(devicePath, tempRoot, out message))
                        {
                            TryDeleteDirectory(tempRoot);
                            return false;
                        }
                    }
                    else if (_cancelled)
                    {
                        return false;
                    }

                    if (File.Exists(trimmed))
                    {
                        // 目标处是文件，挡住目录创建；文件可覆盖
                        try
                        {
                            File.Delete(trimmed);
                        }
                        catch (Exception ex)
                        {
                            message = "本地已存在同名文件，且无法删除：" + ex.Message;
                            return false;
                        }
                    }

                    if (Directory.Exists(trimmed))
                    {
                        // 目标文件夹已存在：合并覆盖，绝不删除原文件夹
                        MergeDirectory(tempRoot, trimmed);
                    }
                    else
                    {
                        Directory.Move(tempRoot, trimmed);
                    }
                    return true;
                }

                if (!RunTransferProcess("pull " + HostQuote(devicePath) + " " + HostQuote(tempRoot), out message))
                {
                    return false;
                }

                if (File.Exists(tempRoot))
                {
                    if (Directory.Exists(trimmed))
                    {
                        message = "目标位置已存在同名文件夹，无法写入同名文件";
                        return false;
                    }
                    if (File.Exists(trimmed)) File.Delete(trimmed);
                    File.Move(tempRoot, trimmed);
                    return true;
                }

                message = "下载失败：没有取回任何内容";
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
                TryDeleteFile(tempRoot);
            }
        }

        /// <summary>
        /// 目录树里是否有名字带逗号的文件/文件夹。Windows 版 adb 无法创建含逗号的本地目录层，
        /// 这种目录不能整目录 pull，必须逐文件拉取。find 不可用时按「没有」处理，走快速路径。
        /// </summary>
        private bool HasCommaInTree(string deviceDir)
        {
            string error;
            string output = RunShell("cd " + Quote(deviceDir) + " && find . -print", 20000, out error);
            if (string.IsNullOrEmpty(output)) return false;

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length <= 2) continue;   // 只有 "." 本身
                if (line.IndexOf(',') >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 逐文件递归拉取（与 Android Studio 一致）：设备目录逐层列出，
        /// 本地目录由 .NET 自己创建，从而绕开 Windows adb 无法创建含逗号目录层的问题。
        /// </summary>
        private bool PullTree(string deviceDir, string localDir, out string message)
        {
            message = null;

            try
            {
                Directory.CreateDirectory(localDir);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }

            string listError;
            List<DeviceFileEntry> entries = List(deviceDir, out listError);
            if (entries.Count == 0 && !string.IsNullOrEmpty(listError))
            {
                message = FirstLine(listError);
                return false;
            }

            foreach (DeviceFileEntry entry in entries)
            {
                if (_cancelled)
                {
                    message = "已取消";
                    return false;
                }

                string childLocal = Path.Combine(localDir, entry.Name);

                if (entry.IsDirectory)
                {
                    if (!PullTree(entry.FullPath, childLocal, out message)) return false;
                    continue;
                }

                string pullMessage;
                if (!RunTransferProcess("pull " + HostQuote(entry.FullPath) + " " + HostQuote(childLocal),
                        out pullMessage))
                {
                    message = string.IsNullOrWhiteSpace(pullMessage)
                        ? "下载失败：" + entry.Name
                        : FirstLine(pullMessage);
                    return false;
                }
            }

            return true;
        }

        /// <summary>递归把 source 的内容合并到 destination：同名文件覆盖，其它保留。</summary>
        private static void MergeDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string dir in Directory.GetDirectories(source))
            {
                MergeDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
            }
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响下载结果
            }
        }

        /// <summary>执行一次 adb push / pull，并判断是否成功。</summary>
        private bool RunTransferProcess(string args, out string message)
        {
            message = null;
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

        /// <summary>
        /// 以应用身份下载：文件直接 cat，目录则逐层遍历、逐个文件取回。
        /// run-as 无法用 adb pull（那是 adbd 的身份），所以目录必须自己走。
        /// </summary>
        private bool DownloadAsRunAs(string devicePath, string localPath, out string message)
        {
            if (IsDirectory(devicePath))
            {
                return DownloadFolderAsRunAs(devicePath, localPath, 0, out message);
            }
            return DownloadFileAsRunAs(devicePath, localPath, out message);
        }

        /// <summary>
        /// 递归下载应用私有目录：列目录 → 建本地目录 → 逐文件 exec-out 取回。
        /// 符号链接指向的目录直接跳过，避免自我循环；层级过深也会停下。
        /// </summary>
        private bool DownloadFolderAsRunAs(string deviceDir, string localDir, int depth, out string message)
        {
            message = null;

            if (_cancelled)
            {
                message = "已取消";
                return false;
            }

            if (depth > 24)
            {
                message = "目录层级过深，已停止";
                return false;
            }

            try
            {
                Directory.CreateDirectory(localDir);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }

            string listError;
            List<DeviceFileEntry> entries = List(deviceDir, out listError);
            if (entries.Count == 0 && !string.IsNullOrEmpty(listError))
            {
                message = listError;
                return false;
            }

            int done = 0;
            int failed = 0;
            string firstError = null;

            foreach (DeviceFileEntry entry in entries)
            {
                if (_cancelled)
                {
                    message = "已取消";
                    return false;
                }

                string localTarget = Path.Combine(localDir, entry.Name);

                if (entry.IsDirectory)
                {
                    if (entry.IsLink) continue;   // 指向目录的链接不跟进，防止循环

                    string childError;
                    if (!DownloadFolderAsRunAs(entry.FullPath, localTarget, depth + 1, out childError))
                    {
                        if (_cancelled) { message = "已取消"; return false; }
                        failed++;
                        if (firstError == null) firstError = entry.Name + "：" + childError;
                    }
                    continue;
                }

                string fileError;
                if (!DownloadFileAsRunAs(entry.FullPath, localTarget, out fileError))
                {
                    if (_cancelled) { message = "已取消"; return false; }
                    failed++;
                    if (firstError == null) firstError = entry.Name + "：" + fileError;
                }
                else
                {
                    done++;
                    if (done % 5 == 0) NotifyProgress("已下载 " + done + " 个文件…");
                }
            }

            if (failed > 0)
            {
                message = "部分文件下载失败（" + failed + " 个）：" + firstError;
                return false;
            }

            NotifyProgress("已下载 " + done + " 个文件");
            return true;
        }

        private void NotifyProgress(string text)
        {
            Action<string> callback = Progress;
            if (callback == null) return;
            try { callback(text); } catch { }
        }

        /// <summary>
        /// 以应用身份下载单个文件：<c>adb exec-out run-as pkg cat 文件</c> 直接流式写入本地。
        /// 必须用二进制流，不能走文本解码（数据库、图片会损坏）。
        /// </summary>
        private bool DownloadFileAsRunAs(string devicePath, string localPath, out string message)
        {
            message = null;
            string command = "run-as " + RunAsPackage + " cat " + Quote(devicePath);

            try
            {
                using (Process process = new Process { StartInfo = CreateInfo("exec-out " + HostQuote(command)) })
                {
                    process.Start();
                    _transferProcess = process;

                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    try
                    {
                        using (FileStream stream = File.Create(localPath))
                        {
                            process.StandardOutput.BaseStream.CopyTo(stream);
                        }
                    }
                    catch (Exception ex)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        _transferProcess = null;
                        message = ex.Message;
                        return false;
                    }

                    process.WaitForExit();
                    string errorText = errorTask.Result;
                    _transferProcess = null;

                    if (_cancelled)
                    {
                        TryDeleteFile(localPath);
                        message = "已取消";
                        return false;
                    }

                    string errorLine = PickError(errorText);
                    if (process.ExitCode != 0 || errorLine != null)
                    {
                        TryDeleteFile(localPath);
                        message = errorLine ?? FirstLine(errorText) ?? "下载失败（该文件可能无法以应用身份读取）";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _transferProcess = null;
                TryDeleteFile(localPath);
                message = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 以应用身份上传：先 push 到 /data/local/tmp（shell 可写），
        /// 再用 run-as cp 复制进应用目录，最后删掉临时副本。
        /// 这样落地文件的属主是该应用自己，应用能正常读写。
        /// </summary>
        private bool UploadAsRunAs(string localPath, string devicePath, out string message)
        {
            message = null;

            // 空目录无法 push，直接以应用身份创建
            if (Directory.Exists(localPath) && IsEmptyLocalDirectory(localPath))
            {
                return Mkdir(devicePath, out message);
            }

            string temp = "/data/local/tmp/adbtools_" + Guid.NewGuid().ToString("N");
            if (!PushRaw(localPath, temp, out message))
            {
                return false;
            }

            string copyError;
            RunShell(AsRunAs("cp -r " + Quote(temp) + " " + Quote(devicePath)), 60000, out copyError);

            // 临时副本必须清掉，否则 /data/local/tmp 里会越积越多
            RunShell("rm -rf " + Quote(temp), 10000, out _);

            if (!string.IsNullOrWhiteSpace(copyError))
            {
                string line = FirstLine(copyError);
                message = line.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "没有访问权限（该目录不在应用自己的数据目录内）"
                    : line;
                return false;
            }
            return true;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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
            return RunShellOk(AsRunAs("mkdir -p " + Quote(path)), out message);
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
            return RunShellOk(AsRunAs("mv " + Quote(path) + " " + Quote(JoinDevicePath(parent, newName))), out message);
        }

        public bool Delete(string path, out string message)
        {
            return RunShellOk(AsRunAs("rm -rf " + Quote(path)), out message);
        }

        /// <summary>
        /// 判断是否是目录（用于决定进入还是下载）。
        /// 用 -L 解引用符号链接：/sdcard 这类链接指向目录时要能进入，而不是当成文件下载。
        /// </summary>
        public bool IsDirectory(string path)
        {
            string error;
            string output = RunShell("LC_ALL=C " + AsRunAs("ls -ldL " + Quote(path)), 8000, out error);
            return output != null && output.TrimStart().StartsWith("d", StringComparison.Ordinal);
        }

        #endregion

        #region 《应用私有数据（run-as）》

        /// <summary>给命令加上 run-as 前缀；不在应用模式时原样返回。</summary>
        private string AsRunAs(string command)
        {
            if (string.IsNullOrEmpty(RunAsPackage)) return command;
            return "run-as " + RunAsPackage + " " + command;
        }

        /// <summary>
        /// 列出第三方（用户安装）应用包名。<c>pm list packages -3</c> 只返回用户安装的应用，
        /// 系统预装应用（/system、/product 等）不在结果里；界面上要把这一点明确告诉用户。
        /// </summary>
        public List<string> ListThirdPartyPackages(out string error)
        {
            var packages = new List<string>();
            string output = RunShell("pm list packages -3", 20000, out error);
            if (output == null)
            {
                if (string.IsNullOrEmpty(error)) error = "读取应用列表失败";
                return packages;
            }

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;

                string name = line.Substring("package:".Length).Trim();
                if (name.Length > 0) packages.Add(name);
            }
            packages.Sort(StringComparer.OrdinalIgnoreCase);
            return packages;
        }

        /// <summary>
        /// 当前 adb shell 是否已经是 root 身份（adb root 过的设备、部分模拟器）。
        /// 这种设备上所有应用目录都能直接读，不需要 run-as。
        /// </summary>
        public bool IsShellRoot()
        {
            string error;
            string output = RunShell("id", 5000, out error);
            return output != null && output.IndexOf("uid=0(", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 读取 包名 → APK 路径映射（<c>pm list packages -f</c>）。
        /// 解析应用名与图标需要 APK 路径，输出形如 <c>package:/data/app/~~xx/base.apk=com.foo</c>。
        /// </summary>
        public Dictionary<string, string> ListPackageApkPaths(out string error)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string output = RunShell("pm list packages -f", 20000, out error);
            if (output == null)
            {
                if (string.IsNullOrEmpty(error)) error = "读取 APK 路径失败";
                return map;
            }

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;

                int separator = line.LastIndexOf('=');
                if (separator <= "package:".Length) continue;

                string path = line.Substring("package:".Length, separator - "package:".Length);
                string package = line.Substring(separator + 1).Trim();
                if (path.Length > 0 && package.Length > 0)
                {
                    map[package] = path;
                }
            }
            return map;
        }

        /// <summary>
        /// 一次性探测一批应用能否用 run-as 访问：在设备端跑一个 for 循环，
        /// 每个包执行一次 <c>run-as pkg id</c>，有 uid 输出即为可访问。
        /// 比“每个包起一个 adb 进程”快得多，也没有多线程并发拉起 adb 的问题。
        /// 返回 包名 → uid 行；不可访问的包不会出现在结果里。
        /// </summary>
        public Dictionary<string, string> ProbeRunAsBatch(IList<string> packages, out string error)
        {
            error = null;
            var accessible = new Dictionary<string, string>(StringComparer.Ordinal);
            if (packages == null || packages.Count == 0) return accessible;

            // 分批执行，避免单条命令过长（adb shell 参数有长度上限）
            const int batchSize = 40;
            for (int start = 0; start < packages.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, packages.Count);
                var batch = new List<string>();
                for (int i = start; i < end; i++) batch.Add(packages[i]);

                string command = "for p in " + string.Join(" ", batch)
                    + "; do u=$(run-as $p id 2>/dev/null); echo \"$p|$u\"; done";

                string batchError;
                string output = RunShell(command, 30000, out batchError);
                if (output == null)
                {
                    if (string.IsNullOrEmpty(error)) error = batchError;
                    continue;
                }

                foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator = raw.IndexOf('|');
                    if (separator <= 0) continue;

                    string package = raw.Substring(0, separator).Trim();
                    string value = raw.Substring(separator + 1).Trim();

                    // 不可访问时 run-as 会把错误写到 stderr（已被 2>/dev/null 丢掉），$u 为空
                    if (package.Length > 0 && value.StartsWith("uid=", StringComparison.Ordinal))
                    {
                        accessible[package] = value;
                    }
                }
            }
            return accessible;
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

        #region 《逐文件传输（扫描 + 冲突询问 + 进度）》

        /// <summary>设备端列表按目录缓存：逐文件传输时不能每个文件都执行一次 ls。</summary>
        private readonly Dictionary<string, Dictionary<string, DeviceFileEntry>> _listingCache =
            new Dictionary<string, Dictionary<string, DeviceFileEntry>>(StringComparer.Ordinal);

        /// <summary>应用私有目录只能整目录 cp -r，无法逐文件询问冲突，仍走原逻辑。</summary>
        public bool IsRunAsMode
        {
            get { return !string.IsNullOrEmpty(RunAsPackage); }
        }

        /// <summary>一批传输开始前重置状态与目录缓存。</summary>
        public void BeginBatch()
        {
            _cancelled = false;
            _listingCache.Clear();
        }

        /// <summary>
        /// 扫描出这一批要处理的全部条目（目录 + 文件）。
        /// 上传遍历本地，下载用一次 find 取回设备上的所有相对路径。
        /// </summary>
        public List<TransferEntry> Scan(bool upload, string localPath, string devicePath, out string error)
        {
            error = null;
            List<TransferEntry> entries = upload
                ? ScanLocal(localPath, devicePath, out error)
                : ScanDevice(devicePath, localPath, out error);

            foreach (TransferEntry entry in entries)
            {
                entry.Upload = upload;
            }
            return entries;
        }

        private static List<TransferEntry> ScanLocal(string localRoot, string deviceRoot, out string error)
        {
            error = null;
            var entries = new List<TransferEntry>();

            if (File.Exists(localRoot))
            {
                entries.Add(new TransferEntry
                {
                    LocalPath = localRoot,
                    DevicePath = deviceRoot,
                    IsDirectory = false
                });
                return entries;
            }

            if (!Directory.Exists(localRoot))
            {
                error = "本地路径不存在或已被移动";
                return entries;
            }

            entries.Add(new TransferEntry
            {
                LocalPath = localRoot,
                DevicePath = deviceRoot,
                IsDirectory = true
            });
            AddLocalChildren(localRoot, deviceRoot, entries);
            return entries;
        }

        private static void AddLocalChildren(string localDir, string deviceDir, List<TransferEntry> entries)
        {
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(localDir);
                dirs = Directory.GetDirectories(localDir);
            }
            catch (Exception)
            {
                // 无权限等异常目录直接跳过，不影响其它内容
                return;
            }

            foreach (string file in files)
            {
                entries.Add(new TransferEntry
                {
                    LocalPath = file,
                    DevicePath = JoinDevicePath(deviceDir, Path.GetFileName(file)),
                    IsDirectory = false
                });
            }

            foreach (string dir in dirs)
            {
                string childDevice = JoinDevicePath(deviceDir, Path.GetFileName(dir));
                entries.Add(new TransferEntry
                {
                    LocalPath = dir,
                    DevicePath = childDevice,
                    IsDirectory = true
                });
                AddLocalChildren(dir, childDevice, entries);
            }
        }

        private List<TransferEntry> ScanDevice(string deviceRoot, string localRoot, out string error)
        {
            error = null;
            var entries = new List<TransferEntry>();

            string root = Normalize(deviceRoot);
            if (!IsDirectory(root))
            {
                // 单文件：直接下发
                entries.Add(new TransferEntry
                {
                    LocalPath = localRoot,
                    DevicePath = root,
                    IsDirectory = false
                });
                return entries;
            }

            entries.Add(new TransferEntry
            {
                LocalPath = localRoot,
                DevicePath = root,
                IsDirectory = true
            });

            // 一次 find 拿全部路径，比逐目录 ls 快得多；老设备没有 find 时退回递归列目录
            string dirError;
            string dirOutput = RunShell("cd " + Quote(root) + " && find . -type d -print", 120000, out dirError);
            string fileError;
            string fileOutput = RunShell("cd " + Quote(root) + " && find . ! -type d -print", 120000, out fileError);

            // find 不可用的设备会返回 "xxx: find: not found" 这类文本，按有无 "./" 开头的结果判断
            if (!HasFindOutput(dirOutput) && !HasFindOutput(fileOutput))
            {
                ScanDeviceByList(root, localRoot, entries);
                return entries;
            }

            AppendFound(entries, dirOutput, root, localRoot, true);
            AppendFound(entries, fileOutput, root, localRoot, false);
            return entries;
        }

        private static bool HasFindOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.TrimStart().StartsWith("./", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AppendFound(List<TransferEntry> entries, string output,
            string deviceRoot, string localRoot, bool isDirectory)
        {
            if (string.IsNullOrEmpty(output)) return;

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();

                // 只接受 find 输出的相对路径，shell 报错行一律忽略
                if (!line.StartsWith("./", StringComparison.Ordinal)) continue;

                string relative = line.Substring(2).TrimEnd('/');
                if (relative.Length == 0) continue;                                 // "." 即根目录本身

                entries.Add(new TransferEntry
                {
                    DevicePath = JoinDevicePath(deviceRoot, relative),
                    LocalPath = Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                    IsDirectory = isDirectory
                });
            }
        }

        private void ScanDeviceByList(string deviceDir, string localDir, List<TransferEntry> entries)
        {
            string error;
            List<DeviceFileEntry> list = List(deviceDir, out error);

            foreach (DeviceFileEntry item in list)
            {
                string childLocal = Path.Combine(localDir, item.Name);

                if (item.IsDirectory)
                {
                    entries.Add(new TransferEntry
                    {
                        DevicePath = item.FullPath,
                        LocalPath = childLocal,
                        IsDirectory = true
                    });
                    ScanDeviceByList(item.FullPath, childLocal, entries);
                }
                else
                {
                    entries.Add(new TransferEntry
                    {
                        DevicePath = item.FullPath,
                        LocalPath = childLocal,
                        IsDirectory = false
                    });
                }
            }
        }

        /// <summary>传一个条目：目录只建目录，文件逐个人工处理冲突。</summary>
        public TransferOutcome TransferFile(TransferEntry entry, out string message)
        {
            message = null;
            try
            {
                return entry.Upload ? PushEntry(entry, out message) : PullEntry(entry, out message);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return TransferOutcome.Failed;
            }
        }

        private TransferOutcome PushEntry(TransferEntry entry, out string message)
        {
            message = null;

            if (entry.IsDirectory)
            {
                return MkdirRaw(entry.DevicePath, out message)
                    ? TransferOutcome.Success
                    : TransferOutcome.Failed;
            }

            if (_cancelled)
            {
                message = "已取消";
                return TransferOutcome.Failed;
            }

            bool replaced = false;
            bool keptBoth = false;
            DeviceFileEntry existing = FindDeviceEntry(entry.DevicePath);
            if (existing != null)
            {
                ConflictAction action = AskConflict(new ConflictInfo
                {
                    Upload = true,
                    SourcePath = entry.LocalPath,
                    TargetPath = entry.DevicePath,
                    SourceSize = SafeFileSize(entry.LocalPath),
                    SourceModified = SafeFileTime(entry.LocalPath) ?? DateTime.MinValue,
                    TargetSize = existing.Size,
                    TargetModified = existing.Modified,
                    TargetIsDirectory = existing.IsDirectory
                });

                if (action == ConflictAction.Skip) return TransferOutcome.Skipped;

                if (action == ConflictAction.KeepBoth)
                {
                    keptBoth = true;
                    entry.DevicePath = UniqueDevicePath(entry.DevicePath);
                }
                else
                {
                    replaced = true;
                    if (existing.IsDirectory)
                    {
                        // 类型不同时选择替换：先删掉同名目录再写文件
                        if (!Delete(entry.DevicePath, out message)) return TransferOutcome.Failed;
                    }
                }
            }

            if (!RunTransferProcess("push " + HostQuote(entry.LocalPath) + " " + HostQuote(entry.DevicePath),
                    out message))
            {
                return TransferOutcome.Failed;
            }

            CacheAdd(entry.DevicePath, false, SafeFileSize(entry.LocalPath), SafeFileTime(entry.LocalPath));
            if (keptBoth) return TransferOutcome.KeptBoth;
            return replaced ? TransferOutcome.Overwritten : TransferOutcome.Success;
        }

        private TransferOutcome PullEntry(TransferEntry entry, out string message)
        {
            message = null;

            if (entry.IsDirectory)
            {
                try
                {
                    Directory.CreateDirectory(entry.LocalPath);
                    return TransferOutcome.Success;
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return TransferOutcome.Failed;
                }
            }

            if (_cancelled)
            {
                message = "已取消";
                return TransferOutcome.Failed;
            }

            string parent = Path.GetDirectoryName(entry.LocalPath);
            if (!string.IsNullOrEmpty(parent))
            {
                try
                {
                    Directory.CreateDirectory(parent);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return TransferOutcome.Failed;
                }
            }

            bool replaced = false;
            bool keptBoth = false;
            bool targetIsDirectory = Directory.Exists(entry.LocalPath);
            bool targetIsFile = File.Exists(entry.LocalPath);

            if (targetIsDirectory || targetIsFile)
            {
                DeviceFileEntry source = FindDeviceEntry(entry.DevicePath);
                ConflictAction action = AskConflict(new ConflictInfo
                {
                    Upload = false,
                    SourcePath = entry.DevicePath,
                    TargetPath = entry.LocalPath,
                    SourceSize = source != null ? source.Size : 0,
                    SourceModified = source != null ? source.Modified : DateTime.MinValue,
                    TargetSize = targetIsFile ? SafeFileSize(entry.LocalPath) : 0,
                    TargetModified = targetIsFile ? (SafeFileTime(entry.LocalPath) ?? DateTime.MinValue) : DateTime.MinValue,
                    TargetIsDirectory = targetIsDirectory
                });

                if (action == ConflictAction.Skip) return TransferOutcome.Skipped;

                if (action == ConflictAction.KeepBoth)
                {
                    keptBoth = true;
                    entry.LocalPath = UniqueLocalPath(entry.LocalPath);
                }
                else
                {
                    replaced = true;
                    if (targetIsDirectory)
                    {
                        // 类型不同时选择替换：先删掉同名目录再写文件
                        try
                        {
                            Directory.Delete(entry.LocalPath, true);
                        }
                        catch (Exception ex)
                        {
                            message = "同名文件夹无法删除：" + ex.Message;
                            return TransferOutcome.Failed;
                        }
                    }
                }
            }

            if (!RunTransferProcess("pull " + HostQuote(entry.DevicePath) + " " + HostQuote(entry.LocalPath),
                    out message))
            {
                return TransferOutcome.Failed;
            }

            if (keptBoth) return TransferOutcome.KeptBoth;
            return replaced ? TransferOutcome.Overwritten : TransferOutcome.Success;
        }

        private ConflictAction AskConflict(ConflictInfo info)
        {
            Func<ConflictInfo, ConflictAction> resolver = ResolveConflict;
            return resolver != null ? resolver(info) : ConflictAction.Overwrite;
        }

        private bool MkdirRaw(string path, out string message)
        {
            return RunShellOk("mkdir -p " + Quote(path), out message);
        }

        private static long SafeFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static DateTime? SafeFileTime(string path)
        {
            try
            {
                return new FileInfo(path).LastWriteTime;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DeviceName(string devicePath)
        {
            string value = Normalize(devicePath);
            int index = value.LastIndexOf('/');
            return index < 0 ? value : value.Substring(index + 1);
        }

        private Dictionary<string, DeviceFileEntry> GetListing(string dir)
        {
            Dictionary<string, DeviceFileEntry> map;
            if (_listingCache.TryGetValue(dir, out map)) return map;

            string error;
            List<DeviceFileEntry> list = List(dir, out error);

            map = new Dictionary<string, DeviceFileEntry>(StringComparer.Ordinal);
            foreach (DeviceFileEntry item in list)
            {
                if (!string.IsNullOrEmpty(item.Name) && !map.ContainsKey(item.Name)) map[item.Name] = item;
            }

            _listingCache[dir] = map;
            return map;
        }

        /// <summary>查设备上的同名项；目录列表按目录缓存，一次 ls 复用到底。</summary>
        private DeviceFileEntry FindDeviceEntry(string devicePath)
        {
            string name = DeviceName(devicePath);
            if (string.IsNullOrEmpty(name)) return null;

            Dictionary<string, DeviceFileEntry> map = GetListing(ParentPath(devicePath));
            DeviceFileEntry entry;
            return map.TryGetValue(name, out entry) ? entry : null;
        }

        private void CacheAdd(string devicePath, bool isDirectory, long size, DateTime? modified)
        {
            string name = DeviceName(devicePath);
            if (string.IsNullOrEmpty(name)) return;

            Dictionary<string, DeviceFileEntry> map = GetListing(ParentPath(devicePath));
            map[name] = new DeviceFileEntry
            {
                Name = name,
                FullPath = devicePath,
                IsDirectory = isDirectory,
                Size = size,
                Modified = modified ?? DateTime.MinValue
            };
        }

        /// <summary>生成设备上的不重名路径：name (1).ext / name (2).ext …</summary>
        private string UniqueDevicePath(string devicePath)
        {
            string parent = ParentPath(devicePath);
            string name = DeviceName(devicePath);
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);

            for (int i = 1; i < 1000; i++)
            {
                string candidate = JoinDevicePath(parent, baseName + " (" + i + ")" + extension);
                if (FindDeviceEntry(candidate) == null) return candidate;
            }

            return JoinDevicePath(parent,
                baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")" + extension);
        }

        /// <summary>生成本地的不重名路径：name (1).ext / name (2).ext …</summary>
        private static string UniqueLocalPath(string localPath)
        {
            string directory = Path.GetDirectoryName(localPath) ?? string.Empty;
            string name = Path.GetFileName(localPath);
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);

            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(directory, baseName + " (" + i + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }

            return Path.Combine(directory,
                baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")" + extension);
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
