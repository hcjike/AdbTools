using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// 应用图标缓存。图标直接存成文件：%APPDATA%\AdbTools\data\icons\&lt;包名&gt;.png。
    /// 文件存在即视为已缓存，不额外维护数据库表；解码失败时由调用方回退占位图。
    /// 缓存里可能出现的格式：png/jpg（WPF 原生支持）、webp（需要 libwebp，见 WebpDecoder）。
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

        /// <summary>写入图标文件；WebP 会先转成 PNG，同名旧扩展名的文件一并清掉，避免同一应用留两份。</summary>
        public static void Save(string packageName, byte[] data, string extension)
        {
            if (string.IsNullOrEmpty(packageName) || data == null || data.Length == 0)
            {
                return;
            }

            // WebP 只在取图时解码一次，随后以 PNG 落盘：解码器缺失也不会反复失败
            if (NormalizeExtension(extension) == ".webp" || WebpDecoder.IsWebp(data))
            {
                byte[] png = EncodePng(WebpDecoder.Decode(data));
                if (png != null)
                {
                    SaveBytes(packageName, ".png", png);
                    return;
                }
            }

            SaveBytes(packageName, NormalizeExtension(extension), data);
        }

        /// <summary>保存一张位图（矢量图标渲染结果）：统一转成 PNG。</summary>
        public static void SaveBitmap(string packageName, BitmapSource image)
        {
            byte[] png = EncodePng(image);
            if (png != null)
            {
                SaveBytes(packageName, ".png", png);
            }
        }

        /// <summary>从缓存文件加载图标；文件损坏或系统缺少解码器时返回 null。</summary>
        public static ImageSource Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            byte[] data;
            try
            {
                // 先整份读进内存：既能按文件头判断真实格式，也不会一直占着文件
                data = File.ReadAllBytes(path);
            }
            catch
            {
                return null;
            }

            if (data.Length == 0)
            {
                return null;
            }

            if (WebpDecoder.IsWebp(data))
            {
                BitmapSource decoded = WebpDecoder.Decode(data);
                if (decoded == null)
                {
                    return null;
                }

                // 旧缓存里的 WebP 顺手转成 PNG，后续启动直接走系统解码器
                if (string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase))
                {
                    SaveBitmap(Path.GetFileNameWithoutExtension(path), decoded);
                }

                return decoded;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
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

        private static void SaveBytes(string packageName, string extension, byte[] data)
        {
            try
            {
                string dir = IconsDirectory;
                string target = Path.Combine(dir, packageName + extension);
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

        private static byte[] EncodePng(BitmapSource image)
        {
            if (image == null)
            {
                return null;
            }

            try
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (MemoryStream stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
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
