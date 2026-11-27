using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 录屏器：直接截取 scrcpy 码流里的 H.264 访问单元，用内置的最小 MP4 封装落盘。
    /// 全程不解码、不重编码，文件体积等于设备编码器的实际输出；画面静止时设备不产帧，
    /// 文件也随之停止增长（与系统录屏 / Android Studio 的体积量级一致）。
    /// 分辨率变化（横竖屏切换、档位切换、编码器自行降级）时收尾当前文件，
    /// 从下一个关键帧开始新的分段文件，避免产生单轨尺寸冲突的非法 MP4。
    /// </summary>
    internal sealed class Mp4Recorder : IDisposable
    {
        /// <summary>待写帧：AU 数据由拆分器每次新建，持有引用是安全的。</summary>
        private struct PendingFrame
        {
            public byte[] Data;
            public bool KeyFrame;
            public long Ticks;
        }

        /// <summary>队列上限：写盘跟不上时丢帧，不能反过来阻塞取流线程。</summary>
        private const int QueueCapacity = 240;

        private readonly BlockingCollection<PendingFrame> _queue =
            new BlockingCollection<PendingFrame>(new ConcurrentQueue<PendingFrame>(), QueueCapacity);

        private readonly string _directory;
        private readonly string _baseName;
        private readonly Action<string> _log;
        private readonly Thread _writer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly object _segmentLock = new object();
        private readonly List<string> _files = new List<string>();

        /// <summary>
        /// 收尾完成信号：Stop 可能在两个线程上同时被调用（点“停止”的后台任务 + 关窗清理），
        /// 后到的调用必须等前一个把 moov 写完再返回，否则窗口关闭/进程退出会把文件带走。
        /// </summary>
        private readonly ManualResetEventSlim _stopCompleted = new ManualResetEventSlim(false);

        private byte[] _sps;
        private byte[] _pps;
        private int _width;
        private int _height;
        private long _segmentStartTicks;
        private Mp4SegmentWriter _segment;
        private int _segmentCount;
        private long _bytes;
        private int _dropped;
        private int _stopped;
        private int _openFailed;
        private int _startedRaised;

        /// <summary>已收尾片段的累计时长（毫秒）：用于显示"已录进文件"的时长。</summary>
        private long _closedMs;

        public Mp4Recorder(string directory, string baseName, Action<string> log)
        {
            _directory = directory;
            _baseName = baseName;
            _log = log;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "mp4-recorder" };
            _writer.Start();
        }

        /// <summary>已完成的片段数（0 表示还在等第一个关键帧）。</summary>
        public int SegmentCount { get { return Volatile.Read(ref _segmentCount); } }

        /// <summary>已写入码流的字节数（不含 MP4 封装开销）。</summary>
        public long BytesWritten { get { return Interlocked.Read(ref _bytes); } }

        /// <summary>因写盘跟不上而丢弃的帧数。</summary>
        public int DroppedFrames { get { return Volatile.Read(ref _dropped); } }

        /// <summary>挂钟时长：从点“录制”开始算，停止后固定（含等待关键帧的空档）。</summary>
        public TimeSpan Elapsed { get { return _clock.Elapsed; } }

        /// <summary>
        /// 已录入文件的时长：各片段内容时长之和。不含等待关键帧、重建取流与分段间隙，
        /// 因此与成片长度一致，通常比 <see cref="Elapsed"/> 少几秒。
        /// </summary>
        public TimeSpan RecordedDuration
        {
            get
            {
                double seconds = Interlocked.Read(ref _closedMs) / 1000.0;
                lock (_segmentLock)
                {
                    if (_segment != null) seconds += _segment.ContentSeconds;
                }
                return TimeSpan.FromSeconds(seconds);
            }
        }

        /// <summary>已生成的文件（按生成顺序）。</summary>
        public IList<string> Files
        {
            get
            {
                lock (_segmentLock) return _files.ToArray();
            }
        }

        public bool IsStopped { get { return Volatile.Read(ref _stopped) != 0; } }

        /// <summary>
        /// 第一段文件真正写出第一帧时触发（写线程，最多一次）。
        /// 窗口用它把"准备中（等待关键帧）"切换成"录制中"并开始计时。
        /// </summary>
        public event Action Started;

        /// <summary>
        /// 送入一个访问单元（在取流线程调用，只做入队，绝不做磁盘 I/O）。
        /// </summary>
        public void Push(byte[] annexB, bool keyFrame)
        {
            Push(annexB, keyFrame, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// 送入一个访问单元并指定到达时刻：预录缓存补帧时用原始时间戳，
        /// 这样"点录制前缓存的那段"与之后的实时帧时间轴是连续的。
        /// </summary>
        public void Push(byte[] annexB, bool keyFrame, long ticks)
        {
            if (annexB == null || annexB.Length == 0) return;
            if (Volatile.Read(ref _stopped) != 0) return;

            var frame = new PendingFrame
            {
                Data = annexB,
                KeyFrame = keyFrame,
                Ticks = ticks
            };

            try
            {
                if (!_queue.TryAdd(frame)) Interlocked.Increment(ref _dropped);
            }
            catch (InvalidOperationException)
            {
                // 已调用 CompleteAdding（正在收尾）：直接丢弃
            }
        }

        /// <summary>
        /// 停止录制：唤醒写线程、写完 moov 后返回。
        /// 若另一个线程已经在收尾（例如刚点过“停止”），这里会等它完成再返回——
        /// 关窗清理与进程退出依赖这一点，否则可能拿到没有 moov 的不可播文件。
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                try { _stopCompleted.Wait(6000); } catch { }
                return;
            }

            try
            {
                try { _queue.CompleteAdding(); } catch { }
                try
                {
                    if (_writer.IsAlive) _writer.Join(5000);
                }
                catch { }

                // 写线程异常退出时的兜底（正常情况下它自己会收尾）
                CloseSegment();
                _clock.Stop();
            }
            finally
            {
                _stopCompleted.Set();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void WriterLoop()
        {
            try
            {
                foreach (PendingFrame frame in _queue.GetConsumingEnumerable())
                {
                    if (Volatile.Read(ref _stopped) != 0 && _queue.Count == 0) break;
                    try
                    {
                        WriteFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        Log("录制写入失败：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("录制线程异常：" + ex.Message);
            }
            finally
            {
                CloseSegment();
            }
        }

        private void WriteFrame(PendingFrame frame)
        {
            // 建文件失败（目录不可写/磁盘满）后不再重试，避免每帧刷一条同样的日志
            if (Volatile.Read(ref _openFailed) != 0) return;

            byte[] sps, pps, sample;
            bool idr;
            if (!H264AccessUnit.TryParse(frame.Data, out sps, out pps, out idr, out sample)) return;

            if (sps != null && pps != null)
            {
                int width = _width;
                int height = _height;
                if (!H264Sps.TryGetSize(sps, out width, out height)) return;

                if (width != _width || height != _height)
                {
                    if (_segment != null)
                    {
                        CloseSegment();
                        Log("录制分辨率变化，切换到 " + width + "x" + height);
                    }
                    _width = width;
                    _height = height;
                }

                _sps = sps;
                _pps = pps;
            }

            if (sample == null) return;
            if (_sps == null || _pps == null || _width <= 0 || _height <= 0) return;

            // 文件必须从关键帧开始，否则播放器开头会花屏（等下一帧 IDR）
            if (_segment == null)
            {
                if (!idr) return;
                if (!OpenSegment()) return;
                _segmentStartTicks = frame.Ticks;
            }

            // 100ns → 90kHz：1 秒 = 1e7 tick = 90000 单位，所以是 ×9/1000。
            // 注意别写成 ×9/100（那是 10 倍慢放，表现为视频时长异常、画面只有几帧）。
            long pts = (frame.Ticks - _segmentStartTicks) * 9 / 1000;
            _segment.WriteSample(sample, idr, pts);
        }

        private bool OpenSegment()
        {
            lock (_segmentLock)
            {
                if (_segment != null) return true;

                string fileName = _baseName
                    + (_segmentCount == 0 ? string.Empty
                        : "_p" + (_segmentCount + 1).ToString(CultureInfo.InvariantCulture))
                    + ".mp4";
                string path = Path.Combine(_directory, fileName);

                // 同名文件已存在（同一秒内连续录制两次）：追加序号，绝不覆盖已有录像
                int index = 2;
                while (File.Exists(path))
                {
                    path = Path.Combine(_directory,
                        Path.GetFileNameWithoutExtension(fileName) + "_"
                        + index.ToString(CultureInfo.InvariantCulture) + ".mp4");
                    index++;
                }

                try
                {
                    _segment = new Mp4SegmentWriter(path, _width, _height, _sps, _pps);
                    _segmentCount++;
                    _files.Add(path);
                    Log("录制片段 #" + _segmentCount + "：" + Path.GetFileName(path)
                        + "（" + _width + "x" + _height + "）");

                    // 通知窗口：真正开始写画面了（"准备中" → "录制中"）
                    if (Interlocked.Exchange(ref _startedRaised, 1) == 0)
                    {
                        Action handler = Started;
                        if (handler != null)
                        {
                            try { handler(); } catch { }
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _openFailed, 1);
                    Log("创建录像文件失败：" + ex.Message);
                    return false;
                }
            }
        }

        private void CloseSegment()
        {
            Mp4SegmentWriter writer;
            lock (_segmentLock)
            {
                writer = _segment;
                _segment = null;
                if (writer != null)
                {
                    // 先按最后一帧的时间戳累计，保证切段瞬间 UI 上显示的时长不会回退
                    Interlocked.Add(ref _closedMs, (long)Math.Round(writer.ContentSeconds * 1000));
                }
            }
            if (writer == null) return;

            try
            {
                writer.Dispose();

                // 最后一帧的显示时长要到收尾时才确定，这里把差额补上，
                // 保证显示的"已录时长"与成片长度一致（差一帧看不出，但没必要留着）
                long tail = (long)Math.Round((writer.DurationSeconds - writer.ContentSeconds) * 1000);
                if (tail > 0) Interlocked.Add(ref _closedMs, tail);

                long bytes = writer.DataBytes;
                Interlocked.Add(ref _bytes, bytes);
                Log("录制片段完成：" + (bytes / 1024) + " KB，"
                    + writer.SampleCount + " 帧，"
                    + writer.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒");
            }
            catch (Exception ex)
            {
                Log("封装 MP4 失败：" + ex.Message);
            }
        }

        private void Log(string message)
        {
            Action<string> log = _log;
            if (log != null) log(message);
        }
    }

    /// <summary>
    /// 访问单元拆解：分离参数集与 VCL 数据。参数集用于 avcC，其余 NAL 转成
    /// AVCC（4 字节长度前缀）作为 MP4 样本——这正是「原样搬运设备码流」的关键一步。
    /// </summary>
    internal static class H264AccessUnit
    {
        public static bool TryParse(byte[] annexB, out byte[] sps, out byte[] pps,
            out bool hasIdr, out byte[] avccSample)
        {
            sps = null;
            pps = null;
            hasIdr = false;
            avccSample = null;
            if (annexB == null || annexB.Length < 5) return false;

            using (var sample = new MemoryStream(annexB.Length))
            {
                int position = 0;
                while (true)
                {
                    int start = FindStartCode(annexB, position);
                    if (start < 0) break;

                    int nalStart = start + 3;
                    if (nalStart >= annexB.Length) break;

                    int next = FindStartCode(annexB, nalStart);
                    int nalEnd = next < 0 ? annexB.Length : next;
                    // 4 字节起始码多出来的 0 属于 trailing_zero_8bits，不能算进上一个 NAL
                    while (nalEnd > nalStart && annexB[nalEnd - 1] == 0) nalEnd--;

                    int length = nalEnd - nalStart;
                    if (length > 0)
                    {
                        int type = annexB[nalStart] & 0x1F;
                        if (type == 7)
                        {
                            sps = new byte[length];
                            Buffer.BlockCopy(annexB, nalStart, sps, 0, length);
                        }
                        else if (type == 8)
                        {
                            pps = new byte[length];
                            Buffer.BlockCopy(annexB, nalStart, pps, 0, length);
                        }
                        else
                        {
                            if (type == 5) hasIdr = true;
                            sample.WriteByte((byte)(length >> 24));
                            sample.WriteByte((byte)(length >> 16));
                            sample.WriteByte((byte)(length >> 8));
                            sample.WriteByte((byte)length);
                            sample.Write(annexB, nalStart, length);
                        }
                    }

                    if (next < 0) break;
                    position = next;
                }

                // 只有参数集（编码器配置包）时也要返回 true：参数集要缓存，但没有可写样本
                if (sample.Length == 0 && sps == null && pps == null) return false;
                avccSample = sample.Length > 0 ? sample.ToArray() : null;
                return true;
            }
        }

        private static int FindStartCode(byte[] data, int from)
        {
            for (int i = Math.Max(from, 0); i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// 从 SPS 里解析可见分辨率（含裁剪），用于确定 MP4 轨道的宽高。
    /// 只解析到裁剪信息为止，遇到非法数据返回 false，由调用方丢弃该帧。
    /// </summary>
    internal static class H264Sps
    {
        public static bool TryGetSize(byte[] sps, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (sps == null || sps.Length < 5) return false;

            try
            {
                byte[] nal = RemoveEmulationPrevention(sps);
                if (nal.Length < 5) return false;

                var reader = new BitReader(nal);
                reader.ReadBits(8);          // NAL header
                int profileIdc = (int)reader.ReadBits(8);
                reader.ReadBits(8);          // constraint_set flags + reserved
                reader.ReadBits(8);          // level_idc
                reader.ReadUe();             // seq_parameter_set_id

                int chromaFormatIdc = 1;
                if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244
                    || profileIdc == 44 || profileIdc == 83 || profileIdc == 86 || profileIdc == 118
                    || profileIdc == 128 || profileIdc == 138 || profileIdc == 139
                    || profileIdc == 134 || profileIdc == 135)
                {
                    chromaFormatIdc = (int)reader.ReadUe();
                    if (chromaFormatIdc == 3) reader.ReadBit();   // separate_colour_plane_flag
                    reader.ReadUe();                              // bit_depth_luma_minus8
                    reader.ReadUe();                              // bit_depth_chroma_minus8
                    reader.ReadBit();                             // qpprime_y_zero_transform_bypass_flag
                    if (reader.ReadBit() != 0)                    // seq_scaling_matrix_present_flag
                    {
                        int count = chromaFormatIdc != 3 ? 8 : 12;
                        for (int i = 0; i < count; i++)
                        {
                            if (reader.ReadBit() != 0) SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }

                reader.ReadUe();             // log2_max_frame_num_minus4
                int pocType = (int)reader.ReadUe();
                if (pocType == 0)
                {
                    reader.ReadUe();         // log2_max_pic_order_cnt_lsb_minus4
                }
                else if (pocType == 1)
                {
                    reader.ReadBit();        // delta_pic_order_always_zero_flag
                    reader.ReadSe();         // offset_for_non_ref_pic
                    reader.ReadSe();         // offset_for_top_to_bottom_field
                    int cycle = (int)reader.ReadUe();
                    if (cycle > 255) return false;
                    for (int i = 0; i < cycle; i++) reader.ReadSe();
                }

                reader.ReadUe();             // max_num_ref_frames
                reader.ReadBit();            // gaps_in_frame_num_value_allowed_flag

                int widthMbs = (int)reader.ReadUe() + 1;
                int heightMapUnits = (int)reader.ReadUe() + 1;
                bool frameMbsOnly = reader.ReadBit() != 0;
                int frameHeightFactor = frameMbsOnly ? 1 : 2;
                if (!frameMbsOnly) reader.ReadBit();  // mb_adaptive_frame_field_flag
                reader.ReadBit();                     // direct_8x8_inference_flag

                int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
                if (reader.ReadBit() != 0)            // frame_cropping_flag
                {
                    cropLeft = (int)reader.ReadUe();
                    cropRight = (int)reader.ReadUe();
                    cropTop = (int)reader.ReadUe();
                    cropBottom = (int)reader.ReadUe();
                }

                int subWidthC;
                int subHeightC;
                if (chromaFormatIdc == 1) { subWidthC = 2; subHeightC = 2; }
                else if (chromaFormatIdc == 2) { subWidthC = 2; subHeightC = 1; }
                else { subWidthC = 1; subHeightC = 1; }

                width = widthMbs * 16 - (cropLeft + cropRight) * subWidthC;
                height = frameHeightFactor * heightMapUnits * 16
                    - (cropTop + cropBottom) * subHeightC * frameHeightFactor;

                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void SkipScalingList(BitReader reader, int size)
        {
            int lastScale = 8;
            int nextScale = 8;
            for (int i = 0; i < size; i++)
            {
                if (nextScale != 0)
                {
                    int delta = reader.ReadSe();
                    nextScale = (lastScale + delta + 256) % 256;
                }
                if (nextScale != 0) lastScale = nextScale;
            }
        }

        private static byte[] RemoveEmulationPrevention(byte[] nal)
        {
            var result = new byte[nal.Length];
            int count = 0;
            for (int i = 0; i < nal.Length; i++)
            {
                if (i >= 2 && nal[i] == 3 && nal[i - 1] == 0 && nal[i - 2] == 0) continue;
                result[count++] = nal[i];
            }
            if (count == nal.Length) return nal;
            Array.Resize(ref result, count);
            return result;
        }
    }

    /// <summary>按位读取（指数哥伦布编码），仅用于解析 SPS。</summary>
    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bitOffset;

        public BitReader(byte[] data)
        {
            _data = data;
        }

        public int ReadBit()
        {
            if (_bitOffset >= _data.Length * 8) throw new IndexOutOfRangeException("比特流已读完");
            int value = (_data[_bitOffset >> 3] >> (7 - (_bitOffset & 7))) & 1;
            _bitOffset++;
            return value;
        }

        public uint ReadBits(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++) value = (value << 1) | (uint)ReadBit();
            return value;
        }

        public uint ReadUe()
        {
            int zeros = 0;
            while (ReadBit() == 0)
            {
                zeros++;
                if (zeros > 31) throw new IndexOutOfRangeException("指数哥伦布编码异常");
            }
            if (zeros == 0) return 0;
            return (1u << zeros) - 1 + ReadBits(zeros);
        }

        public int ReadSe()
        {
            uint value = ReadUe();
            return (value & 1) == 0 ? -(int)(value >> 1) : (int)((value + 1) >> 1);
        }
    }

    /// <summary>
    /// 单段 MP4 写入器（只有一条 H.264 视频轨）：
    /// ftyp → mdat（64 位长度占位，停止时回填）→ moov（停止时补写），
    /// 因此录制过程中被强制结束时最多丢掉索引，不会写坏文件结构。
    /// </summary>
    internal sealed class Mp4SegmentWriter : IDisposable
    {
        private const int Timescale = 90000;

        private readonly FileStream _stream;
        private readonly string _path;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _sps;
        private readonly byte[] _pps;

        private readonly List<int> _sizes = new List<int>();
        private readonly List<long> _offsets = new List<long>();
        private readonly List<long> _durations = new List<long>();
        private readonly List<int> _syncSamples = new List<int>();

        private readonly long _mdatStart;
        private long _lastPts = -1;
        private long _dataBytes;
        private bool _closed;

        public Mp4SegmentWriter(string path, int width, int height, byte[] sps, byte[] pps)
        {
            if (sps == null || sps.Length < 4) throw new ArgumentException("SPS 数据不足");
            if (pps == null || pps.Length == 0) throw new ArgumentException("PPS 数据为空");

            _path = path;
            _width = width;
            _height = height;
            _sps = sps;
            _pps = pps;

            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
            WriteBytes(BuildFtyp());

            _mdatStart = _stream.Position;
            // size=1 + 64 位 largesize：录到 4GB 以上也不需要换格式，停止时回填真实长度
            WriteBytes(Concat(U32(1), FourCC("mdat"), U64(0)));
        }

        /// <summary>写入一帧码流（AVCC 样本）。时间戳单位 1/90000 秒，内部保证严格递增。</summary>
        public void WriteSample(byte[] avccSample, bool sync, long pts)
        {
            if (_closed || avccSample == null || avccSample.Length == 0) return;

            if (pts <= _lastPts) pts = _lastPts + 1;
            if (_lastPts >= 0) _durations.Add(pts - _lastPts);
            _lastPts = pts;

            long offset = _stream.Position;
            _stream.Write(avccSample, 0, avccSample.Length);

            // 写成功后才登记样本：万一写盘失败（磁盘满、路径被占），宁可不记这一帧，
            // 也不能让 stco 的条数和 stsz 对不上——数量不一致会被播放器判定为文件损坏。
            _offsets.Add(offset);
            _sizes.Add(avccSample.Length);
            if (sync) _syncSamples.Add(_sizes.Count);
            _dataBytes += avccSample.Length;
        }

        /// <summary>已写入的码流字节数（不含封装开销）。</summary>
        public long DataBytes { get { return _dataBytes; } }

        /// <summary>写入的样本（帧）数，收尾后有效。</summary>
        public int SampleCount { get; private set; }

        /// <summary>封装后的时长（秒），收尾后有效。</summary>
        public double DurationSeconds { get; private set; }

        /// <summary>
        /// 已写入内容的时长（秒）：最后一帧的时间戳。录制过程中可实时读取，
        /// 比挂钟时间短——不含等待关键帧和重建取流的空档，正好等于成片的长度。
        /// </summary>
        public double ContentSeconds
        {
            get { return _lastPts <= 0 ? 0 : _lastPts / (double)Timescale; }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            // 先把缓冲区落盘再回填 mdat 长度：否则缓冲区里尚未写出的样本会被
            // 随后的 Seek 冲到文件末尾，把 moov 之前的样本区写乱（文件直接损坏）。
            try { _stream.Flush(); } catch { }

            long dataEnd = _stream.Position;
            if (_sizes.Count == 0)
            {
                // 一帧都没写进去（建文件后立刻失败）：删掉残缺文件，别在目录里留垃圾
                try { _stream.Dispose(); } catch { }
                try { File.Delete(_path); } catch { }
                return;
            }

            while (_durations.Count < _sizes.Count)
            {
                _durations.Add(_durations.Count > 0 ? _durations[_durations.Count - 1] : Timescale / 30);
            }

            long duration = 0;
            foreach (long value in _durations) duration += value;

            SampleCount = _sizes.Count;
            DurationSeconds = duration / (double)Timescale;

            _stream.Position = _mdatStart;
            WriteBytes(Concat(U32(1), FourCC("mdat"), U64(dataEnd - _mdatStart)));
            _stream.Position = dataEnd;
            WriteBytes(BuildMoov(duration));

            try
            {
                _stream.Flush();
                _stream.Dispose();
            }
            catch { }
        }

        private byte[] BuildFtyp()
        {
            return Box("ftyp", FourCC("isom"), U32(512),
                FourCC("isom"), FourCC("iso2"), FourCC("avc1"), FourCC("mp41"));
        }

        private byte[] BuildMoov(long duration90k)
        {
            long durationMs = duration90k / 90;

            byte[] mvhd = FullBox("mvhd", 0, 0,
                U32(0), U32(0), U32(1000), U32(durationMs),
                U32(0x00010000), U16(0x0100), U16(0), U32(0), U32(0),
                Matrix(), Zeros(24), U32(2));

            byte[] tkhd = FullBox("tkhd", 0, 7,
                U32(0), U32(0), U32(1), U32(0), U32(durationMs),
                U32(0), U32(0), U16(0), U16(0), U16(0), U16(0),
                Matrix(), U32((long)_width << 16), U32((long)_height << 16));

            byte[] mdhd = FullBox("mdhd", 0, 0,
                U32(0), U32(0), U32(Timescale), U32(duration90k), U16(0x55C4), U16(0));

            byte[] hdlr = FullBox("hdlr", 0, 0,
                U32(0), FourCC("vide"), Zeros(12),
                Encoding.ASCII.GetBytes("VideoHandler"), U8(0));

            byte[] vmhd = FullBox("vmhd", 0, 1, U16(0), U16(0), U16(0), U16(0));
            byte[] dref = FullBox("dref", 0, 0, U32(1), FullBox("url ", 0, 1));
            byte[] dinf = Box("dinf", dref);

            byte[] stsd = FullBox("stsd", 0, 0, U32(1), BuildAvc1(BuildAvcC()));

            var stblParts = new List<byte[]>();
            stblParts.Add(stsd);
            stblParts.Add(BuildStts());
            byte[] stss = BuildStss();
            if (stss != null) stblParts.Add(stss);
            stblParts.Add(FullBox("stsc", 0, 0, U32(1), U32(1), U32(1), U32(1)));
            stblParts.Add(BuildStsz());
            stblParts.Add(BuildStco());

            byte[] stbl = Box("stbl", stblParts.ToArray());
            byte[] minf = Box("minf", vmhd, dinf, stbl);
            byte[] mdia = Box("mdia", mdhd, hdlr, minf);
            byte[] trak = Box("trak", tkhd, mdia);
            return Box("moov", mvhd, trak);
        }

        private byte[] BuildAvcC()
        {
            return Box("avcC",
                U8(1), U8(_sps[1]), U8(_sps[2]), U8(_sps[3]),
                U8(0xFF), U8(0xE1),
                U16(_sps.Length), _sps,
                U8(1), U16(_pps.Length), _pps);
        }

        private byte[] BuildAvc1(byte[] avcC)
        {
            return Box("avc1",
                Zeros(6), U16(1),
                U16(0), U16(0), Zeros(12),
                U16(_width), U16(_height),
                U32(0x00480000), U32(0x00480000), U32(0), U16(1),
                Zeros(32),
                U16(0x0018), U16(0xFFFF),
                avcC);
        }

        private byte[] BuildStts()
        {
            var counts = new List<long>();
            var durations = new List<long>();
            for (int i = 0; i < _durations.Count; i++)
            {
                long value = _durations[i];
                if (durations.Count > 0 && durations[durations.Count - 1] == value)
                {
                    counts[counts.Count - 1]++;
                }
                else
                {
                    durations.Add(value);
                    counts.Add(1);
                }
            }

            var parts = new List<byte[]> { U32(counts.Count) };
            for (int i = 0; i < counts.Count; i++)
            {
                parts.Add(U32(counts[i]));
                parts.Add(U32(durations[i]));
            }
            return FullBox("stts", 0, 0, parts.ToArray());
        }

        private byte[] BuildStss()
        {
            // 全部都是关键帧时省略 stss（规范中缺省即表示每个样本都可随机访问）
            if (_syncSamples.Count == 0 || _syncSamples.Count >= _sizes.Count) return null;

            var parts = new List<byte[]> { U32(_syncSamples.Count) };
            foreach (int index in _syncSamples) parts.Add(U32(index));
            return FullBox("stss", 0, 0, parts.ToArray());
        }

        private byte[] BuildStsz()
        {
            var parts = new List<byte[]> { U32(0), U32(_sizes.Count) };
            foreach (int size in _sizes) parts.Add(U32(size));
            return FullBox("stsz", 0, 0, parts.ToArray());
        }

        private byte[] BuildStco()
        {
            bool large = _offsets.Count > 0 && _offsets[_offsets.Count - 1] > uint.MaxValue;
            var parts = new List<byte[]> { U32(_offsets.Count) };
            foreach (long offset in _offsets)
            {
                parts.Add(large ? U64(offset) : U32(offset));
            }
            return FullBox(large ? "co64" : "stco", 0, 0, parts.ToArray());
        }

        private void WriteBytes(byte[] data)
        {
            _stream.Write(data, 0, data.Length);
        }

        private static byte[] U8(int value)
        {
            return new[] { (byte)value };
        }

        private static byte[] U16(int value)
        {
            return new[] { (byte)(value >> 8), (byte)value };
        }

        private static byte[] U32(long value)
        {
            return new[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            };
        }

        private static byte[] U64(long value)
        {
            return new[]
            {
                (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            };
        }

        private static byte[] FourCC(string type)
        {
            return new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        }

        private static byte[] Zeros(int count)
        {
            return new byte[count];
        }

        private static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = Concat(parts);
            return Concat(U32(payload.Length + 8), FourCC(type), payload);
        }

        private static byte[] FullBox(string type, int version, int flags, params byte[][] parts)
        {
            byte[] payload = Concat(parts);
            return Concat(U32(payload.Length + 12), FourCC(type),
                new[] { (byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags },
                payload);
        }

        private static byte[] Matrix()
        {
            return new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00
            };
        }

        private static byte[] Concat(params byte[][] parts)
        {
            if (parts == null || parts.Length == 0) return new byte[0];

            int length = 0;
            for (int i = 0; i < parts.Length; i++) length += parts[i].Length;

            var result = new byte[length];
            int offset = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, result, offset, parts[i].Length);
                offset += parts[i].Length;
            }
            return result;
        }
    }
}
