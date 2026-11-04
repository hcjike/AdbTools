using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// scrcpy 取流后端：把随包的 scrcpy-server 推到设备、以 app_process 启动，
    /// 通过 adb forward 连上设备端 localabstract socket，以 raw_stream 模式读取
    /// MediaCodec 主动编码输出的 H.264 裸流（Annex-B），直接喂给 AccessUnitSplitter。
    /// screenrecord 是变化驱动的录屏工具，静止画面不产帧；MediaCodec 主动编码
    /// 可指定码率与尺寸，在 TV 机顶盒等低端设备上出帧节奏更稳定。
    /// 若设备不支持 app_process，连续两次连不上就自动回退到 screenrecord 后端。
    /// </summary>
    internal sealed class ScrcpyVideoStream : IVideoStream
    {
        private const string ServerVersion = "5.0.1";
        private const string ServerFileName = "scrcpy-server";
        private const string DeviceServerPath = "/data/local/tmp/adbtools-scrcpy-server.jar";

        /// <summary>
        /// 采集帧率上限。不限帧时 120Hz 设备会按 120fps 出流，
        /// 而 UI 线程每帧都要做大分辨率像素拷贝，跟不上反而更卡。
        /// </summary>
        private const int MaxFps = 60;

        private readonly string _adbPath;
        private readonly string _serial;
        private readonly string _serverPath;
        private readonly object _sync = new object();

        private volatile string _size;
        private volatile int _bitRate;
        private volatile bool _stopped;
        private volatile bool _restartRequested;
        private volatile bool _paused;
        private volatile bool _suppressWatchdog;

        private Process _serverProcess;
        private TcpClient _client;
        private int _forwardPort = -1;
        private int _scid;

        /// <summary>控制通道：与视频通道同端口的第二次连接，可用时输入注入直接走 scrcpy 协议。</summary>
        private TcpClient _controlClient;
        private NetworkStream _controlStream;
        private readonly object _controlWriteLock = new object();
        private volatile bool _controlReady;

        /// <summary>服务端提供不了控制通道时置位，后续片段改用纯视频模式（control=false）。</summary>
        private volatile bool _controlDisabled;

        /// <summary>server 进程已输出 Device 信息的标记（说明 app_process 已启动）。</summary>
        private volatile int _serverReady;

        /// <summary>本档位是否已上报过"编码器不支持当前档位"，避免同一档位连续降多档。</summary>
        private int _incompatibleReported;

        /// <summary>server 已报编码错误，等首批数据读到后再上报，避免降档打断正在建立的连接。</summary>
        private int _incompatiblePending;

        private AdbVideoStream _fallback;
        private int _consecutiveFailures;

        public event Action<string> Log;

        public int SegmentCount { get; private set; }

        public ScrcpyVideoStream(string adbPath, string serial, string size, int bitRate)
        {
            _adbPath = adbPath;
            _serial = serial;
            _size = size;
            _bitRate = bitRate;
            _serverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ServerFileName);
        }

        public string Size
        {
            get { return _size; }
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                _size = value;
                AdbVideoStream f = _fallback;
                if (f != null) f.Size = value;
            }
        }

        public int BitRate
        {
            get { return _bitRate; }
            set
            {
                if (value <= 0) return;
                _bitRate = value;
                AdbVideoStream f = _fallback;
                if (f != null) f.BitRate = value;
            }
        }

        public bool Paused
        {
            get { return _paused; }
            set
            {
                _paused = value;
                AdbVideoStream f = _fallback;
                if (f != null) f.Paused = value;
            }
        }

        public bool SuppressWatchdog
        {
            get { return _suppressWatchdog; }
            set
            {
                _suppressWatchdog = value;
                AdbVideoStream f = _fallback;
                if (f != null) f.SuppressWatchdog = value;
            }
        }

        public void Restart()
        {
            _restartRequested = true;
            // 切换档位意味着要用新参数重新验证一次，允许再次上报「档位不兼容」；
            // 同一档位内因断流/看门狗自动重连则不上报，避免连环降档。
            Interlocked.Exchange(ref _incompatibleReported, 0);
            Interlocked.Exchange(ref _incompatiblePending, 0);
            CloseCurrent();
            AdbVideoStream f = _fallback;
            if (f != null) f.Restart();
        }

        public void Stop()
        {
            _stopped = true;
            CloseCurrent();
            AdbVideoStream f = _fallback;
            if (f != null) f.Stop();
        }

        public void Dispose()
        {
            Stop();
            AdbVideoStream f = _fallback;
            if (f != null) f.Dispose();
        }

        /// <summary>取流前唤醒、解锁并保持常亮（server 自身也会 power_on，这里做双保险）。</summary>
        public void WakeUpDevice()
        {
            RunAdb("shell \"input keyevent 224; wm dismiss-keyguard; svc power stayon true\"");
            Thread.Sleep(300);
        }

        /// <summary>窗口关闭时清理设备端 server 与 adb 转发。</summary>
        public void ReleaseDevice()
        {
            CloseCurrent();
            RunAdb("shell pkill -f com.genymobile.scrcpy.Server");
            AdbVideoStream f = _fallback;
            if (f != null) f.ReleaseDevice();
        }

        /// <summary>阻塞读取；片段结束自动重启，连续失败则整体回退到 screenrecord。</summary>
        public void Pump(Action<byte[], int> onData)
        {
            while (!_stopped)
            {
                if (_fallback != null)
                {
                    _fallback.Pump(onData);
                    return;
                }

                if (_paused)
                {
                    Thread.Sleep(200);
                    continue;
                }

                _restartRequested = false;
                SegmentCount++;
                DateTime segmentStarted = DateTime.UtcNow;
                long totalBytes = 0;
                bool connected = false;
                Thread watchdog = null;
                int[] watchdogStop = null;
                var heartbeat = Stopwatch.StartNew();

                var lastDataTick = new long[] { Environment.TickCount };
                int receivedFlag = 0;

                try
                {
                    int port = PrepareSegment();
                    if (port > 0)
                    {
                        Log?.Invoke("取流片段 #" + SegmentCount + " 已启动");

                        byte[] buffer = new byte[64 * 1024];
                        NetworkStream stream = null;

                        // server 只 accept 一个 video 连接：反复 Connect 会把它的连接搞断，
                        // 因此等 app_process 启动、bind 好 localabstract socket 后只连一次。
                        for (int i = 0; i < 30 && !_stopped && _serverReady == 0; i++) Thread.Sleep(100);
                        if (!_stopped) Thread.Sleep(300);

                        TcpClient client = null;
                        try
                        {
                            client = new TcpClient();
                            client.Connect(IPAddress.Loopback, port);
                            NetworkStream ns = client.GetStream();

                            // server 的 DesktopConnection.open 按 video → control 顺序把所有通道
                            // accept 完才开始推流：控制连接必须在这里建立，
                            // 否则 server 卡在 accept，客户端永远等不到首帧。
                            if (!OpenControl(port))
                            {
                                // 设备端给不了控制通道：此时 server 仍在等第二条连接，
                                // 主动断开让本片段立刻结束，下一片段改用 control=false 纯视频模式
                                _restartRequested = true;
                                try { client.Close(); } catch { }
                                throw new InvalidOperationException("控制通道不可用，改用纯视频通道重试");
                            }

                            // 编码器初始化可能要几秒，首批数据给足超时；
                            ns.ReadTimeout = 8000;
                            int first = ns.Read(buffer, 0, buffer.Length);
                            if (first > 0)
                            {
                                // 后续读取不限时：屏幕静止本来就不产帧，
                                // 由看门狗按 60 秒阈值处理，不能靠 socket 超时误判断流。
                                ns.ReadTimeout = Timeout.Infinite;
                                SetClient(client);
                                stream = ns;
                                Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount);
                                Interlocked.Exchange(ref receivedFlag, 1);
                                totalBytes += first;
                                onData(buffer, first);
                            }
                            else
                            {
                                Log?.Invoke("scrcpy 首次读取返回 " + first.ToString(CultureInfo.InvariantCulture));
                                client.Close();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log?.Invoke("scrcpy 首次读取失败：" + ex.Message);
                            if (client != null) { try { client.Close(); } catch { } }
                        }

                        if (stream != null)
                        {
                            connected = true;
                            // 连接已稳定读到首批数据，此时上报才不会打断连接建立
                            ReportIncompatibility();
                            watchdogStop = new int[1];
                            int[] stopFlag = watchdogStop;

                            watchdog = new Thread(() =>
                            {
                                while (!_stopped && Volatile.Read(ref stopFlag[0]) == 0)
                                {
                                    Thread.Sleep(200);
                                    int timeout = Volatile.Read(ref receivedFlag) == 0
                                        ? (SegmentCount <= 1 ? 8000 : 4000)
                                        : 60000;
                                    if (_suppressWatchdog)
                                    {
                                        Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount);
                                        continue;
                                    }
                                    if (unchecked(Environment.TickCount - Interlocked.Read(ref lastDataTick[0])) > timeout)
                                    {
                                        Log?.Invoke("取流停滞（" + timeout + "ms 无数据），正在重连…");
                                        CloseCurrent();
                                        return;
                                    }
                                }
                            }) { IsBackground = true, Name = "scrcpy-watchdog" };
                            watchdog.Start();

                            int read;
                            while (!_stopped && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                Interlocked.Exchange(ref lastDataTick[0], Environment.TickCount);
                                Interlocked.Exchange(ref receivedFlag, 1);
                                totalBytes += read;
                                onData(buffer, read);

                                if (heartbeat.ElapsedMilliseconds >= 2000)
                                {
                                    Log?.Invoke("已接收 " + (totalBytes / 1024) + " KB");
                                    heartbeat.Restart();
                                }
                            }
                        }
                    }

                    if (!connected)
                    {
                        if (_restartRequested)
                        {
                            // 主动切换档位/旋转打断的片段，不算设备故障
                            Log?.Invoke("scrcpy 取流已按新参数重启");
                            _consecutiveFailures = 0;
                        }
                        else
                        {
                            _consecutiveFailures++;
                            Log?.Invoke("scrcpy 连接失败（第 " + _consecutiveFailures + " 次）");
                            if (_consecutiveFailures >= 2)
                            {
                                Log?.Invoke("设备不支持 scrcpy server，已回退到 screenrecord 取流");
                                SwitchToFallback();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // CloseCurrent / Stop 主动关闭 socket 时 Read 会抛异常，属于正常收尾，不报错误
                    if (!_stopped && _client != null) Log?.Invoke("scrcpy 取流异常：" + ex.Message);
                }
                finally
                {
                    if (watchdogStop != null) Interlocked.Exchange(ref watchdogStop[0], 1);
                    if (watchdog != null)
                    {
                        try { watchdog.Join(600); } catch { }
                    }
                    CloseCurrent();
                }

                if (_stopped) break;
                if (_fallback != null) continue;

                // 主动 Restart 打断的片段不算"取流失败"，否则会额外触发一次降档
                if (!_stopped && !_paused && connected && !_restartRequested)
                {
                    if (totalBytes < 8192)
                    {
                        Log?.Invoke("取流失败：本片段仅收到 " + totalBytes + " 字节（用时 "
                            + (int)heartbeat.Elapsed.TotalMilliseconds + "ms）");
                    }
                    else
                    {
                        Log?.Invoke("取流结束，准备重连");
                        _consecutiveFailures = 0;
                    }
                }

                if (_restartRequested) continue;
                Thread.Sleep((DateTime.UtcNow - segmentStarted).TotalMilliseconds < 1000 ? 1200 : 300);
            }
        }

        /// <summary>准备一次取流片段：push server、建 adb 转发、启动 server；返回本地端口，失败返回 -1。</summary>
        private int PrepareSegment()
        {
            if (!File.Exists(_serverPath))
            {
                Log?.Invoke("缺少 " + ServerFileName + "，无法启用 scrcpy 取流");
                return -1;
            }

            _serverReady = 0;

            // 每个片段换一个 scid，避免上一次残留的 server 占用同名 localabstract socket
            _scid = NewScid();
            string socketName = "scrcpy_" + _scid.ToString("x8", CultureInfo.InvariantCulture);

            int port = GetFreeTcpPort();
            string forwardResult = RunAdb("forward tcp:" + port.ToString(CultureInfo.InvariantCulture)
                + " localabstract:" + socketName) ?? string.Empty;
            if (forwardResult.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || forwardResult.IndexOf("more than one", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log?.Invoke("建立 adb 转发失败：" + forwardResult.Trim());
                return -1;
            }
            _forwardPort = port;

            if (!PushServer())
            {
                Log?.Invoke("推送 scrcpy-server 失败");
                return -1;
            }

            int maxSize = MaxSide();
            var sb = new StringBuilder();
            sb.Append("-s ").Append(AdbTools.Globals.QuoteSerial(_serial)).Append(" shell \"CLASSPATH=").Append(DeviceServerPath);
            sb.Append(" app_process / com.genymobile.scrcpy.Server ").Append(ServerVersion);
            sb.Append(" scid=").Append(_scid.ToString("x8", CultureInfo.InvariantCulture));
            sb.Append(" log_level=info tunnel_forward=true audio=false control=");
            sb.Append(_controlDisabled ? "false" : "true");
            sb.Append(" cleanup=false raw_stream=true");
            if (maxSize > 0) sb.Append(" max_size=").Append(maxSize.ToString(CultureInfo.InvariantCulture));
            if (_bitRate > 0) sb.Append(" video_bit_rate=").Append(_bitRate.ToString(CultureInfo.InvariantCulture));
            sb.Append(" max_fps=").Append(MaxFps.ToString(CultureInfo.InvariantCulture));
            sb.Append('"');

            var psi = new ProcessStartInfo(_adbPath, sb.ToString())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var server = new Process { StartInfo = psi };
            server.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                Log?.Invoke("scrcpy: " + e.Data);

                // scrcpy 的 downsize_on_error 会自己缩小尺寸继续出流，不会报"取流失败"，
                // 上层因此不会降档；但降低 max_size 可让编码器原生支持、画面更清晰。
                // 只先记下待上报：此刻连接可能还没读到首批数据，若立即通知上层降档，
                // 其 Restart 会打断正在建立的连接，反而被误判成"连接失败"回退到 screenrecord。
                if (e.Data.IndexOf("Capture/encoding error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Interlocked.Exchange(ref _incompatiblePending, 1);
                }
            };
            server.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                // Device 日志出现在 app_process 启动之后，用作"可以连接"的信号
                if (e.Data.IndexOf("Device:", StringComparison.OrdinalIgnoreCase) >= 0) _serverReady = 1;
                Log?.Invoke("scrcpy: " + e.Data);
            };
            try
            {
                server.Start();
                server.BeginErrorReadLine();
                server.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                Log?.Invoke("启动 scrcpy server 失败：" + ex.Message);
                try { server.Dispose(); } catch { }
                return -1;
            }
            lock (_sync) _serverProcess = server;
            return port;
        }

        private bool PushServer()
        {
            string outText = RunAdb("push \"" + _serverPath + "\" " + DeviceServerPath);
            // adb push 成功时输出里含文件名；失败时会输出 error
            return outText == null || outText.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>连接稳定后才把挂起的"档位不兼容"报上去（每个档位只报一次）。</summary>
        private void ReportIncompatibility()
        {
            if (Interlocked.Exchange(ref _incompatiblePending, 0) == 1
                && Interlocked.Exchange(ref _incompatibleReported, 1) == 0)
            {
                Log?.Invoke("档位不兼容：设备编码器不支持当前采集尺寸（server 已自动降级）");
            }
        }

        /// <summary>保存当前 socket（与 CloseCurrent 共用锁，避免关闭时竞态）。</summary>
        private void SetClient(TcpClient client)
        {
            lock (_sync) _client = client;
        }

        /// <summary>
        /// 控制通道是否可用。可用时触摸/按键直接走 scrcpy 协议，
        /// 每次注入只有一次本地 TCP 写入，而不是启动一次 adb shell + Java 进程。
        /// </summary>
        public bool ControlAvailable
        {
            get { return _controlReady; }
        }

        /// <summary>
        /// 建立控制通道：server 在 tunnel 模式下按 video → control 的顺序 accept，
        /// 视频连好后再连一次同一转发端口即可。
        /// </summary>
        private bool OpenControl(int port)
        {
            try
            {
                var client = new TcpClient { NoDelay = true, SendTimeout = 300 };
                IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2000))
                {
                    try { client.Close(); } catch { }
                    _controlDisabled = true;
                    Log?.Invoke("scrcpy 控制通道连接超时，改用纯视频通道重试");
                    return false;
                }
                client.EndConnect(ar);

                NetworkStream ns = client.GetStream();
                lock (_sync)
                {
                    _controlClient = client;
                    _controlStream = ns;
                }
                _controlReady = true;
                Log?.Invoke("scrcpy 控制通道已就绪（实时输入）");
                return true;
            }
            catch (Exception ex)
            {
                _controlReady = false;
                _controlDisabled = true;
                Log?.Invoke("scrcpy 控制通道不可用，改用纯视频通道重试：" + ex.Message);
                return false;
            }
        }

        /// <summary>关闭控制通道：片段重启时同步关闭，避免向已结束的 server 写数据。</summary>
        private void CloseControl()
        {
            TcpClient client;
            NetworkStream stream;
            lock (_sync)
            {
                stream = _controlStream;
                client = _controlClient;
                _controlStream = null;
                _controlClient = null;
            }
            _controlReady = false;
            if (stream != null) { try { stream.Dispose(); } catch { } }
            if (client != null) { try { client.Close(); } catch { } }
        }

        /// <summary>
        /// 注入一次触摸事件（scrcpy 控制协议 type=2，32 字节）。
        /// action：0=按下、1=抬起、2=移动。屏幕尺寸必须与设备端 videoSize 完全一致，
        /// 否则 server 的 PositionMapper 会直接丢弃该事件。
        /// </summary>
        public bool SendTouch(int action, int x, int y, int screenWidth, int screenHeight)
        {
            if (!_controlReady || screenWidth <= 0 || screenHeight <= 0) return false;

            var buf = new byte[32];
            buf[0] = 2;                                                   // INJECT_TOUCH_EVENT
            buf[1] = (byte)action;                                        // AMOTION_EVENT_ACTION_*
            WriteU64BE(buf, 2, unchecked((ulong)-2L));                    // SC_POINTER_ID_GENERIC_FINGER
            WriteI32BE(buf, 10, x);
            WriteI32BE(buf, 14, y);
            WriteU16BE(buf, 18, screenWidth);
            WriteU16BE(buf, 20, screenHeight);
            // pressure 是 0~1 的定点数：按下/移动为满值，抬起为 0
            WriteU16BE(buf, 22, action == 1 ? 0 : 0xFFFF);
            WriteU32BE(buf, 24, 0);                                       // action_button
            WriteU32BE(buf, 28, 0);                                       // buttons
            return SendControl(buf);
        }

        /// <summary>
        /// 注入一次滚轮（scrcpy 控制协议 type=3，v2.0+ 带 buttons，共 21 字节）。
        /// scrollX/scrollY 为定点数：约 32767 表示一格滚轮，正负表示方向。
        /// 走控制通道即时写入，不会像 adb shell 的 input swipe 那样阻塞排队。
        /// </summary>
        public bool SendScroll(int x, int y, int screenWidth, int screenHeight, int scrollX, int scrollY)
        {
            if (!_controlReady || screenWidth <= 0 || screenHeight <= 0) return false;

            var buf = new byte[21];
            buf[0] = 3;                                                   // INJECT_SCROLL_EVENT
            WriteI32BE(buf, 1, x);
            WriteI32BE(buf, 5, y);
            WriteU16BE(buf, 9, screenWidth);
            WriteU16BE(buf, 11, screenHeight);
            WriteI16BE(buf, 13, scrollX);
            WriteI16BE(buf, 15, scrollY);
            WriteU32BE(buf, 17, 0);                                       // buttons
            return SendControl(buf);
        }

        /// <summary>
        /// 注入一次按键（scrcpy 控制协议 type=0，14 字节）。
        /// action：0=按下、1=抬起；repeat 为重复计数（按住时递增，设备据此识别为长按重复）。
        /// </summary>
        public bool SendKey(int action, int keyCode, int repeat = 0)
        {
            if (!_controlReady) return false;

            var buf = new byte[14];
            buf[0] = 0;                                                   // INJECT_KEYCODE
            buf[1] = (byte)action;
            WriteU32BE(buf, 2, unchecked((uint)keyCode));
            WriteU32BE(buf, 6, unchecked((uint)repeat));
            WriteU32BE(buf, 10, 0);                                       // meta state
            return SendControl(buf);
        }

        private bool SendControl(byte[] data)
        {
            NetworkStream stream;
            lock (_sync) stream = _controlStream;
            if (stream == null) return false;

            try
            {
                // 多条消息不能交错写入，否则设备端按固定长度解析会整体错位
                lock (_controlWriteLock)
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke("scrcpy 控制通道写入失败，输入退回 adb shell：" + ex.Message);
                CloseControl();
                return false;
            }
        }

        private static void WriteU16BE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 8);
            buf[offset + 1] = (byte)value;
        }

        private static void WriteI16BE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 8);
            buf[offset + 1] = (byte)value;
        }

        private static void WriteU32BE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WriteI32BE(byte[] buf, int offset, int value)
        {
            WriteU32BE(buf, offset, unchecked((uint)value));
        }

        private static void WriteU64BE(byte[] buf, int offset, ulong value)
        {
            for (int i = 7; i >= 0; i--)
            {
                buf[offset + i] = (byte)value;
                value >>= 8;
            }
        }

        /// <summary>关闭当前片段：断开 socket、结束 server 进程、移除转发并清理设备端残留。</summary>
        private void CloseCurrent()
        {
            CloseControl();

            TcpClient client;
            Process server;
            int port;
            lock (_sync)
            {
                client = _client;
                server = _serverProcess;
                port = _forwardPort;
                _client = null;
                _serverProcess = null;
                _forwardPort = -1;
            }

            if (client != null) { try { client.Close(); } catch { } }
            if (server != null)
            {
                try { if (!server.HasExited) server.Kill(); } catch { }
                try { server.Dispose(); } catch { }
            }
            if (port > 0) RunAdb("forward --remove tcp:" + port.ToString(CultureInfo.InvariantCulture));
            RunAdb("shell pkill -f com.genymobile.scrcpy.Server");
        }

        private void SwitchToFallback()
        {
            var fallback = new AdbVideoStream(_adbPath, _serial, _size, _bitRate);
            fallback.Paused = _paused;
            fallback.SuppressWatchdog = _suppressWatchdog;
            fallback.Log += msg =>
            {
                var handler = Log;
                if (handler != null) handler(msg);
            };
            _fallback = fallback;
        }

        /// <summary>从请求尺寸里取长边作为 scrcpy 的 max_size（0 表示不限制）。</summary>
        private int MaxSide()
        {
            string[] parts = (_size ?? string.Empty).Split('x');
            int w, h;
            if (parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
                && w > 0 && h > 0)
            {
                return Math.Max(w, h);
            }
            return 0;
        }

        private static int NewScid()
        {
            // 31 位正数，避免 server 端 Integer.parseInt(value, 16) 溢出为负数
            return Guid.NewGuid().GetHashCode() & 0x7FFFFFFE;
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>执行一条短 adb 命令并返回标准输出（失败返回 null）。</summary>
        private string RunAdb(string adbArgs)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "-s " + AdbTools.Globals.QuoteSerial(_serial) + " " + adbArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    return text + err;
                }
            }
            catch { return null; }
        }
    }
}
