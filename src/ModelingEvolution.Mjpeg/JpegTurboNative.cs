using System.Runtime.InteropServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// P/Invoke declarations for LibJpegWrap native library.
/// Centralized to avoid duplication across JpegCodec and JpegCodecPool.
/// </summary>
internal static class JpegTurboNative
{
    private const string LibraryName = "LibJpegWrap";

    // Encoder lifecycle
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint Create(int width, int height, int quality, ulong bufSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(nint encoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetMode(nint encoder, int mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetQuality(nint encoder, int quality);

    // Encoder operations
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Encode")]
    internal static extern ulong Encode(nint encoder, nint data, nint dstBuffer, ulong dstBufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong EncodeGray8ToJpeg(nint grayData, int width, int height, int quality, nint output, ulong outputSize);

    // Decoder lifecycle
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint CreateDecoder(int maxWidth, int maxHeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseDecoder(nint decoder);

    // Decoder operations (pooled)
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DecoderDecodeI420(nint decoder, nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DecoderDecodeGray(nint decoder, nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    // Legacy non-pooled functions
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DecodeJpegToGray(nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DecodeJpegToI420(nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetJpegImageInfo(nint jpegData, ulong jpegSize, out DecodeInfo info);

    /// <summary>
    /// Decode result info from native library.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DecodeInfo
    {
        public int Width;
        public int Height;
        public int Components;
        public int ColorSpace;
    }
}
