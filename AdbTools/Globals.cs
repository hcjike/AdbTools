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
            private static readonly string ConfigFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AdbTools",
                "AdbTools.config");

            private static Configuration _config;

            private static string _LAST_DEVICE_ADDRESS;
            private static List<string> _DEVICE_ADDRESS_HISTORY;
            private static string _GITHUB_PROXY;

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
