using System.Runtime.InteropServices;

namespace ModelingEvolution.Mjpeg.Wasm;

/// <summary>
/// P/Invoke declarations for LibJpegWrap BGRA decoder (Emscripten/WASM build).
/// </summary>
internal static class WasmJpegNative
{
    private const string LibraryName = "LibJpegWrap";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint CreateBgraDecoder(int maxWidth, int maxHeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseBgraDecoder(nint decoder);

    // NOTE: C++ uses 'unsigned long' which is 4 bytes on WASM (32-bit).
    // C# ulong is always 8 bytes. Use uint to match the WASM ABI.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe uint DecoderDecodeBGRA(
        nint decoder,
        nint jpegData, uint jpegSize,
        nint output, uint outputSize,
        DecodeInfo* info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DecodeInfo
    {
        public int Width;
        public int Height;
        public int Components;
        public int ColorSpace;
    }
}
