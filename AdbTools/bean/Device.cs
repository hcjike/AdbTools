using System;
using System.ComponentModel;
using System.Windows.Media;

namespace AdbTools.bean
{
    public class Device : INotifyPropertyChanged
    {
        /// <summary>
        /// 品牌型号未读取到时列表显示的默认名称
        /// </summary>
        public const string UnknownName = "unknown";

        private static readonly Brush WifiBrush = CreateBrush(0x10, 0xB9, 0x81);
        private static readonly Brush UsbBrush = CreateBrush(0xF5, 0x9E, 0x0B);

        public string DeviceMark { get; set; }

        public string DeviceBrand { get; set; }

        public string DeviceModel { get; set; }

        /// <summary>
        /// 硬件序列号（ro.serialno，取不到时用 ro.boot.serialno）。
        /// 同一台手机用 USB / IP / 无线连接时相同，用于按设备去重与缓存；
        /// 读取随品牌型号一起完成，打开应用列表时可直接使用，不必再发命令。
        /// </summary>
        public string HardwareKey { get; set; }

        private int _sdkVersion;

        /// <summary>
        /// 系统 SDK 版本（ro.build.version.sdk，如 Android 7.0 为 24）。
        /// 随品牌型号一起读取，用于安装前校验 APK 的最低 SDK 要求；0 表示未取到。
        /// </summary>
        public int SdkVersion
        {
            get { return _sdkVersion; }
            set
            {
                if (_sdkVersion == value) return;
                _sdkVersion = value;
                OnPropertyChanged(nameof(SdkVersion));
                OnPropertyChanged(nameof(SdkText));
            }
        }

        /// <summary>设备卡片上的 SDK 徽标文案。</summary>
        public string SdkText
        {
            get { return _sdkVersion > 0 ? "SDK " + _sdkVersion : null; }
        }

        /// <summary>序列号是否已尝试读取过：失败时不反复查询，避免定时刷新持续发命令。</summary>
        private bool _keyQueried;

        public bool IsWifiConnect { get; set; }

        public string ShowDeviceName { get; set; }

        /// <summary>adb -s 参数用的、带双引号的序列号，避免序列号含空格（mDNS/TLS 设备）时命令被截断。</summary>
        public string SerialArg => AdbTools.Globals.QuoteSerial(DeviceMark);

        public string ConnectWay { get; set; }

        public Brush ConnectWayColor { get; set; }

        public string DeviceInfo { get; set; }

        /// <summary>
        /// 第二行（序列号）是否显示：与首行内容相同时隐藏，避免重复
        /// </summary>
        public bool DeviceMarkVisible { get; set; }


        /// <summary>
        /// 刷新设备信息（品牌、型号、硬件序列号缺失时才查询，查询为异步，完成后自动更新界面）
        /// </summary>
        public void Refresh()
        {
            UpdateShow();

            bool needBrand = string.IsNullOrWhiteSpace(DeviceBrand);
            bool needModel = string.IsNullOrWhiteSpace(DeviceModel);
            // 序列号只查一次：个别设备取不到时不反复尝试，避免定时刷新持续发命令
            bool needKey = string.IsNullOrWhiteSpace(HardwareKey) && !_keyQueried;
            bool needSdk = SdkVersion <= 0;

            if (needBrand || needModel || needKey || needSdk)
            {
                QueryDeviceProps(needBrand, needModel, needKey, needSdk);
            }
        }

