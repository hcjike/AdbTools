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

        public string DeviceMark { get; set; }

        public string DeviceBrand { get; set; }

        public string DeviceModel { get; set; }

        public bool IsWifiConnect { get; set; }

        public string ShowDeviceName { get; set; }

        public string ConnectWay { get; set; }

        public Brush ConnectWayColor { get; set; }

        public string DeviceInfo { get; set; }

        /// <summary>
        /// 第二行（序列号）是否显示：与首行内容相同时隐藏，避免重复
        /// </summary>
        public bool DeviceMarkVisible { get; set; }


        /// <summary>
        /// 刷新设备信息（品牌、型号缺失时才查询，查询为异步，完成后自动更新界面）
        /// </summary>
        public void Refresh()
        {
            UpdateShow();

            if (string.IsNullOrWhiteSpace(DeviceBrand))
            {
                QueryProp("ro.product.brand", value => DeviceBrand = value);
            }
            if (string.IsNullOrWhiteSpace(DeviceModel))
            {
                QueryProp("ro.product.model", value => DeviceModel = value);
            }
        }

        /// <summary>
        /// 异步查询系统属性
        /// </summary>
        private void QueryProp(string prop, Action<string> setter)
        {
            CmdExecutor.ExecuteCommandAndReturnAsync($"{MainWindow.adbPath} -s {DeviceMark} shell getprop {prop}", result =>
            {
                // 回调已在UI线程，可直接更新；解析失败时保留原值，避免名字被清空
                string value = ParsePropValue(result, prop);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    setter(value);
                    UpdateShow();
                }
            });
        }

        /// <summary>
        /// 从 cmd 回显的输出中解析 getprop 的结果（取命令回显行的下一行）
        /// </summary>
        private static string ParsePropValue(string result, string prop)
        {
            if (string.IsNullOrEmpty(result))
            {
                return string.Empty;
            }

            // 必须用RemoveEmptyEntries：\r\n会被拆成两个分隔符，
            // 保留空行会导致每个值前面多一个空行，取不到正确内容
            string[] lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (lines[i].Contains($"shell getprop {prop}"))
                {
                    // 跳过 cmd 提示符行（如 “C:\>exit”），防止取到错误内容
                    string value = lines[i + 1].Trim();
                    if (!value.Contains(">"))
                    {
                        return value;
                    }
                }
            }
            return string.Empty;
        }

        private void UpdateShow()
        {
            string info = $"{DeviceBrand} {DeviceModel}".Trim();
            // 品牌型号未取到时先显示默认名称，读取完成后由QueryProp回调再次刷新
            DeviceInfo = string.IsNullOrEmpty(info) ? UnknownName : info;
            // 两行内容相同时隐藏第二行，避免重复显示
            DeviceMarkVisible = !string.Equals(DeviceInfo, DeviceMark, StringComparison.Ordinal);

            ConnectWay = IsWifiConnect ? "WiFi" : "USB";
            ConnectWayColor = IsWifiConnect ? Brushes.Green : Brushes.Orange;

            ShowDeviceName = DeviceMarkVisible ? $"{DeviceInfo} {ConnectWay} ({DeviceMark})".Trim() : $"{DeviceInfo} {ConnectWay}".Trim();
            OnPropertyChanged(string.Empty);
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


    }
}
