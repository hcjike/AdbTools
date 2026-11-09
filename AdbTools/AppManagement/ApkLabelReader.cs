using System;
using System.Collections.Generic;

namespace AdbTools
{
    /// <summary>
    /// 从 APK 内的 AndroidManifest.xml 与 resources.arsc 解析应用显示名。
    /// 设备端只负责把这两个文件吐出来，解析在本机完成，因此不需要 aapt 等外部工具。
    /// </summary>
    internal static class ApkLabelReader
    {
        private const ushort ChunkStringPool = 0x0001;
        private const ushort ChunkXml = 0x0003;
        // ResourceTypes.h：0x0100/0x0101 是命名空间，元素节点是 0x0102/0x0103
        private const ushort ChunkXmlStartElement = 0x0102;
        private const ushort ChunkXmlResourceMap = 0x0180;
        private const ushort ChunkTablePackage = 0x0200;
        private const ushort ChunkTableType = 0x0201;

        private const byte ValueTypeReference = 0x01;
        private const byte ValueTypeString = 0x03;

        /// <summary>android:label 的属性资源 id。</summary>
        private const uint AndroidLabelAttrId = 0x01010001;

        /// <summary>
        /// 解析应用名。任一环节失败都返回 null，由调用方回退到包名。
        /// </summary>
        public static string ReadLabel(byte[] manifest, byte[] arsc)
        {
            uint labelResId;
            string literal;
            if (!TryReadManifestLabel(manifest, out labelResId, out literal))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(literal))
            {
                return literal.Trim();
            }

            if (labelResId == 0 || arsc == null || arsc.Length < 12)
            {
                return null;
            }

            ResTable table = new ResTable(arsc);
            string text = table.ResolveString(labelResId, 0);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        // ---------------------------------------------------------------- manifest

        private static bool TryReadManifestLabel(byte[] data, out uint resId, out string literal)
        {
            resId = 0;
            literal = null;
            if (data == null || data.Length < 8)
            {
                return false;
            }

            ResStringPool pool = null;
            uint[] resourceMap = null;
            int pos = 8; // 跳过文件头

            while (pos + 8 <= data.Length)
            {
                ushort type = ChunkReader.U16(data, pos);
                ushort headerSize = ChunkReader.U16(data, pos + 2);
                uint size = ChunkReader.U32(data, pos + 4);
                if (size < 8 || pos + (long)size > data.Length)
                {
                    return false;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(data, pos);
                }
                else if (type == ChunkXmlResourceMap)
                {
                    int count = (int)((size - headerSize) / 4);
                    if (count > 0)
                    {
                        resourceMap = new uint[count];
                        for (int i = 0; i < count; i++)
                        {
                            resourceMap[i] = ChunkReader.U32(data, pos + headerSize + i * 4);
                        }
                    }
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    // ResXMLTree_node 之后是 attrExt：ns(4) 在前，元素名在 +4
                    int nameIndex = ChunkReader.I32(data, pos + headerSize + 4);
                    if (pool.Get(nameIndex) == "application"
                        && TryReadLabelAttribute(data, pos, headerSize, pool, resourceMap, out resId, out literal))
                    {
                        return true;
                    }
                }

                pos += (int)size;
            }

            return false;
        }

