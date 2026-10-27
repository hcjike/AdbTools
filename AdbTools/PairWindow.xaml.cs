using QRCoder;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// 配对窗口：同一界面内切换「配对码」与「扫码配对」两种方式。
    /// 扫码协议同 Android Studio：二维码载荷为 WIFI:T:ADB;S:实例名;P:配对码;;
    /// 手机扫码后以「实例名」广播 _adb-tls-pairing._tcp 服务，
    /// 借助自带 adb 的 mdns services 发现设备配对地址后执行 adb pair 完成配对。
    /// </summary>
    public partial class PairWindow : Window
    {
        public string AdbPath { get; set; }

        public string Address { get; set; }

        /// <summary>本次窗口是否完成配对（含扫码配对），供调用方刷新设备列表</summary>
        public bool PairSucceeded { get; private set; }

        /// <summary>手机扫码后广播的配对服务类型</summary>
        private const string PAIRING_SERVICE = "_adb-tls-pairing._tcp";

        /// <summary>配对完成后手机广播的连接服务类型</summary>
        private const string CONNECT_SERVICE = "_adb-tls-connect._tcp";

        /// <summary>轮询 mDNS 服务列表的间隔（毫秒）</summary>
        private const int POLL_INTERVAL_MS = 2000;

        /// <summary>单次 adb 命令超时（毫秒）</summary>
        private const int COMMAND_TIMEOUT_MS = 20000;

        /// <summary>配对失败最大重试次数（应对扫码瞬间服务未就绪的竞态）</summary>
        private const int MAX_PAIR_RETRY = 5;

        /// <summary>二维码中的实例名（S 字段），手机扫码后以此为名广播配对服务</summary>
        private string serviceName;

        /// <summary>配对码（P 字段），同时用于 adb pair</summary>
        private string pairPin;

        /// <summary>窗口关闭 / 切回配对码时标记，通知后台线程退出</summary>
        private volatile bool stopFlag;

        /// <summary>二维码代数：重新生成时递增，旧监听线程比对后代次退出</summary>
        private int generation;

        public PairWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Address))
            {
                this.deviceAddress.Text = Address;
            }
            // 默认选中「扫码配对」：加载即生成二维码并开始监听
            if (tabQr.IsChecked == true)
            {
                GenerateQr();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            stopFlag = true;
        }

        // ===== 方式切换 =====
        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            // XAML 解析阶段 tabQr 的 IsChecked="True" 会提前触发本事件，
            // 此时其它控件尚未创建，直接忽略；初始生成由 Window_Loaded 完成
            if (tabQr == null || tabCode == null || codePanel == null || qrPanel == null)
            {
                return;
            }
            // ToggleButton 无 GroupName，手动互斥
            bool qr = sender == tabQr;
            tabQr.IsChecked = qr;
            tabCode.IsChecked = !qr;
            codePanel.Visibility = qr ? Visibility.Collapsed : Visibility.Visible;
            qrPanel.Visibility = qr ? Visibility.Visible : Visibility.Collapsed;
            if (qr)
            {
                stopFlag = false;
                GenerateQr();
            }
            else
            {
                stopFlag = true;
            }
        }

        // ===== 配对码方式 =====
        private void pairCode_Click(object sender, RoutedEventArgs e)
        {
            string address = deviceAddress.Text.Replace("：", ":");
            deviceAddress.Text = address;
            string code = pairCode.Text;
            if (!Globals.IsValidIPEndPoint(address))
            {
                MessageBox.Show("设备配对地址输入错误！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("请输入配对码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            waitAnimation(true);
            // 第二条为配对码，由 adb pair 从stdin读取
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{AdbPath} pair {address}", code }, result =>
            {
                waitAnimation(false);
                if (Globals.ContainsAny(result, "Successfully"))
                {
                    PairSucceeded = true;
                    MessageBox.Show("设备配对成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                    this.Close();
                }
                else
                {
                    MessageBox.Show("设备配对失败！\r\n1.确认IP地址和端口是否处于配对状态；\r\n2.配对码输入是否正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, null);
        }

        // ===== 扫码方式 =====
        /// <summary>生成新的配对码与二维码，并重新开始监听</summary>
        private void GenerateQr()
        {
            Random random = new Random(Guid.NewGuid().GetHashCode());
            pairPin = random.Next(100000, 1000000).ToString();
            serviceName = $"adbtools-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            pairCodeText.Text = pairPin;
            qrImage.Source = RenderQr($"WIFI:T:ADB;S:{serviceName};P:{pairPin};;");
            statusText.Text = "等待手机扫码…";
            StartPoll();
        }

        private void regenerate_Click(object sender, RoutedEventArgs e)
        {
            GenerateQr();
        }

        /// <summary>渲染二维码为 PNG 位图</summary>
        private BitmapImage RenderQr(string payload)
        {
            using (QRCodeGenerator generator = new QRCodeGenerator())
            using (QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M))
            using (PngByteQRCode qr = new PngByteQRCode(data))
            {
                byte[] png = qr.GetGraphic(8);
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        /// <summary>启动监听线程（每代二维码独立线程，旧线程经代次比对自动退出）</summary>
        private void StartPoll()
        {
            int gen = ++generation;
            new Thread(() => PollLoop(gen)) { IsBackground = true }.Start();
        }

        /// <summary>轮询发现配对服务并自动执行 adb pair</summary>
        private void PollLoop(int gen)
        {
            int retries = 0;
            DateTime lastHint = DateTime.Now;
            while (!stopFlag && !PairSucceeded && gen == generation)
            {
                string address = FindService(PAIRING_SERVICE, serviceName);
                if (address != null)
                {
                    // 出现同名配对服务，说明手机已完成扫码，直接配对
                    SetStatus($"已发现设备 {address}，正在配对…");
                    string output = ExecuteAdb($"pair {address} {pairPin}");
                    if (stopFlag || gen != generation)
                    {
                        return;
                    }
                    if (Globals.ContainsAny(output, "Successfully"))
                    {
                        PairSucceeded = true;
                        OnPairSuccess(address);
                        return;
                    }
                    retries++;
                    if (retries >= MAX_PAIR_RETRY)
                    {
                        SetStatus("配对失败！请点击「重新生成二维码」后重新扫描，或返回使用输入配对码方式。");
                        return;
                    }
                }
                else if ((DateTime.Now - lastHint).TotalSeconds > 30)
                {
                    SetStatus("仍未发现设备：请确认 ①手机与电脑同一 Wi-Fi；②手机停留在「使用二维码配对设备」界面；③电脑防火墙未拦截 mDNS(5353/UDP)。");
                    lastHint = DateTime.Now;
                }
                Thread.Sleep(POLL_INTERVAL_MS);
            }
        }

        /// <summary>配对成功后立即反馈用户，自动连接在后台异步进行，不阻塞"配对成功"提示</summary>
        private void OnPairSuccess(string pairAddress)
        {
            PairSucceeded = true;
            string ip = pairAddress.Substring(0, pairAddress.LastIndexOf(':'));

            // 自动连接放到后台线程，最多尝试 3 次；即使窗口已关闭也静默推进，不阻塞 UI
            new Thread(() => AutoConnect(ip)) { IsBackground = true }.Start();

            Dispatcher.Invoke(new Action(() =>
            {
                if (stopFlag)
                {
                    return;
                }
                MessageBox.Show("设备配对成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                Close();
            }));
        }

        /// <summary>后台自动连接已配对设备（尽力而为，失败不影响配对结果）</summary>
        private void AutoConnect(string ip)
        {
            // 不依赖 stopFlag：窗口关闭后仍需完成有限的几次连接尝试，线程独立运行、自然结束
            for (int i = 0; i < 3; i++)
            {
                string connectAddress = FindService(CONNECT_SERVICE, null, ip);
                if (connectAddress != null)
                {
                    ExecuteAdb($"connect {connectAddress}");
                    return;
                }
                Thread.Sleep(POLL_INTERVAL_MS);
            }
        }

        /// <summary>
        /// 查询 adb 的 mDNS 服务列表并筛选目标地址
        /// 输出格式：实例名 + 服务类型 + IP:端口（制表符分隔）
        /// </summary>
        /// <param name="serviceType">服务类型，如 _adb-tls-pairing._tcp</param>
        /// <param name="instanceName">实例名精确匹配，null 时不过滤</param>
        /// <param name="sameIp">限定与该 IP 同主机的服务，null 时不过滤</param>
        private string FindService(string serviceType, string instanceName, string sameIp = null)
        {
            string output = ExecuteAdb("mdns services", 8000);
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains(serviceType))
                {
                    continue;
                }
                string[] tokens = line.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    continue;
                }
                if (instanceName != null && !tokens[0].Equals(instanceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string endpoint = tokens[tokens.Length - 1];
                int colon = endpoint.LastIndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                if (sameIp != null && !endpoint.Substring(0, colon).Equals(sameIp, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!Globals.IsValidIPEndPoint(endpoint))
                {
                    continue;
                }
                return endpoint;
            }
            return null;
        }

        /// <summary>直接执行 adb 命令并返回输出（超时强杀，异常返回空串）</summary>
        private string ExecuteAdb(string arguments, int timeoutMs = COMMAND_TIMEOUT_MS)
        {
            try
            {
                using (Process process = new Process())
                {
                    // AdbPath 由调用方传入，可能带双引号（如 "adb.exe"），Process.FileName 不能带引号
                    process.StartInfo.FileName = AdbPath.Trim('"');
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        process.Kill();
                        return string.Empty;
                    }
                    return process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>更新状态提示（后台线程安全）</summary>
        private void SetStatus(string text)
        {
            if (stopFlag)
            {
                return;
            }
            try
            {
                Dispatcher.Invoke(new Action(() => { statusText.Text = text; }));
            }
            catch (Exception)
            {
                // 窗口关闭或应用退出时的竞态，忽略
            }
        }

        private void waitAnimation(bool show)
        {
            processBarGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            waitProcessBar.IsShowAnimation = show;
        }

        /// <summary>在输入框中按 Enter 直接走「配对码」主路径，无需切换</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (deviceAddress.IsFocused || pairCode.IsFocused))
            {
                e.Handled = true;
                pairCode_Click(null, null);
            }
        }
    }
}
