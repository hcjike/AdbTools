using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

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
        private const ushort ChunkXmlEndElement = 0x0103;
        private const ushort ChunkXmlResourceMap = 0x0180;
        private const ushort ChunkTablePackage = 0x0200;
        private const ushort ChunkTableType = 0x0201;

        private const byte ValueTypeReference = 0x01;
        private const byte ValueTypeString = 0x03;
        private const byte ValueTypeFloat = 0x04;
        private const byte ValueTypeIntDec = 0x10;
        private const byte ValueTypeIntHex = 0x11;
        private const byte ValueTypeColorArgb8 = 0x1C;
        private const byte ValueTypeColorRgb8 = 0x1D;
        private const byte ValueTypeColorArgb4 = 0x1E;
        private const byte ValueTypeColorRgb4 = 0x1F;
        private const byte ValueTypeIntBoolean = 0x12;

        /// <summary>android:label / icon / roundIcon / drawable 的属性资源 id。</summary>
        private const uint AndroidLabelAttrId = 0x01010001;
        private const uint AndroidIconAttrId = 0x01010002;
        private const uint AndroidDrawableAttrId = 0x01010199;
        private const uint AndroidRoundIconAttrId = 0x0101052C;

        /// <summary>
        /// 解析应用名。任一环节失败都返回 null，由调用方回退到包名。
        /// </summary>
        public static string ReadLabel(byte[] manifest, byte[] arsc)
        {
            uint labelResId;
            string literal;
            if (!TryReadApplicationAttribute(manifest, AndroidLabelAttrId, "label", out labelResId, out literal))
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

        /// <summary>读取 android:icon（没有时退回 android:roundIcon）的资源 id；0 表示应用没有声明图标。</summary>
        public static uint ReadIconResourceId(byte[] manifest)
        {
            uint resId;
            string literal;

            if (TryReadApplicationAttribute(manifest, AndroidIconAttrId, "icon", out resId, out literal) && resId != 0)
            {
                return resId;
            }

            if (TryReadApplicationAttribute(manifest, AndroidRoundIconAttrId, "roundIcon", out resId, out literal) && resId != 0)
            {
                return resId;
            }

            return 0;
        }

        /// <summary>
        /// 按资源 id 取文件在 APK 内的路径（如 res/mipmap-xxxhdpi-v4/ic_launcher.png）。
        /// 图标通常有多份密度版本，这里取最高密度；adaptive-icon 会返回 .xml 路径。
        /// </summary>
        public static string ResolveFilePath(byte[] arsc, uint resId)
        {
            if (arsc == null || arsc.Length < 12 || resId == 0)
            {
                return null;
            }

            ResTable table = new ResTable(arsc);
            return table.ResolveStringByDensity(resId, 0);
        }

        /// <summary>解析 adaptive-icon XML 中 foreground 的 android:drawable 资源 id；0 表示解析不到。</summary>
        public static uint ParseAdaptiveIconForeground(byte[] xml)
        {
            if (xml == null || xml.Length < 8)
            {
                return 0;
            }

            ResStringPool pool = null;
            uint[] resourceMap = null;
            int pos = 8;

            while (pos + 8 <= xml.Length)
            {
                ushort type = ChunkReader.U16(xml, pos);
                ushort headerSize = ChunkReader.U16(xml, pos + 2);
                uint size = ChunkReader.U32(xml, pos + 4);
                if (size < 8 || pos + (long)size > xml.Length)
                {
                    return 0;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(xml, pos);
                }
                else if (type == ChunkXmlResourceMap)
                {
                    resourceMap = ReadResourceMap(xml, pos, headerSize, size);
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    int nameIndex = ChunkReader.I32(xml, pos + headerSize + 4);
                    if (pool.Get(nameIndex) == "foreground")
                    {
                        uint resId;
                        string literal;
                        if (TryReadAttributeValue(xml, pos, headerSize, pool, resourceMap,
                                AndroidDrawableAttrId, "drawable", out resId, out literal) && resId != 0)
                        {
                            return resId;
                        }
                    }
                }

                pos += (int)size;
            }

            return 0;
        }

        /// <summary>读取 adaptive-icon 的 background：只有能确定是纯色时才返回，取不到（如背景也是矢量图）返回 false。</summary>
        public static bool TryReadAdaptiveIconBackgroundColor(byte[] xml, byte[] arsc, out uint argb)
        {
            argb = 0;
            if (xml == null || xml.Length < 8)
            {
                return false;
            }

            ResStringPool pool = null;
            int pos = 8;
            while (pos + 8 <= xml.Length)
            {
                ushort type = ChunkReader.U16(xml, pos);
                ushort headerSize = ChunkReader.U16(xml, pos + 2);
                uint size = ChunkReader.U32(xml, pos + 4);
                if (size < 8 || pos + (long)size > xml.Length)
                {
                    return false;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(xml, pos);
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    int nameIndex = ChunkReader.I32(xml, pos + headerSize + 4);
                    if (pool.Get(nameIndex) == "background")
                    {
                        return TryAttrColorStrict(ReadAttributes(xml, pos, headerSize, pool), "drawable", arsc, out argb);
                    }
                }

                pos += (int)size;
            }

            return false;
        }

        // ---------------------------------------------------------------- vector drawable

        /// <summary>
        /// 解析 VectorDrawable（adaptive-icon 前景层常见的最终形态），取出视口尺寸、路径与颜色。
        /// 只处理纯色填充/描边；渐变、clip-path 等复杂结构直接忽略，解析失败返回 null。
        /// </summary>
        public static VectorIcon ParseVectorDrawable(byte[] xml, byte[] arsc)
        {
            if (xml == null || xml.Length < 8)
            {
                return null;
            }

            VectorIcon icon = new VectorIcon { ViewportWidth = 24, ViewportHeight = 24 };

            ResStringPool pool = null;
            Stack<Matrix> transforms = new Stack<Matrix>();
            transforms.Push(Matrix.Identity);
            int pos = 8;

            while (pos + 8 <= xml.Length)
            {
                ushort type = ChunkReader.U16(xml, pos);
                ushort headerSize = ChunkReader.U16(xml, pos + 2);
                uint size = ChunkReader.U32(xml, pos + 4);
                if (size < 8 || pos + (long)size > xml.Length)
                {
                    break;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(xml, pos);
                }
                else if (type == ChunkXmlEndElement)
                {
                    // 每个开始元素都压了一层，结束元素对应弹出，保证 group 变换的作用范围正确
                    if (transforms.Count > 1) transforms.Pop();
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    int nameIndex = ChunkReader.I32(xml, pos + headerSize + 4);
                    string element = pool.Get(nameIndex);
                    List<XmlAttr> attrs = ReadAttributes(xml, pos, headerSize, pool);
                    Matrix current = transforms.Peek();

                    if (element == "group")
                    {
                        current = Matrix.Multiply(current, BuildGroupMatrix(attrs));
                    }
                    else if (element == "path")
                    {
                        VectorIconPath path = BuildPath(attrs, arsc, current);
                        if (path != null) icon.Paths.Add(path);
                    }
                    else if (element == "vector")
                    {
                        icon.ViewportWidth = AttrDouble(attrs, "viewportWidth", icon.ViewportWidth);
                        icon.ViewportHeight = AttrDouble(attrs, "viewportHeight", icon.ViewportHeight);
                    }

                    transforms.Push(current);
                }

                pos += (int)size;
            }

            return icon.Paths.Count == 0 ? null : icon;
        }

        private struct XmlAttr
        {
            public string Name;
            public byte ValueType;
            public uint ValueData;
            public string Literal;
        }

        /// <summary>读取一个 XML 开始元素的全部属性（按名字匹配，不依赖资源 id，兼容缺 resource map 的 APK）。</summary>
        private static List<XmlAttr> ReadAttributes(byte[] data, int chunkPos, int headerSize, ResStringPool pool)
        {
            List<XmlAttr> attrs = new List<XmlAttr>();
            int ext = chunkPos + headerSize;
            if (ext + 16 > data.Length)
            {
                return attrs;
            }

            int attributeStart = ChunkReader.U16(data, ext + 8);
            int attributeSize = ChunkReader.U16(data, ext + 10);
            int attributeCount = ChunkReader.U16(data, ext + 12);
            if (attributeSize < 20 || attributeCount <= 0)
            {
                return attrs;
            }

            int attrPos = ext + attributeStart;
            for (int i = 0; i < attributeCount; i++)
            {
                int a = attrPos + i * attributeSize;
                if (a + 20 > data.Length)
                {
                    break;
                }

                XmlAttr attr = new XmlAttr
                {
                    Name = pool.Get(ChunkReader.I32(data, a + 4)),
                    ValueType = data[a + 15],
                    ValueData = ChunkReader.U32(data, a + 16)
                };

                if (attr.ValueType == ValueTypeString)
                {
                    attr.Literal = pool.Get((int)attr.ValueData);
                }
                else
                {
                    // 少数属性同时带一份字符串形式的原始值
                    int rawIndex = ChunkReader.I32(data, a + 8);
                    if (rawIndex >= 0) attr.Literal = pool.Get(rawIndex);
                }

                if (!string.IsNullOrEmpty(attr.Name))
                {
                    attrs.Add(attr);
                }
            }

            return attrs;
        }

        private static bool TryGetAttr(List<XmlAttr> attrs, string name, out XmlAttr value)
        {
            foreach (XmlAttr attr in attrs)
            {
                if (attr.Name == name)
                {
                    value = attr;
                    return true;
                }
            }

            value = new XmlAttr();
            return false;
        }

        private static string AttrString(List<XmlAttr> attrs, string name)
        {
            XmlAttr attr;
            return TryGetAttr(attrs, name, out attr) ? attr.Literal : null;
        }

        private static double AttrDouble(List<XmlAttr> attrs, string name, double fallback)
        {
            XmlAttr attr;
            if (!TryGetAttr(attrs, name, out attr))
            {
                return fallback;
            }

            switch (attr.ValueType)
            {
                case ValueTypeFloat:
                    return BitConverter.ToSingle(BitConverter.GetBytes(attr.ValueData), 0);
                case ValueTypeIntDec:
                case ValueTypeIntHex:
                    return unchecked((int)attr.ValueData);
            }

            double parsed;
            if (attr.Literal != null
                && double.TryParse(attr.Literal, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        /// <summary>按 Android 的语义合成 group 变换：先移到 pivot 之外，缩放、旋转，再移回去并叠加平移。</summary>
        private static Matrix BuildGroupMatrix(List<XmlAttr> attrs)
        {
            double translateX = AttrDouble(attrs, "translateX", 0);
            double translateY = AttrDouble(attrs, "translateY", 0);
            double scaleX = AttrDouble(attrs, "scaleX", 1);
            double scaleY = AttrDouble(attrs, "scaleY", 1);
            double rotate = AttrDouble(attrs, "rotate", 0);
            double pivotX = AttrDouble(attrs, "pivotX", 0);
            double pivotY = AttrDouble(attrs, "pivotY", 0);

            Matrix matrix = Matrix.Identity;
            matrix.Translate(-pivotX, -pivotY);
            matrix.Scale(scaleX, scaleY);
            matrix.Rotate(rotate);
            matrix.Translate(pivotX + translateX, pivotY + translateY);
            return matrix;
        }

        private static VectorIconPath BuildPath(List<XmlAttr> attrs, byte[] arsc, Matrix transform)
        {
            string data = AttrString(attrs, "pathData");
            if (string.IsNullOrEmpty(data))
            {
                return null;
            }

            double fillAlpha = AttrDouble(attrs, "fillAlpha", 1);
            VectorIconPath path = new VectorIconPath
            {
                Data = data,
                Transform = transform,
                FillAlpha = fillAlpha <= 0 ? 1 : fillAlpha,
                StrokeWidth = AttrDouble(attrs, "strokeWidth", 0),
                EvenOdd = string.Equals(AttrString(attrs, "fillType"), "evenOdd", StringComparison.OrdinalIgnoreCase)
            };

            uint color;
            if (TryGetAttr(attrs, "fillColor", out _) && TryAttrColor(attrs, "fillColor", arsc, out color))
            {
                path.HasFill = true;
                path.FillColor = color;
            }

            if (TryAttrColor(attrs, "strokeColor", arsc, out color))
            {
                path.StrokeColor = color;
            }

            return path;
        }

        /// <summary>取颜色属性：字面量色值、编译后的颜色 int、或 @color 引用都支持；取不到时按黑色兜底。</summary>
        private static bool TryAttrColor(List<XmlAttr> attrs, string name, byte[] arsc, out uint argb)
        {
            if (TryAttrColorStrict(attrs, name, arsc, out argb))
            {
                return true;
            }

            argb = 0xFF000000;
            return TryGetAttr(attrs, name, out _);
        }

        private static bool TryAttrColorStrict(List<XmlAttr> attrs, string name, byte[] arsc, out uint argb)
        {
            argb = 0;
            XmlAttr attr;
            if (!TryGetAttr(attrs, name, out attr))
            {
                return false;
            }

            if (TryConvertColor(attr.ValueType, attr.ValueData, out argb))
            {
                return true;
            }

            if (attr.ValueType == ValueTypeString && TryParseColorText(attr.Literal, out argb))
            {
                return true;
            }

            if (attr.ValueType == ValueTypeReference && arsc != null && arsc.Length >= 12)
            {
                return new ResTable(arsc).TryResolveColor(attr.ValueData, 0, out argb);
            }

            return false;
        }

        private static bool TryConvertColor(byte valueType, uint data, out uint argb)
        {
            argb = 0;
            switch (valueType)
            {
                case ValueTypeColorArgb8:
                    argb = data;
                    return true;
                case ValueTypeColorRgb8:
                    argb = 0xFF000000 | data;
                    return true;
                case ValueTypeColorArgb4:
                    argb = ExpandNibbles((data >> 12) & 0xF, (data >> 8) & 0xF, (data >> 4) & 0xF, data & 0xF);
                    return true;
                case ValueTypeColorRgb4:
                    argb = ExpandNibbles(0xF, (data >> 8) & 0xF, (data >> 4) & 0xF, data & 0xF);
                    return true;
                default:
                    return false;
            }
        }

        private static uint ExpandNibbles(uint a, uint r, uint g, uint b)
        {
            return ((a * 0x11) << 24) | ((r * 0x11) << 16) | ((g * 0x11) << 8) | (b * 0x11);
        }

        /// <summary>解析 #RGB / #ARGB / #RRGGBB / #AARRGGBB 形式的颜色文本。</summary>
        private static bool TryParseColorText(string text, out uint argb)
        {
            argb = 0;
            if (string.IsNullOrEmpty(text) || text[0] != '#' || text.Length < 4)
            {
                return false;
            }

            string hex = text.Substring(1);
            uint value;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            switch (hex.Length)
            {
                case 3:
                    argb = ExpandNibbles(0xF, (value >> 8) & 0xF, (value >> 4) & 0xF, value & 0xF);
                    return true;
                case 4:
                    argb = ExpandNibbles((value >> 12) & 0xF, (value >> 8) & 0xF, (value >> 4) & 0xF, value & 0xF);
                    return true;
                case 6:
                    argb = 0xFF000000 | value;
                    return true;
                case 8:
                    argb = value;
                    return true;
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- system service

        /// <summary>
        /// 判断应用是不是"系统服务"：系统应用里「无桌面入口」或「声明常驻（persistent）」
        /// 满足其一即算。manifest 取不到时不做判定（保守不标，避免误报）。
        /// </summary>
        public static bool IsSystemService(byte[] manifest, bool isSystem)
        {
            if (!isSystem)
            {
                return false;
            }

            if (manifest == null || manifest.Length < 8)
            {
                return false;
            }

            return IsPersistentApp(manifest) || !HasLauncherEntry(manifest);
        }

        /// <summary>manifest 里是否有 activity/activity-alias 声明了 LAUNCHER 或 LEANBACK_LAUNCHER 入口。</summary>
        public static bool HasLauncherEntry(byte[] manifest)
        {
            if (manifest == null || manifest.Length < 8)
            {
                return false;
            }

            ResStringPool pool = null;
            Stack<string> elements = new Stack<string>();
            int pos = 8;

            while (pos + 8 <= manifest.Length)
            {
                ushort type = ChunkReader.U16(manifest, pos);
                ushort headerSize = ChunkReader.U16(manifest, pos + 2);
                uint size = ChunkReader.U32(manifest, pos + 4);
                if (size < 8 || pos + (long)size > manifest.Length)
                {
                    return false;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(manifest, pos);
                }
                else if (type == ChunkXmlEndElement)
                {
                    if (elements.Count > 0) elements.Pop();
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    int nameIndex = ChunkReader.I32(manifest, pos + headerSize + 4);
                    string element = pool.Get(nameIndex);

                    if (element == "category" && IsInsideActivity(elements))
                    {
                        string category = AttrString(ReadAttributes(manifest, pos, headerSize, pool), "name");
                        if (category == "android.intent.category.LAUNCHER"
                            || category == "android.intent.category.LEANBACK_LAUNCHER")
                        {
                            return true;
                        }
                    }

                    elements.Push(element);
                }

                pos += (int)size;
            }

            return false;
        }

        /// <summary>manifest 的 application 是否声明 android:persistent="true"（常驻系统进程）。</summary>
        public static bool IsPersistentApp(byte[] manifest)
        {
            if (manifest == null || manifest.Length < 8)
            {
                return false;
            }

            ResStringPool pool = null;
            int pos = 8;
            while (pos + 8 <= manifest.Length)
            {
                ushort type = ChunkReader.U16(manifest, pos);
                ushort headerSize = ChunkReader.U16(manifest, pos + 2);
                uint size = ChunkReader.U32(manifest, pos + 4);
                if (size < 8 || pos + (long)size > manifest.Length)
                {
                    return false;
                }

                if (type == ChunkStringPool)
                {
                    pool = new ResStringPool(manifest, pos);
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    int nameIndex = ChunkReader.I32(manifest, pos + headerSize + 4);
                    if (pool.Get(nameIndex) == "application")
                    {
                        XmlAttr attr;
                        if (TryGetAttr(ReadAttributes(manifest, pos, headerSize, pool), "persistent", out attr))
                        {
                            if (attr.ValueType == ValueTypeIntBoolean)
                            {
                                return attr.ValueData != 0;
                            }

                            if (attr.Literal != null)
                            {
                                return attr.Literal.Trim().ToLowerInvariant() == "true";
                            }
                        }
                        return false;
                    }
                }

                pos += (int)size;
            }

            return false;
        }

        private static bool IsInsideActivity(Stack<string> elements)
        {
            foreach (string element in elements)
            {
                if (element == "activity" || element == "activity-alias")
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- manifest

        /// <summary>在 &lt;application&gt; 元素上读取指定属性（按资源 id 或属性名匹配）。</summary>
        private static bool TryReadApplicationAttribute(byte[] data, uint attrId, string attrName,
            out uint resId, out string literal)
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
                    resourceMap = ReadResourceMap(data, pos, headerSize, size);
                }
                else if (type == ChunkXmlStartElement && pool != null)
                {
                    // ResXMLTree_node 之后是 attrExt：ns(4) 在前，元素名在 +4
                    int nameIndex = ChunkReader.I32(data, pos + headerSize + 4);
                    if (pool.Get(nameIndex) == "application"
                        && TryReadAttributeValue(data, pos, headerSize, pool, resourceMap, attrId, attrName,
                            out resId, out literal))
                    {
                        return true;
                    }
                }

                pos += (int)size;
            }

            return false;
        }

        private static uint[] ReadResourceMap(byte[] data, int chunkPos, int headerSize, uint size)
        {
            int count = (int)((size - headerSize) / 4);
            if (count <= 0)
            {
                return null;
            }

            uint[] map = new uint[count];
            for (int i = 0; i < count; i++)
            {
                map[i] = ChunkReader.U32(data, chunkPos + headerSize + i * 4);
            }
            return map;
        }

        private static bool TryReadAttributeValue(byte[] data, int chunkPos, int headerSize,
            ResStringPool pool, uint[] resourceMap, uint attrId, string attrName,
            out uint resId, out string literal)
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

                bool matched = nameResId == attrId
                    || (nameResId == 0 && pool.Get(nameIndex) == attrName);
                if (!matched)
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

                // 少数 APK 直接把值写在 rawValue 里
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

            /// <summary>按密度优先取值，用于图标这类"多份密度副本"的资源。</summary>
            public string ResolveStringByDensity(uint resId, int depth)
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
                    if (!package.TryGetEntryByDensity(typeId, entryIndex, out valueType, out valueData))
                    {
                        continue;
                    }

                    if (valueType == ValueTypeString)
                    {
                        return _values.Get((int)valueData);
                    }
                    if (valueType == ValueTypeReference)
                    {
                        return ResolveStringByDensity(valueData, depth + 1);
                    }
                    return null;
                }

                return null;
            }

            /// <summary>按资源 id 解析颜色（@color/xxx），支持编译后的各种颜色 int 形态。</summary>
            public bool TryResolveColor(uint resId, int depth, out uint argb)
            {
                argb = 0;
                if (depth > 4)
                {
                    return false;
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

                    if (valueType == ValueTypeReference)
                    {
                        return TryResolveColor(valueData, depth + 1, out argb);
                    }

                    if (TryConvertColor(valueType, valueData, out argb))
                    {
                        return true;
                    }

                    if (valueType == ValueTypeString && _values != null
                        && TryParseColorText(_values.Get((int)valueData), out argb))
                    {
                        return true;
                    }

                    return false;
                }

                return false;
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

            /// <summary>同样的 TYPE 块，按密度从高到低排序，供图标这类资源使用。</summary>
            private readonly Dictionary<byte, List<int>> _typesByDensity = new Dictionary<byte, List<int>>();

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

                foreach (KeyValuePair<byte, List<int>> pair in _types)
                {
                    pair.Value.Sort((a, b) => ConfigRank(a).CompareTo(ConfigRank(b)));

                    List<int> byDensity = new List<int>(pair.Value);
                    byDensity.Sort((a, b) => DensityRank(a).CompareTo(DensityRank(b)));
                    _typesByDensity[pair.Key] = byDensity;
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

            /// <summary>按密度从高到低取值：图标有多份副本时取最清晰的那份。</summary>
            public bool TryGetEntryByDensity(byte typeId, int entryIndex, out byte valueType, out uint valueData)
            {
                valueType = 0;
                valueData = 0;

                List<int> list;
                if (!_typesByDensity.TryGetValue(typeId, out list))
                {
                    return false;
                }

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
            /// 密度优先级：真实密度越高越靠前；未指定密度按中密度算；
            /// anydpi/nodpi（一般是矢量或 adaptive-icon 的 XML）排最后，避免抢在真实位图前面。
            /// </summary>
            private int DensityRank(int chunkPos)
            {
                int headerSize = ChunkReader.U16(_data, chunkPos + 2);
                int configStart = chunkPos + 20;
                int configSize = headerSize - 20;
                // ResTable_config：screenType 在 +12，其中 orientation(1) touchscreen(1) density(2)
                if (configSize < 16 || configStart + 16 > _data.Length)
                {
                    return 12000;
                }

                int density = ChunkReader.U16(_data, configStart + 14);
                if (density == 0)
                {
                    return 9840; // 未指定，按 mdpi 处理
                }
                if (density == 0xFFFE || density == 0xFFFF)
                {
                    return 20000; // anydpi / nodpi
                }
                return 10000 - density;
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