        private static bool TryReadLabelAttribute(byte[] data, int chunkPos, int headerSize,
            ResStringPool pool, uint[] resourceMap, out uint resId, out string literal)
        {
            resId = 0;
            literal = null;

            int ext = chunkPos + headerSize;
            if (ext + 16 > data.Length)
            {
                return false;
            }

            int attributeStart = ChunkReader.U16(data, ext + 8);
            int attributeSize = ChunkReader.U16(data, ext + 10);
            int attributeCount = ChunkReader.U16(data, ext + 12);
            if (attributeSize < 20 || attributeCount <= 0)
            {
                return false;
            }

            int attrPos = ext + attributeStart;
            for (int i = 0; i < attributeCount; i++)
            {
                int a = attrPos + i * attributeSize;
                if (a + 20 > data.Length)
                {
                    return false;
                }

                int nameIndex = ChunkReader.I32(data, a + 4);
                uint nameResId = resourceMap != null && nameIndex >= 0 && nameIndex < resourceMap.Length
                    ? resourceMap[nameIndex]
                    : 0;

                bool isLabel = nameResId == AndroidLabelAttrId
                    || (nameResId == 0 && pool.Get(nameIndex) == "label");
                if (!isLabel)
                {
                    continue;
                }

                byte valueType = data[a + 15];
                uint valueData = ChunkReader.U32(data, a + 16);
                if (valueType == ValueTypeString)
                {
                    literal = pool.Get((int)valueData);
                    return true;
                }
                if (valueType == ValueTypeReference)
                {
                    resId = valueData;
                    return true;
                }

                // 少数 APK 直接把名称写在 rawValue 里
                int rawIndex = ChunkReader.I32(data, a + 8);
                if (rawIndex >= 0)
                {
                    literal = pool.Get(rawIndex);
                    return true;
                }
                return false;
            }

            return false;
        }

        // ---------------------------------------------------------------- resources

        /// <summary>resources.arsc 的最小解析：按资源 id 取出字符串。</summary>
        private sealed class ResTable
        {
            private readonly byte[] _data;
            private readonly ResStringPool _values;
            private readonly List<PackageChunk> _packages = new List<PackageChunk>();

            public ResTable(byte[] data)
            {
                _data = data;
                // ResTable_header 头部长度为 12（8 字节 chunk 头 + packageCount）
                int headerSize = ChunkReader.U16(data, 2);
                int pos = headerSize >= 12 ? headerSize : 12;
                while (pos + 8 <= data.Length)
                {
                    ushort type = ChunkReader.U16(data, pos);
                    uint size = ChunkReader.U32(data, pos + 4);
                    if (size < 8 || pos + (long)size > data.Length)
                    {
                        break;
                    }

                    if (type == ChunkStringPool && _values == null)
                    {
                        _values = new ResStringPool(data, pos);
                    }
                    else if (type == ChunkTablePackage)
                    {
                        _packages.Add(new PackageChunk(data, pos));
                    }

                    pos += (int)size;
                }
            }

            public string ResolveString(uint resId, int depth)
            {
                if (depth > 4 || _values == null)
                {
                    return null;
                }

                uint packageId = resId >> 24;
                byte typeId = (byte)((resId >> 16) & 0xFF);
                int entryIndex = (int)(resId & 0xFFFF);

                foreach (PackageChunk package in _packages)
                {
                    if (package.Id != packageId)
                    {
                        continue;
                    }

                    byte valueType;
                    uint valueData;
                    if (!package.TryGetEntry(typeId, entryIndex, out valueType, out valueData))
                    {
                        continue;
                    }

                    if (valueType == ValueTypeString)
                    {
                        return _values.Get((int)valueData);
                    }
                    if (valueType == ValueTypeReference)
                    {
                        return ResolveString(valueData, depth + 1);
                    }
                    return null;
                }

                return null;
            }
        }

        private sealed class PackageChunk
        {
            private readonly byte[] _data;
            private readonly int _end;
            private readonly int _typeIdOffset;

            /// <summary>
            /// 同一个类型可能有多个配置版本（简体中文、英文、不同密度等），
            /// 这里按语言优先级排好序，取值时依次尝试。
            /// </summary>
            private readonly Dictionary<byte, List<int>> _types = new Dictionary<byte, List<int>>();

