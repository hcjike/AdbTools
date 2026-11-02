using System;
using System.IO;

namespace AdbTools.RemoteControl
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

        /// <summary>保护缓冲区与当前状态的锁（Feed 在取流线程，FlushPending 在定时器线程）。</summary>
        private readonly object _sync = new object();

        /// <summary>最后一次收到数据的时刻（Environment.TickCount），用于判断码流是否已“静止”。</summary>
        private int _lastDataTick;

        /// <summary>完整访问单元回调：参数为带起始码的码流数据和是否为关键帧。</summary>
        public event Action<byte[], bool> AccessUnit;

        public int AccessUnitCount { get; private set; }
        public int KeyFrameCount { get; private set; }
        public long TotalAuBytes { get; private set; }
        public int LastAuSize { get; private set; }

        public void Feed(byte[] data, int count)
        {
            lock (_sync)
            {
                EnsureCapacity(_length + count);
                Buffer.BlockCopy(data, 0, _buffer, _length, count);
                _length += count;
                _lastDataTick = Environment.TickCount;
                Parse();
            }
        }

        /// <summary>
        /// 画面静止时屏幕不再变化，最后一帧的起始码之后不会再有新的 NAL 到达，
        /// 仅靠“下一个 NAL”判断帧边界会导致这一帧永远送不进解码器（首帧迟迟不出现、
        /// 且画面总是慢一拍）。此处在一段时间没有新数据后，把已完整的帧补发出去。
        /// </summary>
        public void FlushPending(int idleMilliseconds = 120)
        {
            lock (_sync)
            {
                if (unchecked(Environment.TickCount - _lastDataTick) < idleMilliseconds) return;
                if (_length == 0 && !_currentAuHasVcl) return;

                // 缓冲区里最后一个 NAL 尚未闭合（等不到下一个起始码）：按完整 NAL 处理
                int start = FindStartCode(0);
                if (start >= 0 && start + 3 < _length)
                {
                    HandleNal(start, _length - start);
                    _length = 0;
                }

                if (_currentAuHasVcl) FlushAu();
            }
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
