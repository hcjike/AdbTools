using System;
using System.Threading.Tasks;

namespace RemoteProbe
{
    /// <summary>NV12 → BGRA 颜色转换（用于 WPF 显示前验证 CPU 开销）。</summary>
    internal static class Nv12Converter
    {
        /// <summary>把 NV12 数据写入 bgra 缓冲；返回 false 表示数据长度不足。</summary>
        public static bool Convert(IntPtr nv12, int length, int width, int height, byte[] bgra)
        {
            int frameSize = width * height;
            if (length < frameSize + frameSize / 2) return false;
            if (bgra == null || bgra.Length < frameSize * 4) return false;

            unsafe
            {
                var p = (byte*)nv12;
                byte* yPlane = p;
                byte* uvPlane = p + frameSize;

                Parallel.For(0, height, y =>
                {
                    int yRow = y * width;
                    int uvRow = (y >> 1) * width;
                    int outRow = yRow * 4;

                    for (int x = 0; x < width; x++)
                    {
                        int yv = yPlane[yRow + x];
                        int uvIndex = uvRow + (x & ~1);
                        int u = uvPlane[uvIndex] - 128;
                        int v = uvPlane[uvIndex + 1] - 128;

                        int c = yv - 16;
                        if (c < 0) c = 0;
                        int r = (298 * c + 409 * v + 128) >> 8;
                        int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                        int b = (298 * c + 516 * u + 128) >> 8;

                        if (r < 0) r = 0; else if (r > 255) r = 255;
                        if (g < 0) g = 0; else if (g > 255) g = 255;
                        if (b < 0) b = 0; else if (b > 255) b = 255;

                        int o = outRow + x * 4;
                        bgra[o] = (byte)b;
                        bgra[o + 1] = (byte)g;
                        bgra[o + 2] = (byte)r;
                        bgra[o + 3] = 255;
                    }
                });
            }

            return true;
        }
    }
}
