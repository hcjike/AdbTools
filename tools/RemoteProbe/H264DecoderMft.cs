using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RemoteProbe
{
    /// <summary>
    /// 使用 Windows 自带 H.264 解码器 MFT 解码（不依赖第三方库）。
    /// 输入为 Annex-B 访问单元，内部转换为 AVCC(length-prefixed) 后送入 MFT，输出 NV12 帧。
    /// </summary>
    internal sealed class H264DecoderMft : IDisposable
    {
        private readonly IMFTransform _transform;
        private byte[] _sequenceHeader;
        private bool _configured;
        private int _width;
        private int _height;
        private long _sampleTime;
        private bool _disposed;
        private int _diagInput;
        private int _diagOutput;

        /// <summary>输出 MFT 诊断日志（定位解码问题时使用）。</summary>
        public bool Verbose { get; set; }

        /// <summary>样本直接使用 Annex-B（带起始码，实测系统解码器接受此格式，默认启用）。</summary>
        public bool UseAnnexB { get; set; } = true;

        /// <summary>解码出一帧：参数为 NV12 数据指针、长度、宽、高（指针仅在回调期间有效）。</summary>
        public event Action<IntPtr, int, int, int> FrameDecoded;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public int DecodedFrames { get; private set; }
        public int OutputBufferSize { get; private set; }

        public H264DecoderMft()
        {
            _transform = MfInterop.CreateH264Decoder();
        }

        /// <summary>送入一个访问单元，返回本次解出的帧数。</summary>
        public int Feed(byte[] annexB, bool keyFrame)
        {
            if (_disposed) return 0;
            if (annexB == null || annexB.Length == 0) return 0;

            if (!_configured)
            {
                _sequenceHeader = ExtractParameterSets(annexB, UseAnnexB);
                if (_sequenceHeader == null) return 0; // 还没拿到 SPS/PPS
                ConfigureInputType();
            }

            byte[] avcc = UseAnnexB ? annexB : AnnexBToAvcc(annexB);
            if (avcc.Length == 0) return 0;

            IMFSample sample = CreateSample(avcc);
            int hr = _transform.ProcessInput(0, sample, 0);
            if (Verbose && _diagInput++ < 5)
            {
                Console.WriteLine("[MFT] ProcessInput hr=0x" + hr.ToString("X8") + " 输入 " + avcc.Length + " 字节");
            }
            if (hr == MfInterop.MF_E_NOTACCEPTING)
            {
                // 上一次输入还没取完输出：先取空再重试
                int drained = Drain();
                hr = _transform.ProcessInput(0, sample, 0);
                if (hr < 0)
                {
                    Marshal.ReleaseComObject(sample);
                    return drained;
                }
                return drained + Drain();
            }

            if (hr < 0)
            {
                Marshal.ReleaseComObject(sample);
                return 0;
            }

            return Drain();
        }

        private void ConfigureInputType()
        {
            IMFMediaType inputType;
            MfInterop.Check(MfInterop.MFCreateMediaType(out inputType), "创建输入媒体类型");

            Guid major = MfInterop.MFMediaType_Video;
            Guid subtype = MfInterop.MFVideoFormat_H264;
            MfInterop.Check(inputType.SetGUID(ref MfInterop.MF_MT_MAJOR_TYPE, ref major), "设置主类型");
            MfInterop.Check(inputType.SetGUID(ref MfInterop.MF_MT_SUBTYPE, ref subtype), "设置子类型");
            MfInterop.Check(inputType.SetBlob(ref MfInterop.MF_MT_MPEG_SEQUENCE_HEADER, _sequenceHeader, _sequenceHeader.Length), "设置 SPS/PPS");

            int hr = _transform.SetInputType(0, inputType, 0);
            Marshal.ReleaseComObject(inputType);
            MfInterop.Check(hr, "设置输入类型");
            if (Verbose)
            {
                Console.WriteLine("[MFT] 输入类型已设置，序列头 " + _sequenceHeader.Length + " 字节");
            }

            _transform.ProcessMessage(MfInterop.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);
            _transform.ProcessMessage(MfInterop.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _configured = true;
            ConfigureOutputType();
        }

        private void ConfigureOutputType()
        {
            for (int index = 0; ; index++)
            {
                IMFMediaType type;
                int hr = _transform.GetOutputAvailableType(0, index, out type);
                if (hr < 0) break;

                Guid subtype;
                type.GetGUID(ref MfInterop.MF_MT_SUBTYPE, out subtype);
                if (subtype == MfInterop.MFVideoFormat_NV12)
                {
                    long packed;
                    if (type.GetUINT64(ref MfInterop.MF_MT_FRAME_SIZE, out packed) == 0)
                    {
                        int w, h;
                        MfInterop.UnpackSize(packed, out w, out h);
                        _width = w;
                        _height = h;
                    }

                    int setHr = _transform.SetOutputType(0, type, 0);
                    Marshal.ReleaseComObject(type);
                    MfInterop.Check(setHr, "设置输出类型");

                    if (_width <= 0 || _height <= 0)
                    {
                        IMFMediaType current;
                        if (_transform.GetOutputCurrentType(0, out current) == 0)
                        {
                            long packed2;
                            if (current.GetUINT64(ref MfInterop.MF_MT_FRAME_SIZE, out packed2) == 0)
                            {
                                MfInterop.UnpackSize(packed2, out _width, out _height);
                            }
                            Marshal.ReleaseComObject(current);
                        }
                    }

                    MFT_OUTPUT_STREAM_INFO info;
                    _transform.GetOutputStreamInfo(0, out info);
                    OutputBufferSize = Math.Max(info.cbSize, _width * _height * 3 / 2);
                    if (OutputBufferSize <= 0) OutputBufferSize = _width * _height * 3 / 2;
                    return;
                }

                Marshal.ReleaseComObject(type);
            }
        }

        private IMFSample CreateSample(byte[] data)
        {
            IMFMediaBuffer buffer;
            MfInterop.Check(MfInterop.MFCreateMemoryBuffer(data.Length, out buffer), "创建内存缓冲区");
            IntPtr ptr;
            int maxLength, currentLength;
            MfInterop.Check(buffer.Lock(out ptr, out maxLength, out currentLength), "锁定缓冲区");
            Marshal.Copy(data, 0, ptr, data.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(data.Length);

            IMFSample sample;
            MfInterop.Check(MfInterop.MFCreateSample(out sample), "创建样本");
            sample.AddBuffer(buffer);
            Marshal.ReleaseComObject(buffer);

            sample.SetSampleTime(_sampleTime);
            sample.SetSampleDuration(333333); // 30fps 参考时长，仅用于排序
            _sampleTime += 333333;
            return sample;
        }

        private int Drain()
        {
            int produced = 0;

            while (true)
            {
                IMFMediaBuffer outBuffer;
                MfInterop.Check(MfInterop.MFCreateMemoryBuffer(OutputBufferSize, out outBuffer), "创建输出缓冲区");

                IMFSample outSample;
                MfInterop.Check(MfInterop.MFCreateSample(out outSample), "创建输出样本");
                outSample.AddBuffer(outBuffer);

                var dataBuffer = new MFT_OUTPUT_DATA_BUFFER();
                dataBuffer.dwStreamID = 0;
                dataBuffer.pSample = outSample;

                int status;
                int hr = _transform.ProcessOutput(0, 1, ref dataBuffer, out status);
                if (Verbose && _diagOutput++ < 10)
                {
                    Console.WriteLine("[MFT] ProcessOutput hr=0x" + hr.ToString("X8") + " status=0x" + status.ToString("X8")
                        + " sample=" + (dataBuffer.pSample != null ? "有" : "无"));
                }

                // 样本已交给 MFT 后，我们持有的缓冲区引用即可释放（样本内部自持一份）
                Marshal.ReleaseComObject(outBuffer);

                IMFSample producedSample = dataBuffer.pSample;
                bool sampleIsOurs = ReferenceEquals(producedSample, outSample);
                if (!sampleIsOurs) Marshal.ReleaseComObject(outSample);

                if (dataBuffer.pEvents != IntPtr.Zero)
                {
                    Marshal.Release(dataBuffer.pEvents);
                    dataBuffer.pEvents = IntPtr.Zero;
                }

                if (hr == MfInterop.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    if (producedSample != null) Marshal.ReleaseComObject(producedSample);
                    // 分辨率/编码参数变化：重新协商输出类型
                    ConfigureOutputType();
                    continue;
                }

                if (hr < 0 || producedSample == null)
                {
                    if (producedSample != null) Marshal.ReleaseComObject(producedSample);
                    if (hr == MfInterop.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                    break;
                }

                IMFMediaBuffer buffer;
                if (producedSample.ConvertToContiguousBuffer(out buffer) == 0)
                {
                    IntPtr ptr;
                    int maxLength, currentLength;
                    if (buffer.Lock(out ptr, out maxLength, out currentLength) == 0)
                    {
                        DecodedFrames++;
                        produced++;
                        var handler = FrameDecoded;
                        if (handler != null) handler(ptr, currentLength, _width, _height);
                        buffer.Unlock();
                    }
                    Marshal.ReleaseComObject(buffer);
                }
                Marshal.ReleaseComObject(producedSample);
            }

            return produced;
        }

        /// <summary>
        /// 从访问单元中提取 SPS(7)/PPS(8)，按 AVCC（4 字节长度前缀）拼成 MF 需要的序列头。
        /// </summary>
        private static byte[] ExtractParameterSets(byte[] au, bool annexB)
        {
            List<byte> result = new List<byte>();
            foreach (var nal in SplitNals(au))
            {
                if (nal.Length == 0) continue;
                int type = nal[0] & 0x1F;
                if (type != 7 && type != 8) continue;

                if (annexB)
                {
                    result.Add(0); result.Add(0); result.Add(0); result.Add(1);
                }
                else
                {
                    int len = nal.Length;
                    result.Add((byte)(len >> 24));
                    result.Add((byte)(len >> 16));
                    result.Add((byte)(len >> 8));
                    result.Add((byte)len);
                }
                result.AddRange(nal);
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>Annex-B 转 AVCC：每个 NAL 前加 4 字节大端长度。</summary>
        private static byte[] AnnexBToAvcc(byte[] au)
        {
            using (var ms = new MemoryStream(au.Length + 64))
            {
                foreach (var nal in SplitNals(au))
                {
                    if (nal.Length == 0) continue;
                    int len = nal.Length;
                    ms.WriteByte((byte)(len >> 24));
                    ms.WriteByte((byte)(len >> 16));
                    ms.WriteByte((byte)(len >> 8));
                    ms.WriteByte((byte)len);
                    ms.Write(nal, 0, nal.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>按起始码拆分 NAL（不含起始码）。</summary>
        private static IEnumerable<byte[]> SplitNals(byte[] data)
        {
            int pos = 0;
            int nalStart = -1;
            while (true)
            {
                int sc = FindStartCode(data, pos);
                if (sc < 0)
                {
                    if (nalStart >= 0 && nalStart < data.Length)
                    {
                        yield return Slice(data, nalStart, data.Length - nalStart);
                    }
                    yield break;
                }

                if (nalStart >= 0)
                {
                    int end = sc;
                    while (end > nalStart && data[end - 1] == 0) end--; // 去掉尾部的 0
                    if (end > nalStart) yield return Slice(data, nalStart, end - nalStart);
                }

                pos = sc + 3;
                nalStart = pos;
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

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _transform.ProcessMessage(MfInterop.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_transform); } catch { }
        }
    }
}
