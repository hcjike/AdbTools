using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdbTools
{
    /// <summary>
    /// 应用名缓存。解析一个名称要读该应用的 APK，成本不低，
    /// 缓存到本地后再次打开列表即可直接显示。
    /// </summary>
    internal static class AppLabelCache
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Labels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static string _path;

        public static string Get(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return null;

            lock (Sync)
            {
                EnsureLoaded();
                string label;
                return Labels.TryGetValue(packageName, out label) ? label : null;
            }
        }

        public static void Set(string packageName, string label)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrWhiteSpace(label)) return;

            string clean = label.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (clean.Length == 0) return;

            lock (Sync)
            {
                EnsureLoaded();
                Labels[packageName] = clean;

                try
                {
                    string path = CachePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(path, packageName + "\t" + clean + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 缓存写失败不影响使用，下次重新解析即可
                }
            }
        }

        private static string CachePath
        {
            get
            {
                if (_path == null)
                {
                    _path = ResolveCachePath();
                }
                return _path;
            }
        }

        /// <summary>
        /// 统一放在 %APPDATA%\AdbTools\data 下：程序装在哪里都能写，
        /// 也不会和 exe 目录混在一起（装到 Program Files 时同样可用）。
        /// </summary>
        private static string ResolveCachePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AdbTools", "data");
            return Path.Combine(dir, "app_labels.txt");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || tab >= line.Length - 1) continue;
                    string package = line.Substring(0, tab).Trim();
                    string label = line.Substring(tab + 1).Trim();
                    if (package.Length == 0 || label.Length == 0) continue;
                    Labels[package] = label;
                }
            }
            catch
            {
                // 缓存损坏就当没有，不影响功能
            }
        }
    }
}
