using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// 应用图标缓存。图标直接存成文件：%APPDATA%\AdbTools\data\icons\&lt;包名&gt;.png。
    /// 文件存在即视为已缓存，不额外维护数据库表；解码失败（如 WebP 缺解码器）时由调用方回退占位图。
    /// </summary>
    internal static class AppIconStore
    {
        private static readonly string[] KnownExtensions = { ".png", ".webp", ".jpg", ".jpeg" };

        private static string IconsDirectory
        {
            get
            {
                string dir = Path.Combine(Globals.AppDataDirectory, "data", "icons");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 目录不可写时由调用方忽略
                }
                return dir;
            }
        }

        /// <summary>查找已缓存的图标文件，没有则返回 null。</summary>
        public static string Find(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return null;
            }

            try
            {
                string dir = IconsDirectory;
                foreach (string extension in KnownExtensions)
                {
                    string path = Path.Combine(dir, packageName + extension);
                    if (File.Exists(path)) return path;
                }
            }
            catch
            {
                // 查询失败按没有缓存处理
            }

            return null;
        }

        /// <summary>写入图标文件；同名旧扩展名的文件一并清掉，避免同一应用留两份。</summary>
        public static void Save(string packageName, byte[] data, string extension)
        {
            if (string.IsNullOrEmpty(packageName) || data == null || data.Length == 0)
            {
                return;
            }

            try
            {
                string dir = IconsDirectory;
                string target = Path.Combine(dir, packageName + NormalizeExtension(extension));
                File.WriteAllBytes(target, data);

                foreach (string other in KnownExtensions)
                {
                    string path = Path.Combine(dir, packageName + other);
                    if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(path); } catch { }
                    }
                }
            }
            catch
            {
                // 写图标失败不影响功能，下次再取
            }
        }

        /// <summary>从缓存文件加载图标；文件损坏或系统缺少解码器时返回 null。</summary>
        public static BitmapImage Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                // 用文件流而不是 UriSource：路径里出现 # 等字符时不会被当成 URI 片段
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 64;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return ".png";
            }

            string value = extension.ToLowerInvariant();
            if (value[0] != '.') value = "." + value;

            foreach (string known in KnownExtensions)
            {
                if (known == value) return value;
            }
            return ".png";
        }
    }
}
