using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
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
        // 固定子键：不随版本变化，因此系统中只会出现一个“程序和功能”条目
        public const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdbTools";
        // 旧版 Setup Factory 安装留下的显示名，用于迁移清理，避免系统中出现多个条目
        public const string OldProductDisplayName = "AdbTools";
        // 用户配置目录名（位于 %APPDATA% 下，内含 AdbTools.config）
        public const string ConfigFolderName = "AdbTools";
    }

    internal static class InstallEngine
    {
        public static string DefaultInstallPath()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(pf, InstallConsts.ProductName);
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
            Directory.CreateDirectory(path);

            // 升级时先关闭可能正在运行的旧程序，避免文件被占用导致覆盖失败
            TryCloseRunningApps(path);

            // 按“执行项”统计总进度：payload 每个条目 + 2 个快捷方式 + 1 个注册表项
            int entryCount = CountPayloadEntries();
            var tracker = new ProgressTracker(entryCount + 3, progress);

            log("正在解压文件...");
            ExtractPayload(path, () => tracker.Step());

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
        /// 升级时先关闭安装目录下正在运行的旧程序，避免文件被占用导致覆盖失败。
        /// </summary>
        private static void TryCloseRunningApps(string installPath)
        {
            foreach (var name in new[] { "AdbTools", "Update" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.MainModule != null && p.MainModule.FileName.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
                            p.Kill();
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            System.Threading.Thread.Sleep(500);
        }

        private static void ExtractPayload(string path, Action step)
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
                        entry.ExtractToFile(dest, overwrite: true);
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

            // 统计卸载执行项：注册表(1) + 快捷方式(2) + 文件 + 目录 + 配置(可选)
            int fileCount = 0, dirCount = 0;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                try { fileCount = Directory.GetFiles(path).Length; } catch { }
                try { dirCount = Directory.GetDirectories(path).Length; } catch { }
            }
            var tracker = new ProgressTracker(1 + 2 + fileCount + dirCount + (deleteConfig ? 1 : 0), progress);

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
                // 先结束占用安装目录的正在运行程序，尽量让文件可删
                log("正在关闭正在运行的程序...");
                TryCloseRunningApps(path);

                log("正在删除文件...");
                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                        tracker.Step();
                    }
                    foreach (var file in Directory.GetFiles(path))
                    {
                        try { File.Delete(file); } catch { }
                        tracker.Step();
                    }
                }
                catch { }
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

            // uninstall.exe 自身运行时被占用、无法自删：启动独立进程，等本进程退出后重试删除整个安装目录
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    string psExe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    if (!File.Exists(psExe)) psExe = "powershell.exe";

                    string ps =
                        "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" +
                        "$t=$env:ADB_UNINSTALL_TARGET;" +
                        "for($i=0;$i -lt 150;$i++){" +
                        "if(-not (Test-Path -LiteralPath $t)){break};" +
                        "Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue;" +
                        "Start-Sleep -Milliseconds 800}\"";

                    var psi = new ProcessStartInfo(psExe, ps)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        // 关键：工作目录设在临时目录，避免子进程以安装目录为 CWD 而锁住该目录
                        WorkingDirectory = Path.GetTempPath()
                    };
                    psi.EnvironmentVariables["ADB_UNINSTALL_TARGET"] = path;
                    Process.Start(psi);
                }
                catch { }
            }
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
