using System;
using System.Globalization;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 设备端一个文件 / 目录条目，由 <c>ls -al</c> 的输出解析而来。
    /// </summary>
    internal sealed class DeviceFileEntry
    {
        public string Name { get; set; }

        /// <summary>完整路径（父目录 + 名称），点击操作时直接用。</summary>
        public string FullPath { get; set; }

        public bool IsDirectory { get; set; }

        public bool IsLink { get; set; }

        /// <summary>符号链接指向的目标（非链接时为空）。</summary>
        public string LinkTarget { get; set; }

        /// <summary>
        /// 符号链接的目标是否为目录：true = 目录，false = 文件，null = 未知（目标无权限等）。
        /// 列表里据此决定用「文件夹 + 链接」图标还是普通链接图标，双击时也能直接进入。
        /// </summary>
        public bool? LinkTargetIsDirectory { get; set; }

        public long Size { get; set; }

        /// <summary>解析失败时为 <see cref="DateTime.MinValue"/>。</summary>
        public DateTime Modified { get; set; }

        public string Permissions { get; set; }

        /// <summary>列表里显示的类型图标（矢量绘制，按扩展名分类，结果有缓存）。</summary>
        public System.Windows.Media.ImageSource Icon
        {
            get { return FileIcons.Get(Name, IsDirectory, IsLink, LinkTargetIsDirectory == true); }
        }

        public string TypeText
        {
            get
            {
                if (IsLink) return "链接";
                return IsDirectory ? "文件夹" : "文件";
            }
        }

        /// <summary>目录不显示体积（需要 du，比较慢），显示为 "-"。</summary>
        public string SizeText
        {
            get { return IsDirectory ? "-" : FormatSize(Size); }
        }

        public string ModifiedText
        {
            get { return Modified == DateTime.MinValue ? "-" : Modified.ToString("yyyy-MM-dd HH:mm"); }
        }

        /// <summary>把字节数转成易读文本。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";

            string[] units = { "KB", "MB", "GB", "TB" };
            double value = bytes;
            int index = -1;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }
            return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[index];
        }
    }
}
