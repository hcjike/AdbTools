using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace AdbTools.bean
{
    /// <summary>APK 安装列表中每一行的状态。</summary>
    public enum ApkInstallState
    {
        Pending,
        Unselected,
        Installing,
        Success,
        Failed,
        Skipped
    }

    /// <summary>安装列表中的单个 APK：解析信息、勾选状态与安装结果。</summary>
    public sealed class ApkInstallItem : INotifyPropertyChanged
    {
        private static readonly Brush[] AvatarBrushes =
        {
            CreateBrush(0x60, 0xA5, 0xFA),
            CreateBrush(0x34, 0xD3, 0x99),
            CreateBrush(0xF5, 0x9E, 0x0B),
            CreateBrush(0xA7, 0x8B, 0xFA),
            CreateBrush(0xF4, 0x72, 0xB0),
            CreateBrush(0x0E, 0xA5, 0xE9)
        };

        private string _label;
        private string _packageName;
        private string _sizeText;
        private bool _isSelected = true;
        private bool _selectionEnabled = true;
        private string _parseState = "等待解析";
        private ApkInstallState _installState = ApkInstallState.Pending;
        private string _errorText;
        private ImageSource _icon;

        public ApkInstallItem(string filePath, string sizeText)
        {
            FilePath = filePath;
            _sizeText = sizeText;
            _label = Path.GetFileName(filePath);
        }

        public string FilePath { get; }

        public string Label
        {
            get { return _label; }
            set { Set(ref _label, value, nameof(Label)); }
        }

        public string PackageName
        {
            get { return _packageName; }
            set { Set(ref _packageName, value, nameof(PackageName)); }
        }

        /// <summary>直接从 APK 读取到内存的图标；不缓存、不落盘。</summary>
        public ImageSource Icon
        {
            get { return _icon; }
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                Raise(nameof(Icon));
                Raise(nameof(HasIcon));
                Raise(nameof(NoIcon));
                Raise(nameof(IconBackground));
            }
        }

        public Brush IconBackground
        {
            get { return _icon == null ? AvatarBrush : null; }
        }

        public bool HasIcon { get { return _icon != null; } }

        public bool NoIcon { get { return _icon == null; } }

        /// <summary>占位字符：优先应用名首字，未解析出名称时取包名首字母。</summary>
        public string Initial
        {
            get
            {
                string source = !string.IsNullOrWhiteSpace(_label) ? _label.Trim() : _packageName;
                if (string.IsNullOrWhiteSpace(source)) source = Path.GetFileNameWithoutExtension(FilePath);
                return string.IsNullOrEmpty(source) ? "?" : source.Substring(0, 1).ToUpperInvariant();
            }
        }

        /// <summary>按包名生成固定占位色，避免不同应用颜色乱跳。</summary>
        public Brush AvatarBrush
        {
            get
            {
                string source = string.IsNullOrEmpty(_packageName) ? FilePath : _packageName;
                int hash = 0;
                foreach (char c in source)
                {
                    hash = (hash * 31 + c) & 0x7FFFFFFF;
                }
                return AvatarBrushes[hash % AvatarBrushes.Length];
            }
        }

        public string SizeText
        {
            get { return _sizeText; }
            set { Set(ref _sizeText, value, nameof(SizeText)); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>开始安装后禁止再修改勾选状态。</summary>
        public bool SelectionEnabled
        {
            get { return _selectionEnabled; }
            set { Set(ref _selectionEnabled, value, nameof(SelectionEnabled)); }
        }

        public string ParseState
        {
            get { return _parseState; }
            set { Set(ref _parseState, value, nameof(ParseState)); }
        }

        public ApkInstallState InstallState
        {
            get { return _installState; }
            set
            {
                if (_installState == value) return;
                _installState = value;
                Raise(nameof(InstallState));
                Raise(nameof(InstallStateText));
            }
        }

        public string InstallStateText
        {
            get
            {
                switch (_installState)
                {
                    case ApkInstallState.Unselected: return "未选择";
                    case ApkInstallState.Installing: return "安装中";
                    case ApkInstallState.Success: return "成功";
                    case ApkInstallState.Failed: return "失败";
                    case ApkInstallState.Skipped: return "跳过";
                    default: return "待安装";
                }
            }
        }

        public string ErrorText
        {
            get { return _errorText; }
            set { Set(ref _errorText, value, nameof(ErrorText)); }
        }

        public bool HasError
        {
            get { return !string.IsNullOrWhiteSpace(_errorText); }
        }

        /// <summary>用于统计文案：优先应用名，失败时回退文件名。</summary>
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(_label) ? Path.GetFileName(FilePath) : _label; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value)) return;
            field = value;
            Raise(propertyName);

            if (propertyName == nameof(Label))
            {
                Raise(nameof(DisplayName));
                Raise(nameof(Initial));
            }
            else if (propertyName == nameof(PackageName))
            {
                Raise(nameof(Initial));
                Raise(nameof(AvatarBrush));
            }
            else if (propertyName == nameof(ErrorText))
            {
                Raise(nameof(HasError));
            }
        }

        private void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
