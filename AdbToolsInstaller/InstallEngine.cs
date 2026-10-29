using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;

namespace AdbToolsInstaller
{
    /// <summary>
    /// 安装/卸载核心逻辑。
    /// 关键点：
    /// 1) 注册表使用固定子键（不含版本号），保证控制面板永远只有一条目；
    /// 2) 升级时读取已安装的 InstallLocation，覆盖到原位置；
    /// 3) 卸载信息写入安装目录下的 uninstall.exe，支持 /uninstall 干净卸载。
    /// </summary>
    internal static class InstallConsts
    {
        public const string ProductName = "Adb连接工具";
        public const string Publisher = "宏尘极客";
        public const string AppExe = "AdbTools.exe";
        public const string UninstallExeName = "uninstall.exe";
        // 安装清单：记录本次安装写入的文件（相对路径），卸载时按清单删除，避免误删目录内的其他程序文件
        public const string ManifestFileName = "install.manifest";
        // 旧版本安装（没有清单文件）卸载时允许删除的已知文件名，除此之外一律不动
        public static readonly string[] LegacyFileNames =
        {
            AppExe,
            "adb.exe",
            "AdbWinApi.dll",
            "AdbWinUsbApi.dll",
            "QRCoder.dll",
            UninstallExeName,
            ManifestFileName
        };
        // 固定子键：不随版本变化，因此系统中只会出现一个“程序和功能”条目
        public const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdbTools";
        // 旧版 Setup Factory 安装留下的显示名，用于迁移清理，避免系统中出现多个条目
        public const string OldProductDisplayName = "AdbTools";
        // 用户配置目录名（位于 %APPDATA% 下，内含 AdbTools.config）
        public const string ConfigFolderName = "AdbTools";
    }

    internal static class InstallEngine
    {
        /// <summary>
        /// 首次安装的默认位置：
        /// 1) 若存在系统盘以外的本地固定磁盘，取盘符顺序最靠前的一个，使用其 Program Files 目录；
        /// 2) 只有系统盘时，使用系统盘的 Program Files 目录。
        /// 最终路径都追加程序目录名。
        /// </summary>
        public static string DefaultInstallPath()
        {
            string extraDrive = FindPreferredExtraDrive();
            string programFiles = string.IsNullOrEmpty(extraDrive)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(extraDrive, "Program Files");

            return Path.Combine(programFiles, InstallConsts.ProductName);
        }

