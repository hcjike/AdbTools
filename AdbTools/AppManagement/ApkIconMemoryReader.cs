using System;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// 安装列表专用：直接从 APK 读取应用图标到内存，不写缓存、不保存文件。
    /// 支持普通位图、WebP 与 adaptive-icon 的矢量前景。
    /// </summary>
    internal static class ApkIconMemoryReader
    {
        private const long MaxManifestBytes = 4 * 1024 * 1024;
        private const long MaxArscBytes = 48 * 1024 * 1024;
        private const long MaxIconBytes = 4 * 1024 * 1024;
        private const int MaxAdaptiveDepth = 5;

        public static ImageSource Read(string apkPath)
        {
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(apkPath))
                {
                    byte[] manifest = ReadEntry(zip, "AndroidManifest.xml", MaxManifestBytes);
                    byte[] arsc = ReadEntry(zip, "resources.arsc", MaxArscBytes);
                    if (manifest == null || arsc == null) return null;

                    uint iconId = ApkLabelReader.ReadIconResourceId(manifest);
                    if (iconId == 0) return null;

                    string path = ApkLabelReader.ResolveFilePath(arsc, iconId);
                    uint? backgroundColor = null;

                    for (int depth = 0; depth < MaxAdaptiveDepth && IsXml(path); depth++)
                    {
                        byte[] xml = ReadEntry(zip, path, MaxIconBytes);
                        if (xml == null) return null;

                        uint foregroundId = ApkLabelReader.ParseAdaptiveIconForeground(xml);
                        if (foregroundId == 0) break;

                        if (!backgroundColor.HasValue)
                        {
                            uint color;
                            if (ApkLabelReader.TryReadAdaptiveIconBackgroundColor(xml, arsc, out color))
                            {
                                backgroundColor = color;
                            }
                        }

                        path = ApkLabelReader.ResolveFilePath(arsc, foregroundId);
                    }

                    if (string.IsNullOrEmpty(path)) return null;

                    byte[] data = ReadEntry(zip, path, MaxIconBytes);
                    if (data == null) return null;

                    if (IsXml(path))
                    {
                        VectorIcon vector = ApkLabelReader.ParseVectorDrawable(data, arsc);
                        if (vector == null) return null;
                        data = VectorIconRenderer.RenderPng(vector, backgroundColor);
                        if (data == null) return null;
                    }

                    return ToImageSource(data);
                }
            }
            catch (Exception)
            {
                // 图标失败只影响显示，不影响安装流程
                return null;
            }
        }

        private static byte[] ReadEntry(ZipArchive zip, string entryName, long maxBytes)
        {
            if (string.IsNullOrEmpty(entryName) || entryName.Length > 512) return null;

            ZipArchiveEntry entry = zip.GetEntry(entryName);
            if (entry == null || entry.Length > maxBytes) return null;

            using (Stream stream = entry.Open())
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static ImageSource ToImageSource(byte[] data)
        {
            if (WebpDecoder.IsWebp(data))
            {
                return WebpDecoder.Decode(data);
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 64;
                    image.DecodePixelHeight = 64;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsXml(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        }
    }
}
