using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;

namespace RemoteProbe
{
    /// <summary>
    /// M1 验证工具：
    /// 1) 通过 adb 取 H.264 流并拆分成访问单元（帧）；
    /// 2) 加 --decode 时用 Windows 自带 H.264 解码器 MFT 解码，统计真实解码帧率与分辨率。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string adb = "adb";
            string serial = null;
            string size = "720x1280";
            int bitRate = 8000000;
            int seconds = 10;
            bool decode = false;
            bool annexB = true;
            bool ui = false;
            bool injectTest = false;
            bool convert = false;
            double convertTotalMs = 0;
            int convertFrames = 0;
            object convertLock = new object();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--adb": adb = Next(args, ref i); break;
                    case "--serial": serial = Next(args, ref i); break;
                    case "--size": size = Next(args, ref i); break;
                    case "--bit-rate": bitRate = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--seconds": seconds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--decode": decode = true; break;
                    case "--annexb": annexB = true; break;
                    case "--avcc": annexB = false; break;
                    case "--convert": convert = true; break;
                    case "--ui": ui = true; break;
                    case "--inject-test": injectTest = true; break;
                    default:
                        Console.WriteLine("用法: RemoteProbe --adb <adb.exe> --serial <序列号> [--size 720x1280] [--bit-rate 8000000] [--seconds 10] [--decode]");
                        return 1;
                }
            }

            if (string.IsNullOrEmpty(serial))
            {
                Console.WriteLine("必须指定 --serial");
                return 1;
            }

            if (injectTest)
            {
                // 验证常驻 adb shell 注入链路：Home 键 + 文本输入
                using (var injector = new InputInjector(adb, serial))
                {
                    Console.WriteLine("motionevent 支持: " + injector.SupportsMotionEvent);
                    injector.KeyEvent(3);
                    Thread.Sleep(600);
                    injector.Tap(600, 1200);
                    Thread.Sleep(300);
                    injector.Text("adb");
                }
                Console.WriteLine("注入完成");
                return 0;
            }

            if (ui)
            {
                // 交互窗口：实时画面 + 鼠标/键盘控制
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new RemoteControlWindow(adb, serial, size, bitRate);
                app.Run(window);
                return 0;
            }

            H264DecoderMft decoder = null;
            if (decode)
            {
                MfInterop.Check(MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_FULL), "MFStartup");
                decoder = new H264DecoderMft { Verbose = true, UseAnnexB = annexB };
                Console.WriteLine("已创建系统 H.264 解码器 MFT");

                if (convert)
                {
                    byte[] bgra = null;
                    var convertClock = new Stopwatch();
                    decoder.FrameDecoded += (ptr, len, w, h) =>
                    {
                        if (bgra == null || bgra.Length < w * h * 4) bgra = new byte[w * h * 4];
                        convertClock.Restart();
                        if (Nv12Converter.Convert(ptr, len, w, h, bgra))
                        {
                            double ms = convertClock.Elapsed.TotalMilliseconds;
                            lock (convertLock)
                            {
                                convertTotalMs += ms;
                                convertFrames++;
                            }
                        }
                    };
                }
            }

            try
            {
                using (var stream = new AdbVideoStream(adb, serial, size, bitRate))
                {
                    stream.Log += msg => Console.WriteLine("[adb] " + msg);
                    stream.WakeUpDevice();

                    var splitter = new AccessUnitSplitter();
                    var clock = Stopwatch.StartNew();

                    if (decoder != null)
                    {
                        var decoderRef = decoder;
                        bool reported = false;
                        splitter.AccessUnit += (au, keyFrame) =>
                        {
                            try
                            {
                                decoderRef.Feed(au, keyFrame);
                            }
                            catch (Exception ex)
                            {
                                if (!reported)
                                {
                                    reported = true;
                                    Console.WriteLine("[解码错误] " + ex.GetType().Name + ": " + ex.Message);
                                    Console.WriteLine(ex.StackTrace);
                                }
                            }
                        };
                    }
                    else
                    {
                        splitter.AccessUnit += (au, keyFrame) =>
                        {
                            if (keyFrame) Console.WriteLine("[帧] 关键帧，长度 " + au.Length + " 字节");
                        };
                    }

                    var pumpThread = new Thread(() => stream.Pump((data, count) => splitter.Feed(data, count)))
                    {
                        IsBackground = true,
                        Name = "adb-stream"
                    };
                    pumpThread.Start();

                    int lastAu = 0;
                    int lastDecoded = 0;
                    double lastSec = 0;
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        Thread.Sleep(500);
                        double elapsed = clock.Elapsed.TotalSeconds;
                        if (elapsed - lastSec >= 1.0)
                        {
                            int auInWindow = splitter.AccessUnitCount - lastAu;
                            int decodedInWindow = decoder != null ? decoder.DecodedFrames - lastDecoded : 0;

                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "[{0,4:F0}s] 帧 {1,3} ({2,5:F1} fps){3}  片段 {4}",
                                elapsed,
                                auInWindow,
                                auInWindow / (elapsed - lastSec),
                                decoder != null
                                    ? string.Format(CultureInfo.InvariantCulture, "  解码 {0,3} ({1,5:F1} fps) {2}x{3}",
                                        decodedInWindow, decodedInWindow / (elapsed - lastSec), decoder.Width, decoder.Height)
                                    : string.Empty,
                                stream.SegmentCount));

                            lastAu = splitter.AccessUnitCount;
                            lastDecoded = decoder != null ? decoder.DecodedFrames : 0;
                            lastSec = elapsed;
                        }
                    }

                    stream.Stop();
                    pumpThread.Join(2000);

                    double total = clock.Elapsed.TotalSeconds;
                    Console.WriteLine();
                    Console.WriteLine("===== 结果 =====");
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "时长     : {0:F1} s", total));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "访问单元 : {0}（关键帧 {1}）", splitter.AccessUnitCount, splitter.KeyFrameCount));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "平均帧率 : {0:F1} fps", splitter.AccessUnitCount / Math.Max(total, 0.001)));

                    if (decoder != null)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "解码帧数 : {0}（平均 {1:F1} fps，分辨率 {2}x{3}）",
                            decoder.DecodedFrames, decoder.DecodedFrames / Math.Max(total, 0.001), decoder.Width, decoder.Height));

                        if (convert && convertFrames > 0)
                        {
                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "转换耗时 : 平均 {0:F2} ms/帧（共 {1} 帧，NV12→BGRA）",
                                convertTotalMs / convertFrames, convertFrames));
                        }
                        Console.WriteLine(decoder.DecodedFrames > 0
                            ? "结论：系统解码器可用，可进入 M1 渲染（WPF 显示）阶段。"
                            : "结论：未解出画面，需检查输入类型/码流格式。");
                        return decoder.DecodedFrames > 0 ? 0 : 4;
                    }

                    Console.WriteLine(splitter.AccessUnitCount > 0
                        ? "结论：帧边界识别正常，可加 --decode 验证解码。"
                        : "结论：未拆出访问单元，需检查码流内容。");
                    return splitter.AccessUnitCount > 0 ? 0 : 2;
                }
            }
            finally
            {
                if (decoder != null)
                {
                    decoder.Dispose();
                    MfInterop.MFShutdown();
                }
            }
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("参数 " + args[i] + " 缺少取值");
            return args[++i];
        }
    }
}
