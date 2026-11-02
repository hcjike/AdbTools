using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AdbTools.RemoteControl
{
    /// <summary>Media Foundation 相关 COM 接口的最小声明（方法顺序必须与原生 vtable 一致）。</summary>
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out long punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, int cchBufSize, IntPtr pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, long unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, [In] byte[] pBuf, int cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int pcItems);
        [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] new int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] new int GetUINT64(ref Guid guidKey, out long punValue);
        [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] new int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, int cchBufSize, IntPtr pcchLength);
        [PreserveSig] new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        [PreserveSig] new int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] new int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid guidKey);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] new int SetUINT64(ref Guid guidKey, long unValue);
        [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, int cbBufSize);
        [PreserveSig] new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int pcItems);
        [PreserveSig] new int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat(out int pfCompressed);
        [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out int pdwFlags);
        [PreserveSig] int GetRepresentation(ref Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(ref Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        [PreserveSig] int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] new int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] new int GetUINT64(ref Guid guidKey, out long punValue);
        [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] new int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, int cchBufSize, IntPtr pcchLength);
        [PreserveSig] new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        [PreserveSig] new int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] new int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid guidKey);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] new int SetUINT64(ref Guid guidKey, long unValue);
        [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, int cbBufSize);
        [PreserveSig] new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int pcItems);
        [PreserveSig] new int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

        [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(int dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out int pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(int dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO
    {
        public int dwFlags;
        public int cbSize;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        public int dwStreamID;
        [MarshalAs(UnmanagedType.Interface)] public IMFSample pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int pdwInputMinimum, out int pdwInputMaximum, out int pdwOutputMinimum, out int pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
        [PreserveSig] int GetStreamIDs(int dwInputIDArraySize, IntPtr pdwInputIDs, int dwOutputIDArraySize, IntPtr pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(int dwInputStreamID, IntPtr pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(int dwInputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(int dwOutputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(int dwStreamID);
        [PreserveSig] int AddInputStreams(int cStreams, IntPtr adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(int dwInputStreamID, IMFMediaType pType, int dwFlags);
        [PreserveSig] int SetOutputType(int dwOutputStreamID, IMFMediaType pType, int dwFlags);
        [PreserveSig] int GetInputCurrentType(int dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(int dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(int dwInputStreamID, out int pdwFlags);
        [PreserveSig] int GetOutputStatus(out int pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(int dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(int dwInputStreamID, IMFSample pSample, int dwFlags);
        [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out int pdwStatus);
    }
}
