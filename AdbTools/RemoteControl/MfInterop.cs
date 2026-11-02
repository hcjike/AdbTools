using System;
using System.Runtime.InteropServices;

namespace AdbTools.RemoteControl
{
    /// <summary>Media Foundation 需要的常量、GUID 与入口函数。</summary>
    internal static class MfInterop
    {
        public const int MF_VERSION = 0x00020070;
        public const int MFSTARTUP_FULL = 0;

        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
        public const int MF_E_BUFFERTOOSMALL = unchecked((int)0xC00D36B3);
        public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B9);
        public const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B2);

        public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
        public const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
        public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
        public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;

        public const int MFT_OUTPUT_STREAM_WHOLE_SAMPLES = 0x00000001;
        public const int MFT_OUTPUT_STREAM_SINGLE_SAMPLE_PER_BUFFER = 0x00000002;
        public const int MFT_OUTPUT_STREAM_FIXED_SAMPLE_SIZE = 0x00000004;
        public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

        public const int MF_MT_INTERLACE_MODE_PROGRESSIVE = 2;

        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");

        public static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static Guid MF_MT_MPEG_SEQUENCE_HEADER = new Guid("1b81beD0-a0c7-11d3-b6b3-00e04c530b2f");
        public static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        public static Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        // 有效画面区域（MFVideoArea：OffsetX/OffsetY 为 float，Area 为 SIZE）
        public static Guid MF_MT_GEOMETRIC_APERTURE = new Guid("66758743-7e5f-400d-980a-aa8596c85696");
        public static Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new Guid("d7388766-18fe-48c6-a177-ee894867c8c4");

        public static readonly Guid CLSID_CMSH264DecoderMFT = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        public static readonly Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");

        public const int CLSCTX_INPROC_SERVER = 1;

        private static int _startupRefCount;
        private static readonly object _startupLock = new object();

        /// <summary>进程级 MFStartup/MFShutdown 计数：多个远程控制窗口可安全共存。</summary>
        public static void Startup()
        {
            lock (_startupLock)
            {
                if (_startupRefCount++ == 0)
                {
                    Check(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
                }
            }
        }

        /// <summary>与 Startup 配对调用；计数归零时才真正关闭 Media Foundation。</summary>
        public static void Shutdown()
        {
            lock (_startupLock)
            {
                if (_startupRefCount == 0) return;
                if (--_startupRefCount == 0)
                {
                    try { MFShutdown(); } catch { }
                }
            }
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        /// <summary>把 HRESULT 转成可读异常（成功返回 true）。</summary>
        public static bool Succeeded(int hr)
        {
            return hr >= 0;
        }

        public static void Check(int hr, string action)
        {
            if (hr < 0)
            {
                throw new COMException(action + " 失败（hr=0x" + hr.ToString("X8") + "）", hr);
            }
        }

        /// <summary>把宽高打包成 MF_MT_FRAME_SIZE 需要的 64 位值。</summary>
        public static long PackSize(int width, int height)
        {
            return ((long)width << 32) | (uint)height;
        }

        public static void UnpackSize(long packed, out int width, out int height)
        {
            width = (int)(packed >> 32);
            height = (int)(packed & 0xFFFFFFFF);
        }

        /// <summary>低延迟模式：解码器不再为显示排序缓存输出帧，收到一帧即可输出。</summary>
        public static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");

        /// <summary>创建 H.264 解码器 MFT（微软系统自带），并开启低延迟模式。</summary>
        public static IMFTransform CreateH264Decoder()
        {
            Guid clsid = CLSID_CMSH264DecoderMFT;
            Guid iid = IID_IMFTransform;
            object obj;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out obj);
            Check(hr, "创建 H.264 解码器");

            var transform = (IMFTransform)obj;
            try
            {
                IMFAttributes attributes;
                if (transform.GetAttributes(out attributes) == 0 && attributes != null)
                {
                    Guid key = MF_LOW_LATENCY;
                    attributes.SetUINT32(ref key, 1);
                    Marshal.ReleaseComObject(attributes);
                }
            }
            catch { }

            return transform;
        }
    }
}