        /// <summary>
        /// 查找系统盘以外、盘符顺序最靠前的本地固定磁盘（如 "D:\"）；没有则返回 null。
        /// </summary>
        private static string FindPreferredExtraDrive()
        {
            string systemRoot;
            try { systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
            catch { systemRoot = @"C:\"; }
            if (string.IsNullOrEmpty(systemRoot)) systemRoot = @"C:\";

            string best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // 仅考虑本地固定磁盘，跳过 U 盘、光驱、网络盘等
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    if (string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || string.Compare(drive.Name, best, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        best = drive.Name;
                    }
                }
                catch { }
            }
            return best;
        }

        /// <summary>
        /// 安装路径安全校验：首次安装禁止直接落到盘符根目录或系统目录本身，必须使用专属子目录；
        /// 升级到历史遗留的不合规位置时仅告警，避免用户无法升级。
        /// </summary>
        private static void EnsureSafeInstallPath(string path, bool upgrading, Action<string> log)
        {
            string normalized = NormalizeDir(path);
            if (string.IsNullOrEmpty(normalized))
            {
                throw new InvalidOperationException("安装目录无效，请重新选择。");
            }
            if (!IsProtectedDirectory(normalized)) return;

            if (upgrading)
            {
                log?.Invoke("提示：当前安装位置为系统目录或磁盘根目录，建议卸载后重装到专属文件夹。");
                return;
            }

            throw new InvalidOperationException(
                "不能直接安装到磁盘根目录或系统目录，请在其下新建专属文件夹后再安装（例如 D:\\Program Files\\"
                + InstallConsts.ProductName + "）。");
        }

        /// <summary>判断是否为不允许直接使用的系统目录（盘符根、Windows、Program Files、Users 等本身）。</summary>
        private static bool IsProtectedDirectory(string path)
        {
            string p = NormalizeDir(path);
            if (string.IsNullOrEmpty(p)) return true;
            if (p.Length <= 2) return true; // 盘符根，如 "C:"

            foreach (var special in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            })
            {
                if (string.IsNullOrEmpty(special)) continue;
                string s = NormalizeDir(special);
                if (!string.IsNullOrEmpty(s) && string.Equals(p, s, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>归一化目录路径：完整路径 + 去掉末尾分隔符（失败返回 null）。</summary>
        private static string NormalizeDir(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        /// <summary>写入安装清单：每行一个相对路径，卸载时据此精确删除。</summary>
        private static void WriteInstallManifest(string installPath, ICollection<string> relativeFiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();

            if (relativeFiles != null)
            {
                foreach (var rel in relativeFiles)
                {
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    string name = rel.Replace('\\', '/').Trim();
                    if (seen.Add(name)) lines.Add(name);
                }
            }
            if (seen.Add(InstallConsts.ManifestFileName)) lines.Add(InstallConsts.ManifestFileName);

            File.WriteAllLines(Path.Combine(installPath, InstallConsts.ManifestFileName), lines, new UTF8Encoding(false));
        }

        /// <summary>
        /// 用户配置目录：%APPDATA%\AdbTools（内含 AdbTools.config）。
        /// </summary>
        public static string ConfigDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                InstallConsts.ConfigFolderName);
        }

        public static string GetInstalledPath()
        {
            using (var key = OpenUninstallKey(false))
            {
                if (key == null) return null;
                var v = key.GetValue("InstallLocation") as string;
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }

        /// <summary>
        /// 已安装版本：优先读取已安装目录下 AdbTools.exe 的文件版本，读不到再回退注册表。
        /// </summary>
        public static string GetInstalledVersion()
        {
            string path = GetInstalledPath();
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    string exe = Path.Combine(path, InstallConsts.AppExe);
                    if (File.Exists(exe))
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exe);
                        string v = string.IsNullOrEmpty(vi.FileVersion) ? vi.ProductVersion : vi.FileVersion;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
            }

            using (var key = OpenUninstallKey(false))
            {
                if (key == null) return null;
                return key.GetValue("DisplayVersion") as string;
            }
        }

        /// <summary>
        /// 读取随包附带的程序版本（从内嵌 payload 里的主程序 exe 读取文件版本），用于界面提示。
        /// </summary>
        public static string GetPackageVersion()
        {
            try
            {
                using (var ms = GetPayloadStream())
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry target = null;
                    foreach (var en in zip.Entries)
                    {
                        if (string.Equals(en.Name, InstallConsts.AppExe, StringComparison.OrdinalIgnoreCase))
                        { target = en; break; }
                    }
                    if (target == null) return null;

                    string tmp = Path.Combine(Path.GetTempPath(), "AdbToolsVer_" + Guid.NewGuid().ToString("N") + ".exe");
                    try
                    {
                        target.ExtractToFile(tmp, true);
                        var vi = FileVersionInfo.GetVersionInfo(tmp);
                        return string.IsNullOrEmpty(vi.FileVersion) ? vi.ProductVersion : vi.FileVersion;
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            catch { return null; }
        }

        private static RegistryKey OpenUninstallKey(bool writable)
        {
            var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return baseKey.OpenSubKey(InstallConsts.UninstallSubKey, writable);
        }

        /// <summary>
        /// 清理旧版（Setup Factory）在“程序和功能”中留下的同名条目，确保系统中只有我们这一条。
        /// </summary>
        public static void CleanupOldEntries()
        {
            var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var uk = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true))
            {
                if (uk == null) return;
                foreach (var name in uk.GetSubKeyNames())
                {
                    using (var sk = uk.OpenSubKey(name, writable: false))
                    {
                        if (sk == null) continue;
                        var dn = sk.GetValue("DisplayName") as string;
                        if (string.Equals(dn, InstallConsts.OldProductDisplayName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { uk.DeleteSubKeyTree(name); } catch { }
                        }
                    }
                }
            }
        }

        public static void Install(string path, Action<string> log, Action<int> progress)
        {
            path = path.Trim().TrimEnd('\\');

            // 目录中已有本程序（主程序或安装清单）视为升级，沿用原位置以免破坏已安装环境
            bool upgrading = File.Exists(Path.Combine(path, InstallConsts.AppExe))
                || File.Exists(Path.Combine(path, InstallConsts.ManifestFileName));
            EnsureSafeInstallPath(path, upgrading, log);
            Directory.CreateDirectory(path);

            // 升级时先解除安装目录内所有 exe 的占用，避免文件被占用导致覆盖失败
            ReleaseFileLocks(path, log);

            // 按“执行项”统计总进度：payload 每个条目 + 2 个快捷方式 + 1 个注册表项
            int entryCount = CountPayloadEntries();
            var tracker = new ProgressTracker(entryCount + 3, progress);

            log("正在解压文件...");
            var installedFiles = new List<string>();
            ExtractPayload(path, installedFiles, () => tracker.Step());

            // 以实际安装后的主程序版本号作为“程序和功能”中显示的版本
            string appExe = Path.Combine(path, InstallConsts.AppExe);
            string version = "1.0.0";
            try { version = FileVersionInfo.GetVersionInfo(appExe).FileVersion ?? version; } catch { }

            // 卸载程序：payload 中已携带独立小体积 uninstall.exe；缺失时退化为复制自身
            string uninstallExe = Path.Combine(path, InstallConsts.UninstallExeName);
            if (!File.Exists(uninstallExe))
            {
                log("正在写入卸载程序...");
                File.Copy(Assembly.GetExecutingAssembly().Location, uninstallExe, true);
            }
            installedFiles.Add(InstallConsts.UninstallExeName);

            // 写入安装清单：卸载时只删除清单内的文件，绝不按目录整体删除
            WriteInstallManifest(path, installedFiles);

            log("正在创建快捷方式...");
            CreateShortcuts(path);
            tracker.Step(2);

            log("正在写入注册表...");
            WriteUninstallEntry(path, version);
            tracker.Step(1);

            // 迁移：移除旧版遗留条目
            CleanupOldEntries();

            progress(100);
            log("安装完成。");
        }

        /// <summary>
        /// 安装/卸载前解除安装目录内所有 exe 文件的占用：
        /// 1) 结束一切从该目录启动的进程（主程序、旧的卸载程序、adb 服务进程、目录内的安装程序等）；
        /// 2) 等待文件句柄真正释放（进程退出后句柄释放可能有延迟），必要时重试。
        /// 当前正在运行的安装/卸载程序自身会被跳过，避免自我结束。
        /// </summary>
        private static void ReleaseFileLocks(string installPath, Action<string> log)
        {
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) return;

            log?.Invoke("正在解除文件占用...");

            int selfId = Process.GetCurrentProcess().Id;
            string selfFile = null;
            try { selfFile = Path.GetFullPath(Assembly.GetExecutingAssembly().Location); } catch { }

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == selfId) continue;

                    string file = GetProcessFilePath(process);
                    if (string.IsNullOrEmpty(file))
                    {
                        // 读不到模块路径（权限/位数差异）时，仅对自家程序按名字兜底
                        if (IsOwnAppProcess(process.ProcessName))
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                        continue;
                    }
                    if (!IsUnderPath(file, installPath)) continue;
                    if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally { process.Dispose(); }
            }

            if (!WaitFileHandlesReleased(installPath, selfFile, TimeSpan.FromSeconds(5)))
            {
                log?.Invoke("仍有文件被占用，解压时会自动重试...");
            }
        }