            public PackageChunk(byte[] data, int start)
            {
                _data = data;
                _end = start + (int)ChunkReader.U32(data, start + 4);
                Id = ChunkReader.U32(data, start + 8);

                int headerSize = ChunkReader.U16(data, start + 2);
                _typeIdOffset = headerSize >= 288 ? ChunkReader.I32(data, start + 284) : 0;

                int pos = start + headerSize;
                while (pos + 8 <= _end && pos + 8 <= data.Length)
                {
                    ushort type = ChunkReader.U16(data, pos);
                    uint size = ChunkReader.U32(data, pos + 4);
                    if (size < 8 || pos + (long)size > _end)
                    {
                        break;
                    }

                    if (type == ChunkTableType)
                    {
                        byte typeId = (byte)(data[pos + 8] + _typeIdOffset);
                        List<int> list;
                        if (!_types.TryGetValue(typeId, out list))
                        {
                            list = new List<int>();
                            _types[typeId] = list;
                        }
                        list.Add(pos);
                    }

                    pos += (int)size;
                }

                foreach (List<int> list in _types.Values)
                {
                    list.Sort((a, b) => ConfigRank(a).CompareTo(ConfigRank(b)));
                }
            }

            public uint Id { get; private set; }

            public bool TryGetEntry(byte typeId, int entryIndex, out byte valueType, out uint valueData)
            {
                valueType = 0;
                valueData = 0;

                List<int> list;
                if (!_types.TryGetValue(typeId, out list))
                {
                    return false;
                }

                // 按语言优先级依次尝试，取第一个真正含有该条目的配置
                foreach (int chunkPos in list)
                {
                    if (TryReadEntry(chunkPos, entryIndex, out valueType, out valueData))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// 语言优先级：简体中文 0，其他中文 1，默认配置 2，其他语言 3。
            /// 默认配置通常是英文，中文环境下应尽量取到中文资源。
            /// </summary>
            private int ConfigRank(int chunkPos)
            {
                int headerSize = ChunkReader.U16(_data, chunkPos + 2);
                int configStart = chunkPos + 20;
                int configSize = headerSize - 20;
                // ResTable_config：size(4) + imsi(4) + locale(4)，language 在 +8，country 在 +10
                if (configSize < 12 || configStart + 12 > _data.Length)
                {
                    return 2;
                }

                byte l0 = _data[configStart + 8];
                byte l1 = _data[configStart + 9];
                byte c0 = _data[configStart + 10];
                byte c1 = _data[configStart + 11];

                if (l0 == 0 && l1 == 0)
                {
                    // 没有语言限定（默认配置，或仅密度/夜间等限定）
                    return 2;
                }

                if (l0 == (byte)'z' && l1 == (byte)'h')
                {
                    return c0 == (byte)'C' && c1 == (byte)'N' ? 0 : 1;
                }

                return 3;
            }

            private bool TryReadEntry(int chunkPos, int entryIndex, out byte valueType, out uint valueData)
            {
                valueType = 0;
                valueData = 0;

                int headerSize = ChunkReader.U16(_data, chunkPos + 2);
                byte flags = _data[chunkPos + 9];
                int entryCount = ChunkReader.I32(_data, chunkPos + 12);
                int entriesStart = ChunkReader.I32(_data, chunkPos + 16);
                int offsetsStart = chunkPos + headerSize;
                int entriesBase = chunkPos + entriesStart;
                if (entryCount <= 0 || entriesBase <= chunkPos || entriesBase > _data.Length)
                {
                    return false;
                }

                // FLAG_SPARSE：条目表是 (idx, offset/4) 的 4 字节对
                if ((flags & 0x01) != 0)
                {
                    for (int i = 0; i < entryCount; i++)
                    {
                        int p = offsetsStart + i * 4;
                        if (p + 4 > _data.Length) return false;
                        if (ChunkReader.U16(_data, p) != entryIndex) continue;
                        return ReadEntryAt(entriesBase + ChunkReader.U16(_data, p + 2) * 4, out valueType, out valueData);
                    }
                    return false;
                }

                // FLAG_OFFSET16：偏移是 uint16，单位 4 字节
                if ((flags & 0x02) != 0)
                {
                    if (entryIndex >= entryCount) return false;
                    int p = offsetsStart + entryIndex * 2;
                    if (p + 2 > _data.Length) return false;
                    int offset = ChunkReader.U16(_data, p);
                    if (offset == 0xFFFF) return false;
                    return ReadEntryAt(entriesBase + offset * 4, out valueType, out valueData);
                }

                if (entryIndex >= entryCount) return false;
                int pos = offsetsStart + entryIndex * 4;
                if (pos + 4 > _data.Length) return false;
                uint entryOffset = ChunkReader.U32(_data, pos);
                if (entryOffset == 0xFFFFFFFF) return false;
                return ReadEntryAt(entriesBase + (int)entryOffset, out valueType, out valueData);
            }

            private bool ReadEntryAt(int pos, out byte valueType, out uint valueData)
            {
                valueType = 0;
                valueData = 0;
                if (pos + 16 > _data.Length)
                {
                    return false;
                }

                // ResTable_entry：size(2) flags(2) key(4)，随后是 Res_value
                valueType = _data[pos + 11];
                valueData = ChunkReader.U32(_data, pos + 12);
                return true;
            }
        }

        // ---------------------------------------------------------------- strings

        private sealed class ResStringPool
        {
            private readonly byte[] _data;
            private readonly int _start;
            private readonly int _count;
            private readonly int _stringsStart;
            private readonly bool _utf8;

            public ResStringPool(byte[] data, int start)
            {
                _data = data;
                _start = start;

                int headerSize = ChunkReader.U16(data, start + 2);
                _count = ChunkReader.I32(data, start + 8);
                uint flags = ChunkReader.U32(data, start + 16);
                _stringsStart = ChunkReader.I32(data, start + 20);
                _utf8 = (flags & 0x100) != 0;
            }

            public string Get(int index)
            {
                if (index < 0 || index >= _count)
                {
                    return null;
                }

                int headerSize = ChunkReader.U16(_data, _start + 2);
                int offsetPos = _start + headerSize + index * 4;
                if (offsetPos + 4 > _data.Length)
                {
                    return null;
                }

                int pos = _start + _stringsStart + ChunkReader.I32(_data, offsetPos);
                if (pos < 0 || pos >= _data.Length)
                {
                    return null;
                }

                return _utf8 ? ReadUtf8(pos) : ReadUtf16(pos);
            }

            private string ReadUtf8(int pos)
            {
                ReadVarint8(ref pos);            // 字符数，不需要
                int byteLength = ReadVarint8(ref pos);
                if (byteLength <= 0 || pos + byteLength > _data.Length)
                {
                    return null;
                }
                return System.Text.Encoding.UTF8.GetString(_data, pos, byteLength);
            }

            private string ReadUtf16(int pos)
            {
                if (pos + 2 > _data.Length)
                {
                    return null;
                }

                int length = ChunkReader.U16(_data, pos);
                pos += 2;
                if ((length & 0x8000) != 0)
                {
                    if (pos + 2 > _data.Length) return null;
                    length = ((length & 0x7FFF) << 16) | ChunkReader.U16(_data, pos);
                    pos += 2;
                }

                if (length <= 0 || pos + length * 2 > _data.Length)
                {
                    return null;
                }
                return System.Text.Encoding.Unicode.GetString(_data, pos, length * 2);
            }

            private int ReadVarint8(ref int pos)
            {
                if (pos + 1 > _data.Length) return 0;
                int value = _data[pos++];
                if ((value & 0x80) != 0)
                {
                    if (pos + 1 > _data.Length) return 0;
                    value = ((value & 0x7F) << 8) | _data[pos++];
                }
                return value;
            }
        }
    }

    /// <summary>带边界保护的小端读取工具。</summary>
    internal static class ChunkReader
    {
        public static ushort U16(byte[] data, int pos)
        {
            if (pos < 0 || pos + 2 > data.Length) return 0;
            return (ushort)(data[pos] | (data[pos + 1] << 8));
        }

        public static int I32(byte[] data, int pos)
        {
            return unchecked((int)U32(data, pos));
        }

        public static uint U32(byte[] data, int pos)
        {
            if (pos < 0 || pos + 4 > data.Length) return 0;
            return (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        }
    }
}
