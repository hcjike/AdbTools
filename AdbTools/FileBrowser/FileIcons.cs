using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 文件类型图标：用矢量图形绘制（不依赖图片资源与图标字体），
    /// 按扩展名归类，同一类只构建一次并缓存。
    /// </summary>
    internal static class FileIcons
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.Ordinal);

        private static readonly Brush FolderBrush = Frozen(0xF2, 0xB4, 0x41);
        private static readonly Brush PaperBrush = Frozen(0xFF, 0xFF, 0xFF);
        private static readonly Brush PaperEdgeBrush = Frozen(0x9A, 0xA3, 0xAF);
        private static readonly Brush FoldBrush = Frozen(0xD7, 0xDC, 0xE3);
        private static readonly Brush ImageBrush = Frozen(0x22, 0xA0, 0x6B);
        private static readonly Brush VideoBrush = Frozen(0xE5, 0x48, 0x4D);
        private static readonly Brush AudioBrush = Frozen(0x8B, 0x5C, 0xF6);
        private static readonly Brush ArchiveBrush = Frozen(0x9C, 0xA3, 0xAF);
        private static readonly Brush ApkBrush = Frozen(0x2E, 0xB0, 0x6E);
        private static readonly Brush TextBrush = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush CodeBrush = Frozen(0x2A, 0x9F, 0xFF);
        private static readonly Brush DbBrush = Frozen(0x0E, 0xA5, 0xE9);
        private static readonly Brush DbLightBrush = Translucent(0xFF, 0xFF, 0xFF, 0.35);
        private static readonly Brush LinkBrush = Frozen(0x2A, 0x9F, 0xFF);

        /// <summary>文件「纸张」基础轮廓，左上角带折角。</summary>
        private const string PaperPath = "M6,2 H15 L20,7 V22 H6 Z";
        private const string FoldPath = "M15,2 L20,7 H15 Z";

        public static ImageSource Get(string name, bool isDirectory, bool isLink, bool isLinkToDirectory = false)
        {
            string category = GetCategory(name, isDirectory, isLink, isLinkToDirectory);

            lock (Cache)
            {
                ImageSource cached;
                if (Cache.TryGetValue(category, out cached)) return cached;
            }

            ImageSource built = Create(category);

            lock (Cache)
            {
                Cache[category] = built;
            }
            return built;
        }

        private static string GetCategory(string name, bool isDirectory, bool isLink, bool isLinkToDirectory)
        {
            if (isLink) return isLinkToDirectory ? "folderlink" : "link";
            if (isDirectory) return "folder";

            string ext = Path.GetExtension(name ?? string.Empty).ToLowerInvariant().TrimStart('.');

            switch (ext)
            {
                case "apk":
                    return "apk";

                case "jpg":
                case "jpeg":
                case "png":
                case "gif":
                case "bmp":
                case "webp":
                case "heic":
                case "heif":
                case "ico":
                case "svg":
                    return "image";

                case "mp4":
                case "mkv":
                case "avi":
                case "mov":
                case "3gp":
                case "webm":
                case "flv":
                case "wmv":
                case "ts":
                case "m4v":
                    return "video";

                case "mp3":
                case "wav":
                case "flac":
                case "aac":
                case "ogg":
                case "m4a":
                case "amr":
                case "mid":
                case "opus":
                    return "audio";

                case "zip":
                case "rar":
                case "7z":
                case "tar":
                case "gz":
                case "bz2":
                case "xz":
                case "iso":
                    return "archive";

                case "txt":
                case "md":
                case "log":
                case "json":
                case "xml":
                case "csv":
                case "ini":
                case "conf":
                case "yaml":
                case "yml":
                case "pdf":
                case "doc":
                case "docx":
                case "xls":
                case "xlsx":
                    return "text";

                case "java":
                case "kt":
                case "cs":
                case "js":
                case "py":
                case "c":
                case "cpp":
                case "h":
                case "hpp":
                case "go":
                case "rs":
                case "rb":
                case "php":
                case "sh":
                case "bat":
                case "ps1":
                case "html":
                case "css":
                    return "code";

                case "db":
                case "sqlite":
                case "sql":
                    return "database";

                default:
                    return "file";
            }
        }

        private static ImageSource Create(string category)
        {
            var group = new DrawingGroup();

            switch (category)
            {
                case "folder":
                    group.Children.Add(Fill("M3,5 H9.2 L11.2,7.4 H21 V19 H3 Z", FolderBrush));
                    break;

                case "folderlink":
                    // 文件夹 + 右下角链接徽标：表示“指向目录的符号链接”，与普通链接区分开
                    group.Children.Add(Fill("M3,5 H9.2 L11.2,7.4 H21 V19 H3 Z", FolderBrush));
                    group.Children.Add(Fill("M17.5,13.4 a4.1,4.1 0 1 1 0,8.2 a4.1,4.1 0 1 1 0,-8.2 Z", LinkBrush));
                    group.Children.Add(Stroke("M15.6,19.3 L19.4,15.5 M17.7,15.3 H19.6 V17.2", PaperBrush, 1.2));
                    break;

                case "link":
                    AddPaper(group);
                    group.Children.Add(Stroke("M18.5,10.5 L13,16 M8.8,12.6 a2.6,2.6 0 1 1 0,3.7 l2,-2 M12.6,15.3 a2.6,2.6 0 1 1 0,-3.7 l-2,2",
                        LinkBrush, 1.5));
                    break;

                case "image":
                    AddPaper(group);
                    group.Children.Add(Fill("M10.2,10.6 a1.6,1.6 0 1 1 0.1,0 Z", ImageBrush));
                    group.Children.Add(Fill("M8.2,19.2 L12,13.6 L14.6,17 L16.4,15 L18.6,18.2 V19.2 Z", ImageBrush));
                    break;

                case "video":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.5,11 H18 V18.5 H8.5 Z", VideoBrush));
                    group.Children.Add(Fill("M11.6,13 L16,14.75 L11.6,16.5 Z", PaperBrush));
                    break;

                case "audio":
                    AddPaper(group);
                    group.Children.Add(Stroke("M12.4,18 V10.6 L17.6,9.4 V16.6", AudioBrush, 1.4));
                    group.Children.Add(Fill("M10.2,18 a2.2,2.2 0 1 1 0.1,0 Z", AudioBrush));
                    group.Children.Add(Fill("M15.4,16.6 a2.2,2.2 0 1 1 0.1,0 Z", AudioBrush));
                    break;

                case "archive":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,9.6 H18.4 V12.4 H8.4 Z", ArchiveBrush));
                    group.Children.Add(Fill("M12.6,13.2 H14.2 V15.2 H12.6 Z", ArchiveBrush));
                    break;

                case "apk":
                    AddPaper(group);
                    // Android 机器人头像：天线 + 头身 + 两只方眼
                    group.Children.Add(Fill("M8.4,12.4 H18.4 V18.8 H8.4 Z", ApkBrush));
                    group.Children.Add(Stroke("M10,11.4 L9.2,9.8 M16.8,11.4 L17.6,9.8", ApkBrush, 1.1));
                    group.Children.Add(Stroke("M11,14.8 H12.4 M14.4,14.8 H15.8", PaperBrush, 1.4));
                    break;

                case "text":
                    AddPaper(group);
                    group.Children.Add(Stroke("M9,12.4 H17 M9,15.2 H17 M9,18 H14", TextBrush, 1.3));
                    break;

                case "code":
                    AddPaper(group);
                    group.Children.Add(Stroke("M11.4,12 L8.8,15.2 L11.4,18.4 M15,12 L17.6,15.2 L15,18.4", CodeBrush, 1.4));
                    break;

                case "database":
                    group.Children.Add(Fill("M5.4,6.4 H19.4 V19 H5.4 Z", DbBrush));
                    group.Children.Add(Fill("M5.4,19 a7,2.6 0 0 0 14,0 a7,2.6 0 0 0 -14,0 Z", DbBrush));
                    group.Children.Add(Fill("M5.4,12.6 a7,2.6 0 0 0 14,0 a7,2.6 0 0 0 -14,0 Z", DbLightBrush));
                    group.Children.Add(Fill("M5.4,6.4 a7,2.6 0 1 1 14,0 a7,2.6 0 1 1 -14,0 Z", DbBrush));
                    break;

                default:
                    AddPaper(group);
                    break;
            }

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        /// <summary>白色纸张 + 灰色描边 + 折角，绝大多数文件类图标共用这个底。</summary>
        private static void AddPaper(DrawingGroup group)
        {
            group.Children.Add(Fill(PaperPath, PaperBrush));
            group.Children.Add(Fill(FoldPath, FoldBrush));
            group.Children.Add(Stroke(PaperPath, PaperEdgeBrush, 1));
        }

        private static GeometryDrawing Fill(string path, Brush brush)
        {
            var drawing = new GeometryDrawing(brush, null, Geometry.Parse(path));
            drawing.Freeze();
            return drawing;
        }

        private static GeometryDrawing Stroke(string path, Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            var drawing = new GeometryDrawing(null, pen, Geometry.Parse(path));
            drawing.Freeze();
            return drawing;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Translucent(byte r, byte g, byte b, double opacity)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b)) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }
    }
}
