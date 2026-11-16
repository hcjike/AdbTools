using System;
using System.ComponentModel;
using System.Globalization;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 设备端一个文件 / 目录条目，由 <c>ls -al</c> 的输出解析而来。
    /// 目录名恰好是包名时（/data/data、/sdcard/Android/data 等），会补上应用名与图标，
    /// 这些信息是异步回填的，所以需要 INotifyPropertyChanged。
    /// </summary>
    internal sealed class DeviceFileEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

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

        /// <summary>
        /// 应用列表里的条目：包名。非空表示这一行是“应用”，双击进入它的私有目录，
        /// 而不是普通文件（此时 FullPath / IsDirectory 等字段都无意义）。
        /// </summary>
        public string Package { get; set; }

        /// <summary>该应用能否用 run-as 访问（仅应用条目有效，即“是不是 debug 包”）。</summary>
        public bool CanRunAs { get; set; }

        /// <summary>
        /// 该应用的数据目录当前能不能打开：debug 包可以（run-as），
        /// 或者设备 shell 已是 root（所有应用都能开）。
        /// 普通文件条目恒为 true，只有应用列表里的“打不开的应用”才设为 false。
        /// </summary>
        public bool CanOpen { get; set; } = true;

        private System.Windows.Media.ImageSource _iconOverride;
        private string _appLabel;

        /// <summary>应用条目用固定图标（应用列表里的应用图标），覆盖按扩展名推断的图标。</summary>
        public System.Windows.Media.ImageSource IconOverride
        {
            get { return _iconOverride; }
            set
            {
                if (ReferenceEquals(_iconOverride, value)) return;
                _iconOverride = value;
                Raise("Icon");
            }
        }

        /// <summary>应用条目用固定类型文案（如“可访问 / 需 root”）。</summary>
        public string TypeTextOverride { get; set; }

        /// <summary>
        /// 包名目录对应的应用名（来自应用名缓存或后台解析）。为空表示不是应用目录、还没解析出来，
        /// 或者该包确实没有名字。
        /// </summary>
        public string AppLabel
        {
            get { return _appLabel; }
            set
            {
                if (string.Equals(_appLabel, value, StringComparison.Ordinal)) return;
                _appLabel = value;
                Raise("AppLabel");
                Raise("AppLabelText");
            }
        }

        /// <summary>名称后面追加的灰色小字：<c>[应用名]</c>；没有应用名时为空串，不占位也不留白。</summary>
        public string AppLabelText
        {
            get { return string.IsNullOrEmpty(_appLabel) ? string.Empty : "[" + _appLabel + "]"; }
        }

        private void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>列表里显示的类型图标（矢量绘制，按扩展名分类，结果有缓存）。</summary>
        public System.Windows.Media.ImageSource Icon
        {
            get
            {
                if (IconOverride != null) return IconOverride;
                return FileIcons.Get(Name, IsDirectory, IsLink, LinkTargetIsDirectory == true);
            }
        }

        public string TypeText
        {
            get
            {
                if (TypeTextOverride != null) return TypeTextOverride;
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