        /// <summary>是否为 AdbTools 自身的进程名（用于无法读取路径时的兜底结束）。</summary>
        private static bool IsOwnAppProcess(string processName)
        {
            return string.Equals(processName, "AdbTools", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, "Update", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>读取进程可执行文件路径（无权限或进程已退出时返回 null）。</summary>
        private static string GetProcessFilePath(Process process)
        {
            try { return process.MainModule?.FileName; }
            catch { return null; }
        }

        /// <summary>判断文件是否位于指定目录（按目录归一化比较，避免 C:\app 与 C:\app-evil 误判）。</summary>
        private static bool IsUnderPath(string file, string dir)
        {
            try
            {
                string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>轮询等待目录内 exe/dll 的句柄释放，超时后继续（后续解压仍会重试）。</summary>
        private static bool WaitFileHandlesReleased(string installPath, string skipFile, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (AreFilesReleased(installPath, skipFile)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                System.Threading.Thread.Sleep(200);
            }
        }

        /// <summary>检查目录内 exe/dll 是否都已可独占写入（即无人占用）。</summary>
        private static bool AreFilesReleased(string installPath, string skipFile)
        {
            try
            {
                foreach (var pattern in new[] { "*.exe", "*.dll" })
                {
                    foreach (var file in Directory.GetFiles(installPath, pattern, SearchOption.AllDirectories))
                    {
                        // 跳过当前正在运行的安装/卸载程序自身
                        if (!string.IsNullOrEmpty(skipFile)
                            && string.Equals(Path.GetFullPath(file), skipFile, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!CanWriteExclusive(file)) return false;
                    }
                }
            }
            catch { }
            return true;
        }

        /// <summary>尝试以独占方式打开文件，判断是否还有进程占用。</summary>
        private static bool CanWriteExclusive(string file)
        {
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>解压单个条目，遇到占用/权限导致的失败时短暂等待后重试。</summary>
        private static void ExtractEntryWithRetry(ZipArchiveEntry entry, string dest)
        {
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    entry.ExtractToFile(dest, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
        }

        private static void ExtractPayload(string path, ICollection<string> extractedFiles, Action step)
        {
            // 归一化目标根目录，末尾补分隔符，避免前缀误判（如 C:\app 与 C:\app-evil）
            string targetRoot = Path.GetFullPath(path);
            if (!targetRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                targetRoot += Path.DirectorySeparatorChar;

            using (var ms = GetPayloadStream())
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // 防 Zip Slip：解析为完整路径并校验必须位于目标目录内，否则跳过
                    string dest = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
                    if (!dest.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(dest);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        ExtractEntryWithRetry(entry, dest);
                        // 记录相对路径，供写入安装清单使用
                        extractedFiles?.Add(entry.FullName.Replace('\\', '/'));
                    }
                    step?.Invoke();
                }
            }
        }

        /// <summary>
        /// 统计 payload 中的条目数（用于把安装进度按真实执行项划分）。
        /// </summary>
        private static int CountPayloadEntries()
        {
            try
            {
                using (var ms = GetPayloadStream())
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    return zip.Entries.Count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 按“执行项”推进的进度：百分比 = 已完成项 / 总项数。安装与卸载共用同一模型。
        /// </summary>
        private sealed class ProgressTracker
        {
            private readonly Action<int> _report;
            private readonly int _total;
            private int _done;
            private int _last = -1;

            public ProgressTracker(int total, Action<int> report)
            {
                _total = total < 1 ? 1 : total;
                _report = report;
            }

            public void Step(int count = 1)
            {
                _done += count;
                int p = (int)(_done * 100.0 / _total);
                if (p > 100) p = 100;
                if (p != _last)
                {
                    _last = p;
                    _report?.Invoke(p);
                }
            }
        }

        /// <summary>
        /// 读取编译期嵌入的 Payload 资源（payload.zip），返回可随机读的内存流。
        /// </summary>
        private static MemoryStream GetPayloadStream()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var res = asm.GetManifestResourceStream("AdbToolsInstaller.Payload"))
            {
                if (res == null)
                    throw new InvalidDataException("未找到内嵌的 Payload 资源。请重新生成(Rebuild)安装程序项目以嵌入 payload.zip。");
                var ms = new MemoryStream();
                res.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
        }

        private static void CreateShortcuts(string installPath)
        {
            string target = Path.Combine(installPath, InstallConsts.AppExe);
            string icon = target;
            string desc = InstallConsts.ProductName;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                CreateShortcut(Path.Combine(desktop, InstallConsts.ProductName + ".lnk"), target, installPath, desc, icon);
            }
            catch { }

            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string smFolder = Path.Combine(programs, InstallConsts.ProductName);
                Directory.CreateDirectory(smFolder);
                CreateShortcut(Path.Combine(smFolder, InstallConsts.ProductName + ".lnk"), target, installPath, desc, icon);
                string uninstallExe = Path.Combine(installPath, InstallConsts.UninstallExeName);
                CreateShortcut(Path.Combine(smFolder, "卸载 " + InstallConsts.ProductName + ".lnk"), uninstallExe, installPath, "卸载 " + InstallConsts.ProductName, uninstallExe);
            }
            catch { }
        }

        private static void CreateShortcut(string linkPath, string targetPath, string workingDir, string description, string iconPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            var shortcut = shell.CreateShortcut(linkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            shortcut.IconLocation = iconPath + ",0";
            shortcut.Save();
        }

        private static void WriteUninstallEntry(string installPath, string version)
        {
            var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var key = baseKey.CreateSubKey(InstallConsts.UninstallSubKey))
            {
                if (key == null) throw new UnauthorizedAccessException("无法写入注册表，请以管理员身份运行安装程序。");
                string uninstallExe = Path.Combine(installPath, InstallConsts.UninstallExeName);
                key.SetValue("DisplayName", InstallConsts.ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", version, RegistryValueKind.String);
                key.SetValue("Publisher", InstallConsts.Publisher, RegistryValueKind.String);
                key.SetValue("InstallLocation", installPath, RegistryValueKind.String);
                key.SetValue("DisplayIcon", Path.Combine(installPath, InstallConsts.AppExe) + ",0", RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstallExe + "\" /uninstall", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstallExe + "\" /uninstall /silent", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void Uninstall(bool deleteConfig, Action<string> log, Action<int> progress)
        {
            string path = GetInstalledPath();

            // 让当前进程的工作目录离开安装目录，避免 CWD 锁住安装目录导致无法删除
            try { Environment.CurrentDirectory = Path.GetTempPath(); } catch { }

            // 卸载目标：优先使用安装时写入的清单；旧版本无清单时仅处理已知文件名
            List<string> targets = (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                ? BuildUninstallTargets(path, log)
                : new List<string>();

            // 统计卸载执行项：注册表(1) + 快捷方式(2) + 待删除文件 + 配置(可选)
            var tracker = new ProgressTracker(1 + 2 + targets.Count + (deleteConfig ? 1 : 0), progress);

            // 先清理注册表：即使随后文件因占用删不掉，也保证从“程序和功能”中移除
            log("正在清理注册表...");
            // 用 HKLM 根键 + 完整子键路径删除
            try
            {
                var baseKey64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                baseKey64.DeleteSubKeyTree(InstallConsts.UninstallSubKey, false);
            }
            catch { }
            try
            {
                var baseKey32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                baseKey32.DeleteSubKeyTree(InstallConsts.UninstallSubKey, false);
            }
            catch { }
            tracker.Step();

            log("正在移除快捷方式...");
            RemoveShortcuts();
            tracker.Step(2);

            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                if (IsProtectedDirectory(path))
                {
                    // 极端情况：注册表中的安装位置被改成系统目录或盘符根，绝不删除任何文件
                    log("安装位置为系统目录或磁盘根目录，已跳过文件删除，请手动清理残留文件。");
                }
                else
                {
                    // 先解除占用安装目录的进程（主程序、adb 服务等），再按清单精确删除
                    ReleaseFileLocks(path, log);

                    log("正在删除文件...");
                    DeleteInstalledFiles(path, targets, log, tracker);
                }
            }

            if (deleteConfig)
            {
                log("正在删除用户配置...");
                try
                {
                    string cfgDir = ConfigDir();
                    if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, true);
                }
                catch { }
                tracker.Step();
            }

            progress(100);
            log("卸载完成。");

            // 本卸载程序自身运行时无法自删：启动独立进程，等本进程退出后删除自身并移除空目录。
            // 注意：只删除自身文件与空目录（不加 -Recurse），避免在公共目录里误删其他程序的文件。
            if (!string.IsNullOrEmpty(path) && !IsProtectedDirectory(path))
            {
                try
                {
                    string psExe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    if (!File.Exists(psExe)) psExe = "powershell.exe";

                    string ps =
                        "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" +
                        "$t=$env:ADB_UNINSTALL_TARGET;$f=$env:ADB_UNINSTALL_SELF;" +
                        "for($i=0;$i -lt 120;$i++){" +
                        "if(-not (Test-Path -LiteralPath $t)){break};" +
                        "if($f -and (Test-Path -LiteralPath $f)){Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue};" +
                        "Remove-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue;" +
                        "Start-Sleep -Milliseconds 500}\"";

                    var psi = new ProcessStartInfo(psExe, ps)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        // 关键：工作目录设在临时目录，避免子进程以安装目录为 CWD 而锁住该目录
                        WorkingDirectory = Path.GetTempPath()
                    };
                    psi.EnvironmentVariables["ADB_UNINSTALL_TARGET"] = path;
                    psi.EnvironmentVariables["ADB_UNINSTALL_SELF"] = Assembly.GetExecutingAssembly().Location;
                    Process.Start(psi);
                }
                catch { }
            }
        }

        /// <summary>
        /// 按清单删除安装的文件：只删除清单中列出的相对路径（且必须位于安装目录内），
        /// 不递归删除未知内容；目录只有在完全变空后才会被移除。
        /// 删除失败（仍被占用）时重新解除占用并重试，最多 3 轮。
        /// </summary>
        private static void DeleteInstalledFiles(string installPath, List<string> relativeFiles, Action<string> log, ProgressTracker tracker)
        {
            var pending = new List<string>();
            foreach (var rel in relativeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dest = SafeCombine(installPath, rel);
                if (dest != null && File.Exists(dest)) pending.Add(dest);
            }

            const int maxRounds = 3;
            for (int round = 1; round <= maxRounds && pending.Count > 0; round++)
            {
                if (round > 1)
                {
                    // 上一轮仍有文件删不掉：再次结束占用进程并等待句柄释放
                    ReleaseFileLocks(installPath, log);
                }

                bool firstRound = round == 1;
                var still = new List<string>();

                foreach (var file in pending)
                {
                    bool removed = TryDelete(() => File.Delete(file));
                    if (removed) continue;
                    if (File.Exists(file)) still.Add(file);
                }
                if (firstRound) tracker.Step(pending.Count);

                pending = still;
            }

            if (pending.Count > 0)
            {
                log($"有 {pending.Count} 个文件仍被占用，将在程序退出后自动清理。");
            }

            // 自底向上清理空目录：只删除空目录，绝不递归删除内容
            RemoveEmptyDirectories(installPath);
        }

        /// <summary>构造卸载文件清单：优先读取安装清单，缺失时回退到已知文件名（旧版本安装）。</summary>
        private static List<string> BuildUninstallTargets(string installPath, Action<string> log)
        {
            var targets = new List<string>();
            string manifest = Path.Combine(installPath, InstallConsts.ManifestFileName);

            if (File.Exists(manifest))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(manifest))
                    {
                        string rel = line?.Trim();
                        if (!string.IsNullOrEmpty(rel)) targets.Add(rel);
                    }
                }
                catch { }
            }

            if (targets.Count == 0)
            {
                log?.Invoke("未找到安装清单，按已知文件名卸载。");
                targets.AddRange(InstallConsts.LegacyFileNames);
            }

            // 清单自身与卸载程序（可能来自旧版本安装）始终纳入删除范围
            AddTargetIfMissing(targets, InstallConsts.ManifestFileName);
            AddTargetIfMissing(targets, InstallConsts.UninstallExeName);
            return targets;
        }

        private static void AddTargetIfMissing(List<string> targets, string name)
        {
            if (targets.Any(t => string.Equals(t.Replace('\\', '/').Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            targets.Add(name);
        }

        /// <summary>把清单中的相对路径解析为安装目录内的完整路径；越界或非法返回 null。</summary>
        private static string SafeCombine(string installPath, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            try
            {
                string dest = Path.GetFullPath(Path.Combine(installPath, relative.Replace('/', Path.DirectorySeparatorChar)));
                return IsUnderPath(dest, installPath) ? dest : null;
            }
            catch { return null; }
        }

        /// <summary>自底向上删除安装目录下的空子目录（非空目录一律保留）。</summary>
        private static void RemoveEmptyDirectories(string installPath)
        {
            try
            {
                var dirs = Directory.GetDirectories(installPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length)
                    .ToList();

                foreach (var dir in dirs)
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir, false);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>执行删除操作，失败返回 false（不抛出异常）。</summary>
        private static bool TryDelete(Action deleteAction)
        {
            try
            {
                deleteAction();
                return true;
            }
            catch { return false; }
        }

        private static void RemoveShortcuts()
        {
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), InstallConsts.ProductName + ".lnk")); } catch { }
            try
            {
                string smFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstallConsts.ProductName);
                if (Directory.Exists(smFolder)) Directory.Delete(smFolder, true);
            }
            catch { }
        }
    }
}
