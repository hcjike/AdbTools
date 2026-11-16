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
    /// 所有「纸张」类图标共用同一张白纸底（<see cref="PaperPath"/>），
    /// 靠左上角的色带 / 中部的图形区分类型，缩到 20px 也能一眼分开。
    /// </summary>
    internal static class FileIcons
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.Ordinal);

        // 基础色
        private static readonly Brush FolderBrush = Frozen(0xF2, 0xB4, 0x41);
        private static readonly Brush PaperBrush = Frozen(0xFF, 0xFF, 0xFF);
        private static readonly Brush PaperEdgeBrush = Frozen(0x9A, 0xA3, 0xAF);
        private static readonly Brush FoldBrush = Frozen(0xD7, 0xDC, 0xE3);

        // 各类文件的主题色：同一类型用同一种颜色，不同大类的色相尽量拉开
        private static readonly Brush ImageBrush = Frozen(0x22, 0xA0, 0x6B);
        private static readonly Brush VideoBrush = Frozen(0xE5, 0x48, 0x4D);
        private static readonly Brush AudioBrush = Frozen(0x8B, 0x5C, 0xF6);
        private static readonly Brush ArchiveBrush = Frozen(0x9C, 0xA3, 0xAF);
        private static readonly Brush ApkBrush = Frozen(0x2E, 0xB0, 0x6E);
        private static readonly Brush TextBrush = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush MarkdownBrush = Frozen(0x37, 0x41, 0x51);
        private static readonly Brush CodeBrush = Frozen(0x2A, 0x9F, 0xFF);
        private static readonly Brush DbBrush = Frozen(0x0E, 0xA5, 0xE9);
        private static readonly Brush DbLightBrush = Translucent(0xFF, 0xFF, 0xFF, 0.35);
        private static readonly Brush LinkBrush = Frozen(0x2A, 0x9F, 0xFF);

        private static readonly Brush DocumentBrush = Frozen(0x2B, 0x57, 0x9A);   // Word 蓝
        private static readonly Brush SpreadsheetBrush = Frozen(0x21, 0x73, 0x46); // Excel 绿
        private static readonly Brush PresentationBrush = Frozen(0xC4, 0x47, 0x1F); // PPT 橙
        private static readonly Brush PdfBrush = Frozen(0xD9, 0x30, 0x25);         // PDF 红
        private static readonly Brush PdfFoldBrush = Frozen(0xF2, 0x8B, 0x82);
        private static readonly Brush EbookBrush = Frozen(0x8B, 0x5E, 0x3C);       // 书棕
        private static readonly Brush WebBrush = Frozen(0x0E, 0xA5, 0xE9);         // 网页天蓝
        private static readonly Brush ConfigBrush = Frozen(0x64, 0x74, 0x8B);      // 配置石板灰
        private static readonly Brush FontBrush = Frozen(0x7C, 0x3A, 0xED);        // 字体紫
        private static readonly Brush BinaryBrush = Frozen(0x47, 0x55, 0x69);      // 二进制深灰蓝
        private static readonly Brush ScriptBrush = Frozen(0x05, 0x96, 0x69);      // 终端绿
        private static readonly Brush CertBrush = Frozen(0xCA, 0x8A, 0x04);        // 证书金
        private static readonly Brush SubtitleBrush = Frozen(0x14, 0xB8, 0xA6);    // 字幕青
        private static readonly Brush DiscBrush = Frozen(0x63, 0x66, 0xF1);        // 镜像靛蓝
        private static readonly Brush ModelLightBrush = Frozen(0x67, 0xE8, 0xF9);
        private static readonly Brush ModelMidBrush = Frozen(0x06, 0xB6, 0xD4);
        private static readonly Brush ModelDarkBrush = Frozen(0x0E, 0x74, 0x90);
        private static readonly Brush DesignBrush = Frozen(0xEC, 0x48, 0x99);      // 设计稿粉
        private static readonly Brush DesignLightBrush = Frozen(0xF9, 0xA8, 0xD4);
        private static readonly Brush CalendarBrush = Frozen(0xDC, 0x26, 0x26);
        private static readonly Brush CalendarDotBrush = Frozen(0x9C, 0xA3, 0xAF);
        private static readonly Brush ContactBrush = Frozen(0x3B, 0x82, 0xF6);
        private static readonly Brush PackageBrush = Frozen(0x3D, 0xDC, 0x84);      // Android 绿
        private static readonly Brush PackageDisabledBrush = Frozen(0x9C, 0xA3, 0xAF);

        /// <summary>应用数据列表里的应用图标（可访问 / 不可访问两种）。</summary>
        private static ImageSource _packageIcon;
        private static ImageSource _packageIconDisabled;

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

        /// <summary>按名称与属性归类，返回内部使用的类型键（同时用于图标缓存）。</summary>
        /// <summary>
        /// 应用数据列表里的应用图标：与工具栏「应用数据」按钮同款的 Android 机器人，
        /// 白色机器人放在方形底色上。可访问为 Android 绿，不可访问（非调试包）为灰色。
        /// </summary>
        public static ImageSource GetPackageIcon(bool accessible)
        {
            lock (Cache)
            {
                ImageSource cached = accessible ? _packageIcon : _packageIconDisabled;
                if (cached != null) return cached;
            }

            Brush tile = accessible ? PackageBrush : PackageDisabledBrush;
            var group = new DrawingGroup();
            group.Children.Add(Fill(
                "M4.8,3.6 H19.2 A1.2,1.2 0 0 1 20.4,4.8 V19.2 A1.2,1.2 0 0 1 19.2,20.4" +
                " H4.8 A1.2,1.2 0 0 1 3.6,19.2 V4.8 A1.2,1.2 0 0 1 4.8,3.6 Z", tile));
            // 机器人整体缩进，四周留出与按键一致的边距，白色不会顶到绿色边缘
            group.Children.Add(Stroke("M8.6,9.7 L7.5,7.5 M15.4,9.7 L16.5,7.5", PaperBrush, 1.4));
            group.Children.Add(Fill("M5.9,15.4 A6.1,6.1 0 0 1 18.1,15.4 Z", PaperBrush));
            group.Children.Add(Fill(
                "M8,12.5 A0.95,0.95 0 1 1 9.9,12.5 A0.95,0.95 0 1 1 8,12.5 Z" +
                " M14.1,12.5 A0.95,0.95 0 1 1 16,12.5 A0.95,0.95 0 1 1 14.1,12.5 Z",
                tile));

            var image = new DrawingImage(group);
            image.Freeze();

            lock (Cache)
            {
                if (accessible) _packageIcon = image;
                else _packageIconDisabled = image;
            }
            return image;
        }

        private static string GetCategory(string name, bool isDirectory, bool isLink, bool isLinkToDirectory)
        {
            if (isLink) return isLinkToDirectory ? "folderlink" : "link";
            if (isDirectory) return "folder";

            string ext = Path.GetExtension(name ?? string.Empty).ToLowerInvariant().TrimStart('.');

            switch (ext)
            {
                // ---------- 安装包 ----------
                case "apk":
                case "apks":
                case "xapk":
                case "apkm":
                    return "apk";

                // ---------- 图片 ----------
                case "jpg":
                case "jpeg":
                case "jpe":
                case "jfif":
                case "png":
                case "gif":
                case "bmp":
                case "webp":
                case "avif":
                case "jxl":
                case "heic":
                case "heif":
                case "ico":
                case "svg":
                case "tif":
                case "tiff":
                case "jp2":
                case "tga":
                case "dng":
                case "cr2":
                case "nef":
                case "arw":
                case "orf":
                case "raf":
                    return "image";

                // ---------- 视频 ----------
                case "mp4":
                case "mkv":
                case "avi":
                case "mov":
                case "3gp":
                case "3g2":
                case "webm":
                case "flv":
                case "wmv":
                case "asf":
                case "mts":
                case "m2ts":
                case "m4v":
                case "mpg":
                case "mpeg":
                case "ogv":
                case "rm":
                case "rmvb":
                case "f4v":
                case "vob":
                    return "video";

                // ---------- 音频 ----------
                case "mp3":
                case "wav":
                case "flac":
                case "aac":
                case "ogg":
                case "oga":
                case "m4a":
                case "m4b":
                case "amr":
                case "mid":
                case "midi":
                case "opus":
                case "wma":
                case "ape":
                case "aiff":
                case "aif":
                case "au":
                case "mka":
                case "ac3":
                case "dsf":
                case "dff":
                    return "audio";

                // ---------- 压缩包 ----------
                case "zip":
                case "rar":
                case "7z":
                case "tar":
                case "gz":
                case "tgz":
                case "bz2":
                case "tbz2":
                case "xz":
                case "txz":
                case "zst":
                case "lz":
                case "lz4":
                case "lzma":
                case "cab":
                case "arj":
                case "z":
                case "jar":
                case "war":
                case "aar":
                    return "archive";

                // ---------- 纯文本 / 日志 ----------
                case "txt":
                case "log":
                case "text":
                case "nfo":
                case "asc":
                case "diz":
                case "readme":
                    return "text";

                // ---------- Markdown ----------
                case "md":
                case "markdown":
                case "mdown":
                case "mkd":
                    return "markdown";

                // ---------- 文档 ----------
                case "doc":
                case "docx":
                case "docm":
                case "dot":
                case "dotx":
                case "odt":
                case "rtf":
                case "pages":
                case "wps":
                case "sxw":
                    return "document";

                // ---------- 表格 ----------
                case "xls":
                case "xlsx":
                case "xlsm":
                case "xlsb":
                case "ods":
                case "csv":
                case "tsv":
                case "numbers":
                case "et":
                    return "spreadsheet";

                // ---------- 演示文稿 ----------
                case "ppt":
                case "pptx":
                case "pptm":
                case "pps":
                case "ppsx":
                case "odp":
                case "key":
                case "dps":
                    return "presentation";

                // ---------- PDF ----------
                case "pdf":
                    return "pdf";

                // ---------- 电子书 ----------
                case "epub":
                case "mobi":
                case "azw":
                case "azw3":
                case "fb2":
                case "djvu":
                case "lit":
                case "ibooks":
                case "cbz":
                case "cbr":
                    return "ebook";

                // ---------- 源码 ----------
                case "java":
                case "kt":
                case "kts":
                case "cs":
                case "js":
                case "mjs":
                case "cjs":
                case "ts":
                case "jsx":
                case "tsx":
                case "py":
                case "pyw":
                case "c":
                case "cc":
                case "cpp":
                case "cxx":
                case "h":
                case "hpp":
                case "hh":
                case "go":
                case "rs":
                case "rb":
                case "php":
                case "swift":
                case "m":
                case "mm":
                case "scala":
                case "lua":
                case "pl":
                case "r":
                case "dart":
                case "vue":
                case "svelte":
                case "asm":
                    return "code";

                // ---------- 网页 ----------
                case "html":
                case "htm":
                case "xhtml":
                case "css":
                case "scss":
                case "sass":
                case "less":
                case "styl":
                    return "web";

                // ---------- 配置文件 ----------
                case "json":
                case "json5":
                case "xml":
                case "yaml":
                case "yml":
                case "ini":
                case "conf":
                case "cfg":
                case "toml":
                case "properties":
                case "prop":
                case "env":
                case "plist":
                case "lock":
                    return "config";

                // ---------- 数据库 ----------
                case "db":
                case "sqlite":
                case "sqlite3":
                case "sql":
                case "mdb":
                case "accdb":
                case "realm":
                    return "database";

                // ---------- 字体 ----------
                case "ttf":
                case "otf":
                case "ttc":
                case "woff":
                case "woff2":
                case "fnt":
                case "fon":
                case "pfb":
                case "pfm":
                    return "font";

                // ---------- 可执行文件 / 二进制 ----------
                case "so":
                case "dll":
                case "exe":
                case "bin":
                case "elf":
                case "dex":
                case "odex":
                case "vdex":
                case "oat":
                case "art":
                case "class":
                case "ko":
                case "a":
                case "o":
                case "lib":
                case "axf":
                    return "binary";

                // ---------- 脚本 ----------
                case "sh":
                case "bash":
                case "zsh":
                case "fish":
                case "bat":
                case "cmd":
                case "ps1":
                case "psd1":
                case "vbs":
                    return "script";

                // ---------- 证书 / 密钥 ----------
                case "pem":
                case "crt":
                case "cer":
                case "der":
                case "p12":
                case "pfx":
                case "jks":
                case "keystore":
                case "csr":
                case "p7b":
                case "gpg":
                    return "certificate";

                // ---------- 字幕 ----------
                case "srt":
                case "ass":
                case "ssa":
                case "vtt":
                case "sub":
                case "smi":
                case "idx":
                    return "subtitle";

                // ---------- 光盘 / 磁盘镜像 ----------
                case "iso":
                case "img":
                case "dmg":
                case "vhd":
                case "vhdx":
                case "vmdk":
                case "qcow":
                case "qcow2":
                case "cue":
                case "nrg":
                case "toast":
                    return "disc";

                // ---------- 3D 模型 ----------
                case "obj":
                case "stl":
                case "glb":
                case "gltf":
                case "fbx":
                case "dae":
                case "3ds":
                case "blend":
                case "ply":
                case "3mf":
                case "usdz":
                    return "model";

                // ---------- 设计稿 ----------
                case "psd":
                case "psb":
                case "ai":
                case "eps":
                case "indd":
                case "idml":
                case "sketch":
                case "fig":
                case "xd":
                case "cdr":
                case "dwg":
                case "dxf":
                    return "design";

                // ---------- 日历 ----------
                case "ics":
                case "ical":
                case "vcs":
                    return "calendar";

                // ---------- 联系人 ----------
                case "vcf":
                case "vcard":
                    return "contact";

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

                // ---------- 图片：绿色照片 + 太阳 + 山 ----------
                case "image":
                    AddPaper(group);
                    group.Children.Add(Fill("M9.1,12.2 a1.5,1.5 0 1 1 3,0 a1.5,1.5 0 1 1 -3,0 Z", ImageBrush));
                    group.Children.Add(Fill("M8.2,19.2 L12,13.6 L14.6,17 L16.4,15 L18.6,18.2 V19.2 Z", ImageBrush));
                    break;

                // ---------- 视频：红色画面 + 播放键 ----------
                case "video":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,11 H17.6 V18.6 H8.4 Z", VideoBrush));
                    group.Children.Add(Fill("M11.4,13.2 L15.8,14.8 L11.4,16.4 Z", PaperBrush));
                    break;

                // ---------- 音频：紫色双音符 ----------
                case "audio":
                    AddPaper(group);
                    group.Children.Add(Stroke("M12.4,18 V10.6 L17.6,9.4 V16.6", AudioBrush, 1.4));
                    group.Children.Add(Fill("M10.2,18 a2.2,2.2 0 1 1 0.1,0 Z", AudioBrush));
                    group.Children.Add(Fill("M15.4,16.6 a2.2,2.2 0 1 1 0.1,0 Z", AudioBrush));
                    break;

                // ---------- 压缩包：纸面上的拉链 ----------
                case "archive":
                    AddPaper(group);
                    group.Children.Add(Stroke("M8.6,10.8 H17.4 V18.8 H8.6 Z", ArchiveBrush, 1.1));
                    group.Children.Add(Fill("M11.3,10.8 H12.9 V12.4 H11.3 Z", ArchiveBrush));
                    group.Children.Add(Fill("M11.3,12.8 H12.9 V14.4 H11.3 Z", ArchiveBrush));
                    group.Children.Add(Fill("M11.3,14.8 H12.9 V16.4 H11.3 Z", ArchiveBrush));
                    group.Children.Add(Fill("M11.3,16.8 H12.9 V18.8 H11.3 Z", ArchiveBrush));
                    break;

                // ---------- 安装包：Android 机器人 ----------
                case "apk":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,12.4 H18.4 V18.8 H8.4 Z", ApkBrush));
                    group.Children.Add(Stroke("M10,11.4 L9.2,9.8 M16.8,11.4 L17.6,9.8", ApkBrush, 1.1));
                    group.Children.Add(Stroke("M11,14.8 H12.4 M14.4,14.8 H15.8", PaperBrush, 1.4));
                    break;

                // ---------- 纯文本：灰色文字行 ----------
                case "text":
                    AddPaper(group);
                    group.Children.Add(Stroke("M9,12.4 H17 M9,15.2 H17 M9,18 H14", TextBrush, 1.3));
                    break;

                // ---------- Markdown：M 字标 ----------
                case "markdown":
                    AddPaper(group);
                    group.Children.Add(Stroke("M9,18.4 V11.6 L12,15 L15,11.6 V18.4", MarkdownBrush, 1.4));
                    break;

                // ---------- 文档：蓝色页眉 + 文字行 ----------
                case "document":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,10.8 H17.6 V13.2 H8.4 Z", DocumentBrush));
                    group.Children.Add(Stroke("M9.2,15.4 H16.8 M9.2,17.6 H14.6", TextBrush, 1.2));
                    break;

                // ---------- 表格：绿色网格 ----------
                case "spreadsheet":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,10.8 H17.6 V13.4 H8.4 Z", SpreadsheetBrush));
                    group.Children.Add(Stroke("M8.4,13.4 H17.6 V18.8 H8.4 Z", SpreadsheetBrush, 1));
                    group.Children.Add(Stroke("M11.4,13.4 V18.8 M14.6,13.4 V18.8 M8.4,16.1 H17.6", SpreadsheetBrush, 0.9));
                    break;

                // ---------- 演示文稿：橙色图表 ----------
                case "presentation":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,10.8 H17.6 V18.8 H8.4 Z", PresentationBrush));
                    group.Children.Add(Fill("M9.7,15.6 H11.1 V18 H9.7 Z", PaperBrush));
                    group.Children.Add(Fill("M12.3,13.4 H13.7 V18 H12.3 Z", PaperBrush));
                    group.Children.Add(Fill("M14.9,14.6 H16.3 V18 H14.9 Z", PaperBrush));
                    break;

                // ---------- PDF：整页红色 ----------
                case "pdf":
                    group.Children.Add(Fill(PaperPath, PdfBrush));
                    group.Children.Add(Fill(FoldPath, PdfFoldBrush));
                    group.Children.Add(Stroke(PaperPath, PdfBrush, 1));
                    group.Children.Add(Stroke("M9,12 H15.6 M9,14.4 H15.6 M9,16.8 H13", PaperBrush, 1.2));
                    break;

                // ---------- 电子书：翻开的书 ----------
                case "ebook":
                    group.Children.Add(Fill("M6.8,9.6 C8.8,8.8 10.6,9 12,10 C13.4,9 15.2,8.8 17.2,9.6 V18.2 C15.2,17.4 13.4,17.6 12,18.6 C10.6,17.6 8.8,17.4 6.8,18.2 Z",
                        EbookBrush));
                    group.Children.Add(Stroke("M12,10 V18.6", PaperBrush, 0.9));
                    break;

                // ---------- 源码：尖括号 ----------
                case "code":
                    AddPaper(group);
                    group.Children.Add(Stroke("M11.4,12 L8.8,15.2 L11.4,18.4 M15,12 L17.6,15.2 L15,18.4", CodeBrush, 1.4));
                    break;

                // ---------- 网页：地球 ----------
                case "web":
                    AddPaper(group);
                    group.Children.Add(Stroke("M8.6,15 a4.1,4.1 0 1 1 8.2,0 a4.1,4.1 0 1 1 -8.2,0 Z", WebBrush, 1.2));
                    group.Children.Add(Stroke("M8.7,15 H17.3 M12.7,10.9 C10.4,12.7 10.4,17.3 12.7,19.1 M12.7,10.9 C15,12.7 15,17.3 12.7,19.1",
                        WebBrush, 0.9));
                    break;

                // ---------- 配置：滑杆 ----------
                case "config":
                    AddPaper(group);
                    group.Children.Add(Stroke("M8.8,12.6 H17.2 M8.8,17.4 H17.2", ConfigBrush, 1.1));
                    group.Children.Add(Fill("M9.4,12.6 a1.5,1.5 0 1 1 3,0 a1.5,1.5 0 1 1 -3,0 Z", ConfigBrush));
                    group.Children.Add(Fill("M13.6,17.4 a1.5,1.5 0 1 1 3,0 a1.5,1.5 0 1 1 -3,0 Z", ConfigBrush));
                    break;

                // ---------- 数据库：蓝色圆柱 ----------
                case "database":
                    group.Children.Add(Fill("M5.4,6.4 H19.4 V19 H5.4 Z", DbBrush));
                    group.Children.Add(Fill("M5.4,19 a7,2.6 0 0 0 14,0 a7,2.6 0 0 0 -14,0 Z", DbBrush));
                    group.Children.Add(Fill("M5.4,12.6 a7,2.6 0 0 0 14,0 a7,2.6 0 0 0 -14,0 Z", DbLightBrush));
                    group.Children.Add(Fill("M5.4,6.4 a7,2.6 0 1 1 14,0 a7,2.6 0 1 1 -14,0 Z", DbBrush));
                    break;

                // ---------- 字体：字母 A ----------
                case "font":
                    AddPaper(group);
                    group.Children.Add(Stroke("M9.2,18.6 L12.7,10.4 L16.2,18.6", FontBrush, 1.3));
                    group.Children.Add(Stroke("M10.4,15.4 H15", FontBrush, 1.3));
                    break;

                // ---------- 可执行文件：芯片 ----------
                case "binary":
                    AddPaper(group);
                    // 左右各三条引脚 + 中间的芯片方框，缩到 20px 也能认出是“可执行/二进制”
                    group.Children.Add(Fill("M7.8,13.2 H9 V14 H7.8 Z M7.8,15.3 H9 V16.1 H7.8 Z M7.8,17.4 H9 V18.2 H7.8 Z" +
                                            " M16,13.2 H17.2 V14 H16 Z M16,15.3 H17.2 V16.1 H16 Z M16,17.4 H17.2 V18.2 H16 Z",
                        BinaryBrush));
                    group.Children.Add(Fill("M9,12.2 H16 V19.2 H9 Z", BinaryBrush));
                    group.Children.Add(Fill("M11,14.2 H14 V17.2 H11 Z", PaperBrush));
                    break;

                // ---------- 脚本：终端提示符 ----------
                case "script":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,11 H17.6 V18.8 H8.4 Z", ScriptBrush));
                    group.Children.Add(Stroke("M10.2,13.2 L12.6,15.2 L10.2,17.2", PaperBrush, 1.2));
                    group.Children.Add(Stroke("M13.6,17.2 H16.4", PaperBrush, 1.2));
                    break;

                // ---------- 证书 / 密钥 ----------
                case "certificate":
                    AddPaper(group);
                    group.Children.Add(Stroke("M9,12.4 a2.1,2.1 0 1 1 4.2,0 a2.1,2.1 0 1 1 -4.2,0 Z", CertBrush, 1.4));
                    group.Children.Add(Stroke("M12.6,14.1 L16.9,18.4 M15.4,16.9 L16.9,15.4", CertBrush, 1.4));
                    break;

                // ---------- 字幕：对话气泡 ----------
                case "subtitle":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.4,11.2 H17.6 V16.4 H12.6 L10.2,18.8 V16.4 H8.4 Z", SubtitleBrush));
                    group.Children.Add(Stroke("M10,12.9 H16 M10,14.6 H13.6", PaperBrush, 0.9));
                    break;

                // ---------- 镜像：光盘 ----------
                case "disc":
                    group.Children.Add(Fill("M12,6 a6,6 0 1 1 0,12 a6,6 0 1 1 0,-12 Z", DiscBrush));
                    group.Children.Add(Stroke("M12,8.4 a3.6,3.6 0 1 1 0,7.2 a3.6,3.6 0 1 1 0,-7.2 Z", PaperBrush, 0.9));
                    group.Children.Add(Fill("M11.3,12 a0.7,0.7 0 1 1 1.4,0 a0.7,0.7 0 1 1 -1.4,0 Z", PaperBrush));
                    break;

                // ---------- 3D 模型：立方体 ----------
                case "model":
                    group.Children.Add(Fill("M7.2,11.2 L12,8.6 L16.8,11.2 L12,13.8 Z", ModelLightBrush));
                    group.Children.Add(Fill("M7.2,11.2 L12,13.8 V19 L7.2,16.4 Z", ModelDarkBrush));
                    group.Children.Add(Fill("M16.8,11.2 L12,13.8 V19 L16.8,16.4 Z", ModelMidBrush));
                    break;

                // ---------- 设计稿：画笔 ----------
                case "design":
                    group.Children.Add(Fill("M9.2,18.9 L10.6,14.6 L15.6,9.4 L18,11.8 L12.8,16.8 Z", DesignBrush));
                    group.Children.Add(Fill("M9.2,18.9 L10.2,16.2 L11.9,17.9 Z", DesignLightBrush));
                    break;

                // ---------- 日历 ----------
                case "calendar":
                    AddPaper(group);
                    group.Children.Add(Fill("M8.2,10.8 H17.8 V13.4 H8.2 Z", CalendarBrush));
                    group.Children.Add(Stroke("M8.2,13.4 H17.8 V19 H8.2 Z", CalendarBrush, 1));
                    group.Children.Add(Fill("M9.6,14.9 H10.8 V16.1 H9.6 Z M12.1,14.9 H13.3 V16.1 H12.1 Z M14.6,14.9 H15.8 V16.1 H14.6 Z M9.6,17 H10.8 V18.2 H9.6 Z M12.1,17 H13.3 V18.2 H12.1 Z",
                        CalendarDotBrush));
                    break;

                // ---------- 联系人：头像 ----------
                case "contact":
                    AddPaper(group);
                    group.Children.Add(Fill("M10.4,12 a1.7,1.7 0 1 1 3.4,0 a1.7,1.7 0 1 1 -3.4,0 Z", ContactBrush));
                    group.Children.Add(Fill("M8.8,18.8 a3.9,3.3 0 0 1 7.8,0 Z", ContactBrush));
                    break;

                // ---------- 未知类型：空白纸张 ----------
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
