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

        private string _displayName;
        private bool _isDisabled;
        private bool _isBusy;

        public string PackageName { get; set; }

        public string ApkPath { get; set; }

        public bool IsSystem { get; set; }

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
    }
}