        /// <summary>
        /// 一次取回品牌、型号、硬件序列号与系统 SDK：都合并在同一条 shell 命令里，
        /// 比分开查省几次 adb 往返。输出用 @@B / @@M / @@S / @@K / @@V 分段，
        /// 某个属性为空时也不会张冠李戴。
        /// </summary>
        private void QueryDeviceProps(bool needBrand, bool needModel, bool needKey, bool needSdk)
        {
            if (needKey)
            {
                _keyQueried = true;
            }

            string command = MainWindow.adbPath + " -s " + SerialArg
                + " shell \"echo @@B; getprop ro.product.brand; echo @@M; getprop ro.product.model"
                + "; echo @@S; getprop ro.serialno; echo @@K; getprop ro.boot.serialno"
                + "; echo @@V; getprop ro.build.version.sdk\"";

            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { command }, result =>
            {
                // 回调已在UI线程，可直接更新；解析失败时保留原值，避免名字被清空
                string brand;
                string model;
                string serial;
                string bootSerial;
                int sdk;
                ParseDeviceProps(result, out brand, out model, out serial, out bootSerial, out sdk);

                bool changed = false;
                if (needBrand && !string.IsNullOrWhiteSpace(brand))
                {
                    DeviceBrand = brand;
                    changed = true;
                }
                if (needModel && !string.IsNullOrWhiteSpace(model))
                {
                    DeviceModel = model;
                    changed = true;
                }

                if (needKey)
                {
                    string key = PickHardwareKey(serial, bootSerial);
                    if (key != null)
                    {
                        HardwareKey = key;
                    }
                }

                if (needSdk && sdk > 0)
                {
                    SdkVersion = sdk;
                }

                if (changed)
                {
                    UpdateShow();
                }
            },
            err => true, 15);
        }

        /// <summary>硬件序列号优先取 ro.serialno，其次 ro.boot.serialno；占位值与错误文本都跳过。</summary>
        private static string PickHardwareKey(string serial, string bootSerial)
        {
            if (AppDeviceKey.IsUsable(serial))
            {
                return AppDeviceKey.Normalize(serial);
            }
            if (AppDeviceKey.IsUsable(bootSerial))
            {
                return AppDeviceKey.Normalize(bootSerial);
            }
            return null;
        }

        /// <summary>
        /// 解析 "echo @@B; getprop brand; echo @@M; getprop model; echo @@S; ..." 的输出：
        /// 跳过命令回显行，按分段标记取值，各段取最后一个有效行。
        /// </summary>
        private static void ParseDeviceProps(string result, out string brand, out string model,
            out string serial, out string bootSerial, out int sdk)
        {
            brand = null;
            model = null;
            serial = null;
            bootSerial = null;
            sdk = 0;
            if (string.IsNullOrEmpty(result))
            {
                return;
            }

            string section = null;
            foreach (string raw in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                // 命令回显行里同时含 shell 与 getprop，不能当成分段标记
                if (line.IndexOf("shell", StringComparison.OrdinalIgnoreCase) >= 0
                    && line.IndexOf("getprop", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (line == "@@B") { section = "b"; continue; }
                if (line == "@@M") { section = "m"; continue; }
                if (line == "@@S") { section = "s"; continue; }
                if (line == "@@K") { section = "k"; continue; }
                if (line == "@@V") { section = "v"; continue; }

                // 提示符行与 adb 错误信息都不是属性值
                if (line.Contains(">") || LooksLikeError(line)) continue;

                if (section == "b") brand = line;
                else if (section == "m") model = line;
                else if (section == "s") serial = line;
                else if (section == "k") bootSerial = line;
                else if (section == "v")
                {
                    int value;
                    if (int.TryParse(line, out value)) sdk = value;
                }
            }
        }

        /// <summary>
        /// 设备掉线或未授权时，adb 会把错误写进输出（如 "adb.exe: device 'xxx' not found"），
        /// 这类文本不能当作属性值，否则设备名会变成错误信息。
        /// </summary>
        private static bool LooksLikeError(string value)
        {
            return value.IndexOf("adb.exe:", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateShow()
        {
            string info = $"{DeviceBrand} {DeviceModel}".Trim();
            // 品牌型号未取到时先显示默认名称，读取完成后由QueryProp回调再次刷新
            DeviceInfo = string.IsNullOrEmpty(info) ? UnknownName : info;
            // 两行内容相同时隐藏第二行，避免重复显示
            DeviceMarkVisible = !string.Equals(DeviceInfo, DeviceMark, StringComparison.Ordinal);

            ConnectWay = IsWifiConnect ? "WiFi" : "USB";
            ConnectWayColor = IsWifiConnect ? WifiBrush : UsbBrush;

            ShowDeviceName = DeviceMarkVisible ? $"{DeviceInfo} {ConnectWay} ({DeviceMark})".Trim() : $"{DeviceInfo} {ConnectWay}".Trim();
            OnPropertyChanged(string.Empty);
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName = null)
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
