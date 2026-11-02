using System;
using System.IO;

namespace RemoteProbe
{
    /// <summary>
    /// Annex-B 码流拆分：先把字节流切成 NAL 单元，再按 H.264 规则组装访问单元（Access Unit）。
    /// 每个访问单元对应一帧，是送进解码器的基本单位。
    /// </summary>
    internal sealed class AccessUnitSplitter
    {
        private byte[] _buffer = new byte[512 * 1024];
        private int _length;

        private readonly MemoryStream _currentAu = new MemoryStream();
        private bool _currentAuHasVcl;
        private bool _currentAuIsKeyFrame;

        /// <summary>完整访问单元回调：参数为带起始码的码流数据和是否为关键帧。</summary>
        public event Action<byte[], bool> AccessUnit;

        public int AccessUnitCount { get; private set; }
        public int KeyFrameCount { get; private set; }
        public long TotalAuBytes { get; private set; }
        public int LastAuSize { get; private set; }

        public void Feed(byte[] data, int count)
        {
            EnsureCapacity(_length + count);
            Buffer.BlockCopy(data, 0, _buffer, _length, count);
            _length += count;
            Parse();
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer.Length) return;
            int size = _buffer.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _buffer, size);
        }

        private void Parse()
        {
            int consumed = 0;
            while (true)
            {
                int start = FindStartCode(consumed);
                if (start < 0)
                {
                    consumed = Math.Max(consumed, _length - 2);
                    break;
                }

                int next = FindStartCode(start + 3);
                if (next < 0)
                {
                    consumed = start;
                    break;
                }

                HandleNal(start, next - start);
                consumed = next;
            }

            if (consumed > 0)
            {
                _length -= consumed;
                if (_length > 0) Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _length);
            }
        }

        /// <summary>返回起始码位置（00 00 01），找不到返回 -1。</summary>
        private int FindStartCode(int from)
        {
            for (int i = Math.Max(from, 0); i + 2 < _length; i++)
            {
                if (_buffer[i] == 0 && _buffer[i + 1] == 0 && _buffer[i + 2] == 1) return i;
            }
            return -1;
        }

        private void HandleNal(int offset, int length)
        {
            int headerIndex = offset + 3;
            if (headerIndex >= offset + length) return;

            int type = _buffer[headerIndex] & 0x1F;
            bool isVcl = type == 1 || type == 5;
            bool startsNewAu = false;

            if (_currentAuHasVcl)
            {
                if (!isVcl)
                {
                    // 参数集 / SEI / AUD 出现在 VCL 之后，说明上一帧已结束
                    startsNewAu = true;
                }
                else if (IsFirstSlice(_buffer, headerIndex, offset + length))
                {
                    // 多 slice 帧：first_mb_in_slice>0 的 slice 属于同一帧
                    startsNewAu = true;
                }
            }

            if (startsNewAu) FlushAu();

            // 写入起始码 + NAL 数据（统一用 4 字节起始码）
            _currentAu.WriteByte(0);
            _currentAu.WriteByte(0);
            _currentAu.WriteByte(0);
            _currentAu.WriteByte(1);
            _currentAu.Write(_buffer, headerIndex, offset + length - headerIndex);

            if (isVcl)
            {
                _currentAuHasVcl = true;
                if (type == 5) _currentAuIsKeyFrame = true;
            }
        }

        private void FlushAu()
        {
            if (_currentAu.Length == 0) return;

            byte[] au = _currentAu.ToArray();
            bool isKeyFrame = _currentAuIsKeyFrame;

            LastAuSize = au.Length;
            TotalAuBytes += au.Length;
            AccessUnitCount++;
            if (isKeyFrame) KeyFrameCount++;

            _currentAu.SetLength(0);
            _currentAuHasVcl = false;
            _currentAuIsKeyFrame = false;

            AccessUnit?.Invoke(au, isKeyFrame);
        }

        /// <summary>
        /// 判断 slice 是否为帧的首个 slice（first_mb_in_slice == 0）。
        /// </summary>
        private static bool IsFirstSlice(byte[] data, int headerIndex, int end)
        {
            int bytePos = headerIndex + 1;
            int bitPos = 0;
            int zeros = 0;

            while (true)
            {
                if (bytePos >= end) return true;
                int bit = (data[bytePos] >> (7 - bitPos)) & 1;
                bitPos++;
                if (bitPos == 8) { bitPos = 0; bytePos++; }

                if (bit == 1) break;
                zeros++;
                if (zeros > 31) return true; // 码流异常，按新帧处理
            }

            if (zeros == 0) return true; // first_mb_in_slice == 0

            int value = 1;
            for (int i = 0; i < zeros; i++)
            {
                if (bytePos >= end) return true;
                int bit = (data[bytePos] >> (7 - bitPos)) & 1;
                bitPos++;
                if (bitPos == 8) { bitPos = 0; bytePos++; }
                value = (value << 1) | bit;
            }

            return value - 1 == 0;
        }
    }
}
