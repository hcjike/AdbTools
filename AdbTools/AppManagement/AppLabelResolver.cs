using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AdbTools
{
    /// <summary>
    /// 后台解析应用名：让设备把 APK 里的 AndroidManifest.xml 与 resources.arsc 吐出来，
    /// 本机解析后回填到列表。整个过程异步、可中断，失败只影响名称显示。
    /// </summary>
    internal sealed class AppLabelResolver
    {
        private const string ManifestMarker = "@@MANIFEST@@";

        /// <summary>连续这么多个应用都取不到名称，就认为设备缺少 unzip/base64，停止解析。</summary>
        private const int MaxConsecutiveFailures = 10;

        /// <summary>资源表取值上限，超过这个大小不再尝试（避免极端 APK 把内存吃穿）。</summary>
        private const long MaxArscBytes = 160L * 1024 * 1024;

        /// <summary>超过这个大小视为大资源表：同一时刻只取一个，避免几个大包并发占满带宽。</summary>
        private const long LargeArscThreshold = 24L * 1024 * 1024;

        /// <summary>保证大资源表串行获取。</summary>
        private static readonly System.Threading.SemaphoreSlim LargeFetchLock =
            new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>单个图标文件的上限，正常图标远小于此值，超出视为异常直接放弃。</summary>
        private const int MaxIconBytes = 4 * 1024 * 1024;

        private readonly string _adbFile;
        private readonly string _serialArg;
        private readonly string _deviceKey;
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _log;
        private readonly int _concurrency;

        private AppInfo[] _items;
        private int _next = -1;
        private int _done;
        private int _failureStreak;
        private volatile bool _stopped;

        /// <summary>单个应用解析完成（label 为空表示解析失败）。回调已在 UI 线程。</summary>
        public event Action<AppInfo, string, int, int> ItemResolved;

        /// <summary>单个应用图标已写入缓存（回调已在 UI 线程）。</summary>
        public event Action<AppInfo> ItemIconResolved;

        /// <summary>解析被提前终止（设备不支持或设备离线）。</summary>
        public event Action<string> Aborted;

        public AppLabelResolver(string adbPath, string serialArg, string deviceKey, Dispatcher dispatcher,
            Action<string> log, int concurrency)
        {
            _adbFile = (adbPath ?? string.Empty).Trim('"');
            _serialArg = serialArg ?? string.Empty;
            _deviceKey = AppDeviceKey.Normalize(deviceKey);
            _dispatcher = dispatcher;
            _log = log;
            _concurrency = concurrency < 1 ? 1 : concurrency;
        }

        public void Start(IList<AppInfo> apps)
        {
            Stop();
            if (apps == null || apps.Count == 0)
            {
                return;
            }

            _items = new AppInfo[apps.Count];
            apps.CopyTo(_items, 0);
            _next = -1;
            _done = 0;
            _failureStreak = 0;
            _stopped = false;

            int total = _items.Length;
            for (int i = 0; i < _concurrency; i++)
            {
                Thread thread = new Thread(() => Worker(total))
                {
                    IsBackground = true,
                    Name = "app-label-" + i
                };
                thread.Start();
            }
        }

        public void Stop()
        {
            _stopped = true;
            _items = null;
        }

        private void Worker(int total)
        {
            while (!_stopped)
            {
                AppInfo[] items = _items;
                if (items == null)
                {
                    return;
                }

                int index = Interlocked.Increment(ref _next);
                if (index >= items.Length)
                {
                    return;
                }

                AppInfo app = items[index];
                string label = null;
                bool transportOk = false;
                bool? isService = null;
                bool nameDetermined = false;
                try
                {
                    label = Resolve(app, out transportOk, out isService, out nameDetermined);
                }
                catch (Exception ex)
                {
                    transportOk = false;
                    if (_log != null) _log("解析应用名异常：" + ex.Message);
                }

                if (_stopped)
                {
                    return;
                }

                if (label != null)
                {
                    AppLabelStore.SetLabel(_deviceKey, app.PackageName, label);
                    Interlocked.Exchange(ref _failureStreak, 0);
                }
                else if (nameDetermined)
                {
                    // 资源齐全仍没名字（部分系统包）：落空标记，下次不再重复拉 arsc
                    AppLabelStore.SetLabelResolved(_deviceKey, app.PackageName);
                    Interlocked.Exchange(ref _failureStreak, 0);
                }
                else if (transportOk)
                {
                    // 链路正常但资源没拿全（如超大 arsc），不算失败，下次再试
                    Interlocked.Exchange(ref _failureStreak, 0);
                }
                else if (Interlocked.Increment(ref _failureStreak) >= MaxConsecutiveFailures)
                {
                    // 设备上没有 unzip/base64 时每个应用都会失败，继续跑没有意义
                    _stopped = true;
                    NotifyAborted("当前设备无法自动解析应用名（缺少 unzip/base64），已改为只显示包名。");
                    return;
                }

                // 系统服务判定结果写缓存：包名唯一，下次直接命中，不用再解析 manifest
                if (isService.HasValue)
                {
                    AppLabelStore.SetService(_deviceKey, app.PackageName, isService.Value);
                }

                int done = Interlocked.Increment(ref _done);
                string result = label;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (nameDetermined) app.NameResolved = true;
                    if (isService.HasValue) app.IsService = isService.Value;
                    Action<AppInfo, string, int, int> handler = ItemResolved;
                    if (handler != null) handler(app, result, done, total);
                }));
            }
        }

        private void NotifyAborted(string message)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Action<string> handler = Aborted;
                if (handler != null) handler(message);
            }));
        }

        private string Resolve(AppInfo app, out bool transportOk, out bool? isService, out bool nameDetermined)
        {
            transportOk = false;
            isService = null;
            nameDetermined = false;
            if (string.IsNullOrEmpty(app.ApkPath))
            {
                return null;
            }

            // 第一步：拿资源表大小 + manifest。manifest 很小，先确认取数链路是通的。
            string command = "unzip -l '" + app.ApkPath + "' resources.arsc; echo " + ManifestMarker
                + "; unzip -p '" + app.ApkPath + "' AndroidManifest.xml | base64";

            string output = RunShell(command, 15000);
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            int marker = output.IndexOf(ManifestMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                return null;
            }

            transportOk = true;

            long arscSize = ParseArscSize(output.Substring(0, marker));
            byte[] manifest = DecodeBase64(output.Substring(marker + ManifestMarker.Length));
            if (manifest == null)
            {
                return null;
            }

            // 第二步：整份取回资源表。用 exec-out 二进制直传，比 base64 少三成数据量和一次解码。
            byte[] arsc = null;
            if (arscSize > 0 && arscSize <= MaxArscBytes)
            {
                bool large = arscSize > LargeArscThreshold;
                if (large) LargeFetchLock.Wait();
                try
                {
                    arsc = RunShellBinary("unzip -p '" + app.ApkPath + "' resources.arsc",
                        large ? 300000 : 60000, (int)Math.Min(MaxArscBytes, arscSize + 65536));
                }
                finally
                {
                    if (large) LargeFetchLock.Release();
                }
            }

            string label = ApkLabelReader.ReadLabel(manifest, arsc);

            // 名称与图标一起处理；图标失败只影响显示，不影响名称
            TryResolveIcon(app, manifest, arsc);

            // 系统服务判定不依赖资源表，manifest 到位即可算
            isService = ApkLabelReader.IsSystemService(manifest, app.IsSystem);

            // 名称已确定：拿到了名字，或 manifest + arsc 都在但确实没名字。
            // arsc 没拿到（大表/缺失）时不判定，留到下次再试。
            nameDetermined = label != null || arsc != null;

            return label;
        }

        /// <summary>
        /// 解析图标：manifest 的 android:icon → 资源表里取文件路径 → 取文件。
        /// 如果是 adaptive-icon（.xml），再解析出 foreground 后重复一次；
        /// 追到底仍是 .xml 说明是矢量图标，本机渲染成 PNG 后缓存。
        /// </summary>
        private void TryResolveIcon(AppInfo app, byte[] manifest, byte[] arsc)
        {
            if (_stopped || arsc == null)
            {
                return;
            }

            if (AppIconStore.HasIcon(_deviceKey, app.PackageName))
            {
                return; // 该设备下已有缓存
            }

            if (AppLabelStore.IsIconFailed(_deviceKey, app.PackageName))
            {
                return; // 之前已尝试且确定拿不到，不再重试
            }

            uint iconId = ApkLabelReader.ReadIconResourceId(manifest);
            if (iconId == 0)
            {
                AppLabelStore.SetIconFailed(_deviceKey, app.PackageName); // 应用没声明图标
                return;
            }

            string path = ApkLabelReader.ResolveFilePath(arsc, iconId);

            // adaptive-icon 只描述"前景 + 背景"，前景层才是真正的图。
            // 顺带记下背景色，渲染纯白前景时铺在底下，避免图标看不见。
            uint backgroundColor = 0;
            bool hasBackgroundColor = false;

            for (int hop = 0; hop < 2 && path != null && IsXml(path); hop++)
            {
                if (_stopped) return;

                byte[] xml = FetchEntry(app.ApkPath, path);
                if (xml == null) break;

                uint foregroundId = ApkLabelReader.ParseAdaptiveIconForeground(xml);
                if (foregroundId == 0) break;

                if (!hasBackgroundColor)
                {
                    hasBackgroundColor = ApkLabelReader.TryReadAdaptiveIconBackgroundColor(
                        xml, arsc, out backgroundColor);
                }

                path = ApkLabelReader.ResolveFilePath(arsc, foregroundId);
            }

            if (path == null || _stopped)
            {
                if (path == null) AppLabelStore.SetIconFailed(_deviceKey, app.PackageName); // 资源表里解析不到图标
                return;
            }

            // 追完仍是 XML：矢量图标（VectorDrawable），渲染后按 PNG 缓存
            if (IsXml(path))
            {
                TrySaveVectorIcon(app, path, arsc, hasBackgroundColor ? (uint?)backgroundColor : null);
                return;
            }

            byte[] image = FetchEntry(app.ApkPath, path);
            if (image == null || _stopped)
            {
                return;
            }

            if (AppIconStore.Save(_deviceKey, app.PackageName, image, Path.GetExtension(path)))
            {
                NotifyIcon(app);
            }
        }

        /// <summary>把矢量图标渲染成 PNG 缓存；解析或渲染失败时保持占位图。</summary>
        private void TrySaveVectorIcon(AppInfo app, string path, byte[] arsc, uint? backgroundColor)
        {
            byte[] xml = FetchEntry(app.ApkPath, path);
            if (xml == null || _stopped)
            {
                return;
            }

            VectorIcon icon = ApkLabelReader.ParseVectorDrawable(xml, arsc);
            if (icon == null)
            {
                AppLabelStore.SetIconFailed(_deviceKey, app.PackageName); // 无法解析的矢量结构
                return;
            }

            byte[] png = VectorIconRenderer.RenderPng(icon, backgroundColor);
            if (png == null)
            {
                if (!_stopped) AppLabelStore.SetIconFailed(_deviceKey, app.PackageName); // 渲染失败
                return;
            }

            if (AppIconStore.Save(_deviceKey, app.PackageName, png, ".png"))
            {
                NotifyIcon(app);
            }
        }

        private byte[] FetchEntry(string apkPath, string entryPath)
        {
            return RunShellBinary("unzip -p '" + apkPath + "' '" + entryPath + "'", 20000, MaxIconBytes);
        }

        private static bool IsXml(string path)
        {
            return path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        }

        private void NotifyIcon(AppInfo app)
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    Action<AppInfo> handler = ItemIconResolved;
                    if (handler != null) handler(app);
                }));
            }
            catch
            {
                // 窗口已关闭
            }
        }

        /// <summary>从 unzip -l 的输出里取 resources.arsc 的原始大小（字节）。</summary>
        private static long ParseArscSize(string listing)
        {
            if (string.IsNullOrEmpty(listing))
            {
                return 0;
            }

            foreach (string raw in listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.IndexOf("resources.arsc", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length && i < 2; i++)
                {
                    long size;
                    if (long.TryParse(parts[i], out size) && size > 0)
                    {
                        return size;
                    }
                }
            }

            return 0;
        }

        /// <summary>用 exec-out 直接取二进制：不经过 cmd.exe，避免输出被转义或截断。</summary>
        private string RunShell(string command, int timeoutMs)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = _adbFile,
                Arguments = "-s " + _serialArg + " exec-out \"sh -c '" + command + "'\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                process.WaitForExit();
                return outputTask.Result;
            }
        }

        /// <summary>
        /// 取二进制内容（如 resources.arsc）。走 exec-out 直传，避免 base64 的三成膨胀，
        /// 读取过程中按上限和超时提前中断，避免异常文件把内存吃穿。
        /// </summary>
        private byte[] RunShellBinary(string command, int timeoutMs, int maxBytes)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = _adbFile,
                Arguments = "-s " + _serialArg + " exec-out \"sh -c '" + command + "'\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();

                // stderr 必须异步读，否则写满管道会卡住
                process.StandardError.ReadToEndAsync();

                MemoryStream buffer = new MemoryStream();
                byte[] chunk = new byte[64 * 1024];
                Stream stream = process.StandardOutput.BaseStream;
                Stopwatch watch = Stopwatch.StartNew();

                try
                {
                    while (true)
                    {
                        int read = stream.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;

                        // 用户点“停止”或超出大小/超时限制时立即中断传输
                        if (_stopped || buffer.Length + read > maxBytes || watch.ElapsedMilliseconds > timeoutMs)
                        {
                            try { process.Kill(); } catch { }
                            return null;
                        }

                        buffer.Write(chunk, 0, read);
                    }
                }
                catch
                {
                    return null;
                }

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                return buffer.Length > 0 ? buffer.ToArray() : null;
            }
        }

        private static byte[] DecodeBase64(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c)) builder.Append(c);
            }

            if (builder.Length == 0)
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(builder.ToString());
            }
            catch
            {
                return null;
            }
        }
    }
}
