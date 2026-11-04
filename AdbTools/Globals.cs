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
        /// 更新安装包下载目录（%APPDATA%\AdbTools\update），不存在时自动创建。
        /// 放在用户目录而不是 %TEMP%，避免临时目录被清理或权限受限。
        /// </summary>
        public static string UpdateDirectory
        {
            get
            {
                string dir = Path.Combine(AppDataDirectory, "update");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 目录不可写时由下载流程报错，这里不抛异常
                }
                return dir;
            }
        }

        /// <summary>
        /// 清理更新下载目录：每次启动时调用，删除上次残留的安装包。
        /// 文件被占用（例如安装程序仍在运行）时跳过，下次启动再清。
        /// </summary>
        public static void CleanupUpdateDirectory()
        {
            try
            {
                string dir = Path.Combine(AppDataDirectory, "update");
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
