namespace ModelingEvolution.Mjpeg.Wasm;

/// <summary>
/// Abstraction over native JPEG decoder operations.
/// Enables unit testing without WASM runtime.
/// </summary>
internal interface INativeDecoder
{
    nint CreateDecoder(int maxWidth, int maxHeight);
    void CloseDecoder(nint decoder);
    unsafe uint Decode(nint decoder, nint jpegData, uint jpegSize,
        nint output, uint outputSize, WasmJpegNative.DecodeInfo* info);
}

internal sealed class WasmNativeDecoder : INativeDecoder
{
    public static readonly WasmNativeDecoder Instance = new();

    public nint CreateDecoder(int maxWidth, int maxHeight)
        => WasmJpegNative.CreateBgraDecoder(maxWidth, maxHeight);

    public void CloseDecoder(nint decoder)
        => WasmJpegNative.CloseBgraDecoder(decoder);

    public unsafe uint Decode(nint decoder, nint jpegData, uint jpegSize,
        nint output, uint outputSize, WasmJpegNative.DecodeInfo* info)
        => WasmJpegNative.DecoderDecodeBGRA(decoder, jpegData, jpegSize, output, outputSize, info);
}
