using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;

namespace AdbTools
{
    /// <summary>
    /// 全局配置与公共工具
    /// </summary>
    public static class Globals
    {
        /// <summary>
        /// 判断源字符串是否包含任意一个子串
        /// </summary>
        public static bool ContainsAny(string source, params string[] substrings)
        {
            return substrings.Any(substring => source.Contains(substring));
        }

        /// <summary>常见 adb 安装错误的可读说明；未命中的错误保持原文。</summary>
        private static readonly Dictionary<string, string> InstallErrorMessages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "INSTALL_FAILED_ALREADY_EXISTS", "应用已存在，当前未启用覆盖安装，请先卸载后再安装" },
                { "INSTALL_FAILED_DUPLICATE_PACKAGE", "设备已存在相同包名的应用" },
                { "INSTALL_FAILED_INVALID_APK", "APK 文件无效或已损坏" },
                { "INSTALL_FAILED_INVALID_URI", "APK 路径无效，无法读取安装包" },
                { "INSTALL_FAILED_INSUFFICIENT_STORAGE", "设备存储空间不足" },
                { "INSTALL_FAILED_NO_MATCHING_ABIS", "设备 CPU 架构不支持该 APK" },
                { "INSTALL_FAILED_CPU_ABI_INCOMPATIBLE", "设备 CPU 架构不支持该 APK" },
                { "INSTALL_FAILED_UPDATE_INCOMPATIBLE", "同包名应用签名不一致，请先卸载原应用再安装" },
                { "INSTALL_FAILED_SHARED_USER_INCOMPATIBLE", "共享用户签名不匹配，无法安装" },
                { "INSTALL_FAILED_PERMISSION_MODEL_DOWNGRADE", "应用权限模型降级，无法覆盖安装，请先卸载原应用" },
                { "INSTALL_FAILED_VERSION_DOWNGRADE", "安装版本低于设备已有版本，无法覆盖安装" },
                { "INSTALL_FAILED_OLDER_SDK", "设备系统版本过低，不支持该应用" },
                { "INSTALL_FAILED_NEWER_SDK", "应用与当前系统版本不兼容" },
                { "INSTALL_FAILED_TEST_ONLY", "这是测试包，需要以测试模式安装或使用正式签名包" },
                { "INSTALL_FAILED_ABORTED", "安装被设备或用户取消" },
                { "INSTALL_FAILED_USER_RESTRICTED", "设备限制了安装，请在设备设置中允许通过 USB / 网络调试安装应用" },
                { "INSTALL_FAILED_VERIFICATION_FAILURE", "设备应用验证失败，请检查设备安全设置或安装包来源" },
                { "INSTALL_FAILED_VERIFICATION_TIMEOUT", "设备验证安装超时，请检查设备状态后重试" },
                { "INSTALL_FAILED_MISSING_SHARED_LIBRARY", "应用依赖的共享库缺失" },
                { "INSTALL_FAILED_MISSING_FEATURE", "设备缺少应用要求的功能" },
                { "INSTALL_FAILED_CONFLICTING_PROVIDER", "应用声明的 Provider 与设备已有应用冲突" },
                { "INSTALL_FAILED_PACKAGE_CHANGED", "应用包名与设备已有版本不一致" },
                { "INSTALL_FAILED_UID_CHANGED", "同包名应用 UID 变化，请先卸载原应用" },
                { "INSTALL_FAILED_DEXOPT", "设备优化应用失败，通常是空间不足或系统异常" },
                { "INSTALL_FAILED_CONTAINER_ERROR", "设备无法写入应用存储位置" },
                { "INSTALL_FAILED_INVALID_INSTALL_LOCATION", "安装位置无效" },
                { "INSTALL_FAILED_MEDIA_UNAVAILABLE", "安装存储不可用" },
                { "INSTALL_FAILED_INTERNAL_ERROR", "设备内部错误导致安装失败" },
                { "INSTALL_FAILED_NO_SIGNATURE", "APK 未签名或签名信息无效" },
                { "INSTALL_PARSE_FAILED_NO_CERTIFICATES", "APK 未签名或签名信息无效" },
                { "INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES", "APK 签名与设备已有应用不一致" },
                { "INSTALL_PARSE_FAILED_MANIFEST_MALFORMED", "APK 的 AndroidManifest 格式异常" },
                { "INSTALL_PARSE_FAILED_NOT_APK", "文件不是有效的 APK" }
            };

        /// <summary>
        /// 把 adb 安装错误压成一句可读说明；已知错误给出中文解释并保留错误码，未知错误显示原文。
        /// </summary>
        public static string DescribeInstallError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "未知错误";

            string upper = error.ToUpperInvariant();

            if (upper.Contains("DEVICE OFFLINE") || upper.Contains("ERROR: CLOSED"))
            {
                return "设备已断开或未响应，请重新连接后重试";
            }
            if (upper.Contains("命令执行超时"))
            {
                return "安装命令超时，设备端可能仍在处理，请稍后检查应用列表";
            }
            if (upper.Contains("PERMISSION DENIED"))
            {
                return "设备拒绝访问，请检查 USB / WiFi 调试授权或应用安装限制";
            }

            string code = ExtractInstallErrorCode(error);
            if (!string.IsNullOrEmpty(code))
            {
                string message;
                if (InstallErrorMessages.TryGetValue(code, out message))
                {
                    return message + "（" + code + "）";
                }
            }

            foreach (KeyValuePair<string, string> pair in InstallErrorMessages)
            {
                if (upper.Contains(pair.Key))
                {
                    return pair.Value + "（" + pair.Key + "）";
                }
            }

            return FirstLine(error);
        }

        private static string ExtractInstallErrorCode(string error)
        {
            const string marker = "Failure [";
            int index = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;

            int start = index + marker.Length;
            int end = error.IndexOf(']', start);
            if (end <= start) end = error.Length;

            string inner = error.Substring(start, end - start).Trim();
            int split = inner.IndexOfAny(new[] { ' ', ':', '\r', '\n' });
            return split > 0 ? inner.Substring(0, split).Trim() : inner;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "未知错误";

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "未知错误" : lines[0].Trim();
        }

        /// <summary>
        /// 把设备序列号包裹成 adb -s 参数可用的形式：含空格（如 mDNS/TLS 连接名
        /// `adb-xxxx._adb-tls-connect._tcp`）时用双引号括起，避免命令被空格截断；
        /// 序列号内若含双引号则转义为 ""（cmd 规则）。
        /// </summary>
        public static string QuoteSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return "\"\"";
            return "\"" + serial.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 校验是否为合法的设备连接地址（IP、IP:端口，或 mDNS 服务名）
        /// </summary>
        public static bool IsValidIPEndPoint(string ipPort)
        {
            if (string.IsNullOrEmpty(ipPort))
            {
                return false;
            }
            if (ipPort.Contains("._adb-tls-connect._tcp"))
            {
                // mDNS 广播出的服务名，如 adb-XXXX._adb-tls-connect._tcp
                return true;
            }

            string[] parts = ipPort.Split(':');
            if (parts.Length > 2)
            {
                return false;
            }
            if (parts.Length == 1)
            {
                // 仅 IP，使用默认端口
                return IPAddress.TryParse(ipPort, out _);
            }

            return IPAddress.TryParse(parts[0], out _)
                && int.TryParse(parts[1], out int port)
                && port > 0 && port <= 65535;
        }

        /// <summary>
        /// 程序在用户 Roaming 目录下的数据目录（%APPDATA%\AdbTools）。
        /// </summary>
        public static string AppDataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AdbTools");
            }
        }

        /// <summary>
        /// 日志目录（%APPDATA%\AdbTools\logs），不存在时自动创建。
        /// </summary>
        public static string LogDirectory
        {
            get
            {
                string dir = Path.Combine(AppDataDirectory, "logs");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 目录不可写时交给调用方处理（写日志本身失败不应影响功能）
                }
                return dir;
            }
        }

        /// <summary>
        /// 拼接日志文件的完整路径。
        /// </summary>
        public static string GetLogFilePath(string fileName)
        {
            return Path.Combine(LogDirectory, fileName);
        }

        /// <summary>
        /// 设备日志导出目录（%APPDATA%\AdbTools\DeviceLogs），与应用运行日志 logs 分开。
        /// 内部按设备名称再分子目录。
        /// </summary>
        public static string DeviceLogsDirectory
        {
            get
            {
                string dir = Path.Combine(AppDataDirectory, "DeviceLogs");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 目录不可写时由调用方处理
                }
                return dir;
            }
        }

        /// <summary>
        /// 缓存目录（%APPDATA%\AdbTools\cache）：运行期中间文件、更新安装包、兜底日志等可随时丢弃的数据。
        /// 启动时由 CleanupCacheDirectory 清空，保证不在系统临时目录留东西。
        /// </summary>
        public static string CacheDirectory
        {
            get
            {
                string dir = Path.Combine(AppDataDirectory, "cache");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 目录不可写时交给调用方处理
                }
                return dir;
            }
        }

        /// <summary>拼接缓存文件的完整路径。</summary>
        public static string GetCacheFilePath(string fileName)
        {
            return Path.Combine(CacheDirectory, fileName);
        }

        /// <summary>
        /// 清理缓存目录：每次启动时调用。这里的文件都允许丢弃，
        /// 删除失败（被占用）时跳过，下次启动再清。
        /// </summary>
        public static void CleanupCacheDirectory()
        {
            try
            {
                string dir = Path.Combine(AppDataDirectory, "cache");
                if (!Directory.Exists(dir)) return;

                foreach (string file in Directory.GetFiles(dir))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
            catch
            {
                // 清理失败不影响启动
            }
        }

        /// <summary>
        /// Config配置文件读取
        /// </summary>
        public static class AppSettings
        {
            private const string DEFAULT_GITHUB_PROXY = "https://ghfast.top/";

            /// <summary>
            /// 配置文件路径：当前登录用户的 Roaming 专属目录（%APPDATA%\AdbTools\AdbTools.config）。
            /// 不放在 exe 同目录，避免安装目录只读、程序更新被覆盖导致配置丢失；
            /// 用 Roaming 可在同域多台机器间随账户漫游，保留配置
            /// </summary>
            private static readonly string ConfigFilePath =
                Path.Combine(AppDataDirectory, "AdbTools.config");

            private static Configuration _config;

            private static string _LAST_DEVICE_ADDRESS;
            private static List<string> _DEVICE_ADDRESS_HISTORY;
            private static string _GITHUB_PROXY;
            private static string _REFRESH_INTERVAL;
            private static string _CLOSE_TO_TRAY;
            private static string _TRAY_TIP_SHOWN;
            private static string _NOTIFY_DEVICE_CHANGE;
            private static string _NOTIFY_INSTALL_RESULT;
            private static string _NOTIFY_REMOTE_DISCONNECT;
            private static string _NOTIFY_PAIR_RESULT;
            private static string _FILE_SORT_KEY;
            private static string _FILE_SORT_ASC;
            private static string _LOG_WRAP_LINES;
            private static string _WINDOW_TOPMOST;

            /// <summary>
            /// 最近连接的设备地址
            /// </summary>
            public static string LAST_DEVICE_ADDRESS
            {
                get => GetString(ref _LAST_DEVICE_ADDRESS, "LAST_DEVICE_ADDRESS", "");
                set => SetValue("LAST_DEVICE_ADDRESS", value, v => _LAST_DEVICE_ADDRESS = v);
            }

            /// <summary>
            /// 设备连接地址历史记录
            /// </summary>
            public static List<string> DEVICE_ADDRESS_HISTORY
            {
                get
                {
                    if (_DEVICE_ADDRESS_HISTORY == null)
                    {
                        string value = GetKeyValue("DEVICE_ADDRESS_HISTORY");
                        _DEVICE_ADDRESS_HISTORY = string.IsNullOrWhiteSpace(value)
                            ? new List<string>()
                            : new List<string>(value.Split(','));
                    }
                    return _DEVICE_ADDRESS_HISTORY;
                }
                set => SetValue("DEVICE_ADDRESS_HISTORY", string.Join(",", value), v => _DEVICE_ADDRESS_HISTORY = value);
            }

            /// <summary>
            /// GitHub 下载代理前缀
            /// </summary>
            public static string GITHUB_PROXY
            {
                get => GetString(ref _GITHUB_PROXY, "GITHUB_PROXY", DEFAULT_GITHUB_PROXY);
                set => SetValue("GITHUB_PROXY", value, v => _GITHUB_PROXY = v);
            }

            /// <summary>
            /// 配置文件中是否已设置 GitHub 代理
            /// </summary>
            public static bool IS_GITHUB_PROXY => GetKeyValue("GITHUB_PROXY") != null;

            /// <summary>
            /// 设备列表定时刷新间隔（秒），0 表示关闭；默认关闭
            /// </summary>
            public static int REFRESH_INTERVAL
            {
                get
                {
                    string value = GetString(ref _REFRESH_INTERVAL, "REFRESH_INTERVAL", "0");
                    return int.TryParse(value, out int seconds) && seconds > 0 ? seconds : 0;
                }
                set => SetValue("REFRESH_INTERVAL", value.ToString(), v => _REFRESH_INTERVAL = v);
            }

            /// <summary>
            /// 关闭主窗口时最小化到系统托盘（默认开启）。
            /// 关闭后点窗口的 × 等同于退出程序（仍会二次确认）。
            /// </summary>
            public static bool CLOSE_TO_TRAY
            {
                get => GetBool(ref _CLOSE_TO_TRAY, "CLOSE_TO_TRAY", true);
                set => SetValue("CLOSE_TO_TRAY", value ? "true" : "false", v => _CLOSE_TO_TRAY = v);
            }

            /// <summary>
            /// 是否已经提示过“程序已最小化到托盘”，只提示一次
            /// </summary>
            public static bool TRAY_TIP_SHOWN
            {
                get => GetBool(ref _TRAY_TIP_SHOWN, "TRAY_TIP_SHOWN", false);
                set => SetValue("TRAY_TIP_SHOWN", value ? "true" : "false", v => _TRAY_TIP_SHOWN = v);
            }

            /// <summary>
            /// 设备连接/断开时是否弹托盘通知（默认开启）
            /// </summary>
            public static bool NOTIFY_DEVICE_CHANGE
            {
                get => GetBool(ref _NOTIFY_DEVICE_CHANGE, "NOTIFY_DEVICE_CHANGE", true);
                set => SetValue("NOTIFY_DEVICE_CHANGE", value ? "true" : "false", v => _NOTIFY_DEVICE_CHANGE = v);
            }

            /// <summary>
            /// 应用安装成功/失败时是否弹托盘通知（默认开启）
            /// </summary>
            public static bool NOTIFY_INSTALL_RESULT
            {
                get => GetBool(ref _NOTIFY_INSTALL_RESULT, "NOTIFY_INSTALL_RESULT", true);
                set => SetValue("NOTIFY_INSTALL_RESULT", value ? "true" : "false", v => _NOTIFY_INSTALL_RESULT = v);
            }

            /// <summary>
            /// 设备掉线导致远程控制中断时是否弹托盘通知（默认开启）
            /// </summary>
            public static bool NOTIFY_REMOTE_DISCONNECT
            {
                get => GetBool(ref _NOTIFY_REMOTE_DISCONNECT, "NOTIFY_REMOTE_DISCONNECT", true);
                set => SetValue("NOTIFY_REMOTE_DISCONNECT", value ? "true" : "false", v => _NOTIFY_REMOTE_DISCONNECT = v);
            }

            /// <summary>
            /// 无线配对成功时是否发系统通知（默认开启）。
            /// 关闭或通知发不出去时都不再提醒：配对结果以设备列表为准。
            /// </summary>
            public static bool NOTIFY_PAIR_RESULT
            {
                get => GetBool(ref _NOTIFY_PAIR_RESULT, "NOTIFY_PAIR_RESULT", true);
                set => SetValue("NOTIFY_PAIR_RESULT", value ? "true" : "false", v => _NOTIFY_PAIR_RESULT = v);
            }

            /// <summary>
            /// 文件管理列表的排序列：name / type / size / time / permissions，下次打开沿用
            /// </summary>
            public static string FILE_SORT_KEY
            {
                get => GetString(ref _FILE_SORT_KEY, "FILE_SORT_KEY", "name");
                set => SetValue("FILE_SORT_KEY", value, v => _FILE_SORT_KEY = v);
            }

            /// <summary>
            /// 文件管理列表是否升序（默认升序），下次打开沿用
            /// </summary>
            public static bool FILE_SORT_ASC
            {
                get => GetBool(ref _FILE_SORT_ASC, "FILE_SORT_ASC", true);
                set => SetValue("FILE_SORT_ASC", value ? "true" : "false", v => _FILE_SORT_ASC = v);
            }

            /// <summary>
            /// 设备日志窗口是否自动换行（默认关闭），下次打开沿用
            /// </summary>
            public static bool LOG_WRAP_LINES
            {
                get => GetBool(ref _LOG_WRAP_LINES, "LOG_WRAP_LINES", false);
                set => SetValue("LOG_WRAP_LINES", value ? "true" : "false", v => _LOG_WRAP_LINES = v);
            }

            /// <summary>
            /// 主窗口是否始终置顶（默认关闭），下次启动沿用
            /// </summary>
            public static bool WINDOW_TOPMOST
            {
                get => GetBool(ref _WINDOW_TOPMOST, "WINDOW_TOPMOST", false);
                set => SetValue("WINDOW_TOPMOST", value ? "true" : "false", v => _WINDOW_TOPMOST = v);
            }

            /// <summary>
            /// 获取配置的值
            /// </summary>
            /// <param name="key">key</param>
            /// <returns>读取错误时为null</returns>
            public static string GetKeyValue(string key)
            {
                try
                {
                    KeyValueConfigurationElement element = GetConfig().AppSettings.Settings[key];
                    return element?.Value;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>
            /// 在用户目录的配置文件中appSettings配置节增加或修改一对键、值对
            /// </summary>
            public static void UpdateAppConfig(string key, string value)
            {
                try
                {
                    Configuration config = GetConfig();
                    // 目标目录可能不存在（首次保存），先创建，否则Save会失败
                    string directory = Path.GetDirectoryName(ConfigFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    KeyValueConfigurationElement element = config.AppSettings.Settings[key];
                    if (element == null)
                    {
                        config.AppSettings.Settings.Add(key, value);
                    }
                    else
                    {
                        element.Value = value;
                    }
                    config.Save(ConfigurationSaveMode.Modified);
                }
                catch (Exception)
                {
                }
            }

            /// <summary>
            /// 获取配置对象（懒加载；首次使用时将旧版 exe 目录下的配置迁移过来）
            /// </summary>
            private static Configuration GetConfig()
            {
                if (_config == null)
                {
                    MigrateFromExeConfigIfNeeded();
                    _config = ConfigurationManager.OpenMappedExeConfiguration(
                        new ExeConfigurationFileMap { ExeConfigFilename = ConfigFilePath },
                        ConfigurationUserLevel.None);
                }
                return _config;
            }

            /// <summary>
            /// 旧版配置写在 exe 同目录的 *.exe.config 中，首次启动时迁移到用户目录，保留历史记录
            /// </summary>
            private static void MigrateFromExeConfigIfNeeded()
            {
                if (File.Exists(ConfigFilePath))
                {
                    return;
                }
                try
                {
                    Configuration oldConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                    if (oldConfig.AppSettings.Settings.Count == 0)
                    {
                        // 没有旧配置，无需迁移
                        return;
                    }

                    string directory = Path.GetDirectoryName(ConfigFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    Configuration newConfig = ConfigurationManager.OpenMappedExeConfiguration(
                        new ExeConfigurationFileMap { ExeConfigFilename = ConfigFilePath },
                        ConfigurationUserLevel.None);
                    foreach (KeyValueConfigurationElement setting in oldConfig.AppSettings.Settings)
                    {
                        newConfig.AppSettings.Settings.Add(setting.Key, setting.Value);
                    }
                    newConfig.Save(ConfigurationSaveMode.Modified);
                }
                catch (Exception)
                {
                    // 迁移失败不影响使用，按无配置启动
                }
            }

            /// <summary>
            /// 读取字符串配置，带缓存与默认值
            /// </summary>
            private static string GetString(ref string cache, string key, string defaultValue)
            {
                if (cache == null)
                {
                    cache = GetKeyValue(key) ?? defaultValue;
                }
                return cache;
            }

            /// <summary>读取布尔配置，无法解析时用默认值</summary>
            private static bool GetBool(ref string cache, string key, bool defaultValue)
            {
                string value = GetString(ref cache, key, null);
                if (string.IsNullOrEmpty(value))
                {
                    cache = defaultValue ? "true" : "false";
                    return defaultValue;
                }
                if (bool.TryParse(value, out bool parsed))
                {
                    return parsed;
                }
                return defaultValue;
            }

            /// <summary>
            /// 写入配置并更新缓存
            /// </summary>
            private static void SetValue(string key, string value, Action<string> updateCache)
            {
                UpdateAppConfig(key, value);
                updateCache(value);
            }
        }

        /// <summary>
        /// 文件数量统计（统计前即可上限截断，避免遍历超大目录）
        /// </summary>
        public static class FileCounter
        {
            /// <summary>
            /// 统计文件数量（包括子文件夹），达到上限时提前结束
            /// </summary>
            /// <param name="paths">要统计的路径列表（可为文件或目录）</param>
            /// <param name="exceededMax">是否达到或超过上限</param>
            /// <param name="maxCount">文件数量上限</param>
            /// <returns>文件数量（不超过maxCount）</returns>
            public static int CountFiles(string[] paths, out bool exceededMax, int maxCount)
            {
                exceededMax = false;
                if (paths == null || paths.Length == 0)
                {
                    return 0;
                }

                int count = 0;
                foreach (string path in paths)
                {
                    if (File.Exists(path))
                    {
                        count++;
                    }
                    else if (Directory.Exists(path))
                    {
                        count = CountDirectory(path, count, maxCount, out exceededMax);
                        if (exceededMax)
                        {
                            return maxCount;
                        }
                    }

                    if (count >= maxCount)
                    {
                        exceededMax = true;
                        return maxCount;
                    }
                }

                exceededMax = count >= maxCount;
                return count;
            }

            /// <summary>
            /// 迭代统计目录（含子目录）中的文件数，单个目录访问失败时跳过
            /// </summary>
            private static int CountDirectory(string directory, int count, int maxCount, out bool exceededMax)
            {
                exceededMax = false;
                var stack = new Stack<string>();
                stack.Push(directory);

                while (stack.Count > 0)
                {
                    string current = stack.Pop();
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(current))
                        {
                            if (++count >= maxCount)
                            {
                                exceededMax = true;
                                return count;
                            }
                        }
                        foreach (string subDirectory in Directory.EnumerateDirectories(current))
                        {
                            stack.Push(subDirectory);
                        }
                    }
                    catch (Exception)
                    {
                        // 跳过无权限或无法访问的目录，继续统计其他目录
                    }
                }
                return count;
            }
        }
    }
}
