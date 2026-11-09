using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// WebP 解码。WPF 自带的 WIC 没有 WebP 解码器（除非用户额外装了商店的“WebP 图像扩展”），
    /// 所以随程序附带官方 libwebp.dll（BSD-3，见 THIRD_PARTY_NOTICES.txt），用三个导出函数解成 BGRA。
    /// 缺少 DLL 或架构不符时静默降级为“没有图标”，不影响列表本身。
    /// </summary>
    internal static class WebpDecoder
    {
        private const string DllName = "libwebp.dll";

        /// <summary>DLL 不可用（缺失、位数不符、导出缺失）时置位，避免每个图标都抛异常。</summary>
        private static volatile bool _unavailable;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPDecodeBGRA(byte[] data, UIntPtr dataSize, out int width, out int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPFree(IntPtr pointer);

        /// <summary>按文件头判断是不是 WebP（RIFF....WEBP），不依赖扩展名。</summary>
        public static bool IsWebp(byte[] data)
        {
            return data != null
                && data.Length >= 16
                && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';
        }

        /// <summary>把 WebP 解成 32 位 BGRA 位图；失败返回 null。</summary>
        public static BitmapSource Decode(byte[] data)
        {
            if (_unavailable || !IsWebp(data))
            {
                return null;
            }

            try
            {
                int width;
                int height;
                IntPtr pixels = WebPDecodeBGRA(data, (UIntPtr)(uint)data.Length, out width, out height);
                if (pixels == IntPtr.Zero || width <= 0 || height <= 0)
                {
                    return null;
                }

                try
                {
                    long stride = (long)width * 4;
                    long total = stride * height;
                    if (total > int.MaxValue)
                    {
                        return null;
                    }

                    BitmapSource image = BitmapSource.Create(width, height, 96, 96,
                        PixelFormats.Bgra32, null, pixels, (int)total, (int)stride);
                    image.Freeze();
                    return image;
                }
                finally
                {
                    WebPFree(pixels);
                }
            }
            catch (DllNotFoundException)
            {
                _unavailable = true;
                return null;
            }
            catch (BadImageFormatException)
            {
                _unavailable = true;
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                _unavailable = true;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
