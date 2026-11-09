using System.ComponentModel;
using System.Windows.Media;

namespace AdbTools.bean
{
    /// <summary>
    /// 应用列表中的一行。应用名需要解析 APK 才能得到，未解析出来时用包名兜底。
    /// </summary>
    public class AppInfo : INotifyPropertyChanged
    {
        private static readonly Brush DisabledBackground = CreateBrush(0xFE, 0xF3, 0xE2);
        private static readonly Brush DisabledForeground = CreateBrush(0xD9, 0x77, 0x06);
        private static readonly Brush EnabledBackground = CreateBrush(0xEC, 0xFD, 0xF5);
        private static readonly Brush EnabledForeground = CreateBrush(0x05, 0x96, 0x69);

        private static readonly Brush[] AvatarBrushes = CreateAvatarBrushes();

        private string _displayName;
        private bool _isDisabled;
        private bool _isBusy;
        private bool _isService;
        private bool _nameResolved;
        private bool _fromCache;
        private ImageSource _icon;

        public string PackageName { get; set; }

        public string ApkPath { get; set; }

        public bool IsSystem { get; set; }

        /// <summary>已解析并缓存的图标；为空时界面显示首字母占位。</summary>
        public ImageSource Icon
        {
            get { return _icon; }
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                Raise("Icon");
                Raise("HasIcon");
                Raise("NoIcon");
                Raise("IconBackground");
            }
        }

        /// <summary>图标占位背景：没有图标时用固定色块，图标加载成功后改为透明，让真实图标自然呈现。</summary>
        public Brush IconBackground
        {
            get { return _icon == null ? AvatarBrush : null; }
        }

        public bool HasIcon
        {
            get { return _icon != null; }
        }

        public bool NoIcon
        {
            get { return _icon == null; }
        }

        /// <summary>占位字符：优先取应用名第一个字，名称还没解析出来时退回包名首字母。</summary>
        public string Initial
        {
            get
            {
                string source = !string.IsNullOrWhiteSpace(_displayName) ? _displayName.Trim() : PackageName;
                if (string.IsNullOrWhiteSpace(source)) return "?";

                // 处理 emoji 等代理对字符，避免截断半个字符
                int length = char.IsHighSurrogate(source[0]) && source.Length > 1 && char.IsLowSurrogate(source[1])
                    ? 2
                    : 1;
                return source.Substring(0, length).ToUpperInvariant();
            }
        }

        /// <summary>占位色块：按包名取固定颜色，同一应用每次打开都一致。</summary>
        public Brush AvatarBrush
        {
            get
            {
                if (string.IsNullOrEmpty(PackageName)) return AvatarBrushes[0];

                int hash = 0;
                foreach (char c in PackageName)
                {
                    hash = (hash * 31 + c) & 0x7FFFFFFF;
                }
                return AvatarBrushes[hash % AvatarBrushes.Length];
            }
        }

        /// <summary>解析出的应用名，可能为空（系统包无 label 或解析失败）。</summary>
        public string DisplayName
        {
            get { return _displayName; }
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                Raise("DisplayName");
                Raise("Title");
                Raise("Initial");
            }
        }

        /// <summary>列表主标题：优先应用名，没有则显示包名。</summary>
        public string Title
        {
            get { return string.IsNullOrWhiteSpace(_displayName) ? PackageName : _displayName; }
        }

        public string TypeText
        {
            get { return IsSystem ? "系统" : "第三方"; }
        }

        /// <summary>系统服务：系统应用里无桌面入口、常驻（persistent）或位于 priv-app 的包，由解析结果异步回填。</summary>
        public bool IsService
        {
            get { return _isService; }
            set
            {
                if (_isService == value) return;
                _isService = value;
                Raise("IsService");
            }
        }

        /// <summary>名称是否已解析（含"确定无名称"）。为 true 时不再列入待解析队列，避免反复拉 arsc。</summary>
        public bool NameResolved
        {
            get { return _nameResolved; }
            set
            {
                if (_nameResolved == value) return;
                _nameResolved = value;
                Raise("NameResolved");
            }
        }

        /// <summary>该行信息是否来自本地缓存（而非本次实时解析），用于界面展示来源。</summary>
        public bool FromCache
        {
            get { return _fromCache; }
            set
            {
                if (_fromCache == value) return;
                _fromCache = value;
                Raise("FromCache");
            }
        }

        public bool IsDisabled
        {
            get { return _isDisabled; }
            set
            {
                if (_isDisabled == value) return;
                _isDisabled = value;
                Raise("IsDisabled");
                Raise("StatusText");
                Raise("ActionText");
                Raise("StatusBackground");
                Raise("StatusForeground");
            }
        }

        public string StatusText
        {
            get { return _isDisabled ? "已停用" : "已启用"; }
        }

        public string ActionText
        {
            get { return _isDisabled ? "启用" : "停用"; }
        }

        public Brush StatusBackground
        {
            get { return _isDisabled ? DisabledBackground : EnabledBackground; }
        }

        public Brush StatusForeground
        {
            get { return _isDisabled ? DisabledForeground : EnabledForeground; }
        }

        /// <summary>该行正在执行命令，按钮暂时禁用。</summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                Raise("IsBusy");
                Raise("IsNotBusy");
            }
        }

        public bool IsNotBusy
        {
            get { return !_isBusy; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush[] CreateAvatarBrushes()
        {
            return new[]
            {
                CreateBrush(0x60, 0xA5, 0xFA),
                CreateBrush(0x34, 0xD3, 0x99),
                CreateBrush(0xFB, 0x92, 0x3C),
                CreateBrush(0xA7, 0x8B, 0xFA),
                CreateBrush(0xF4, 0x72, 0xB6),
                CreateBrush(0x38, 0xBD, 0xF8),
                CreateBrush(0xFB, 0x71, 0x85),
                CreateBrush(0x2D, 0xD4, 0xBF)
            };
        }
    }
}
