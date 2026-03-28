using System.Collections.Concurrent;

namespace ModelingEvolution.Mjpeg.Wasm.Tests;

/// <summary>
/// Fake native decoder for unit testing.
/// Tracks Create/Close calls and simulates decode by writing a pattern to output.
/// </summary>
internal sealed class FakeNativeDecoder : INativeDecoder
{
    private int _nextHandle = 1;
    public ConcurrentBag<nint> CreatedDecoders { get; } = new();
    public ConcurrentBag<nint> ClosedDecoders { get; } = new();
    public ConcurrentBag<nint> DecodeCalledWith { get; } = new();
    public bool ShouldFailDecode { get; set; }
    public int DecodeDelayMs { get; set; }

    public nint CreateDecoder(int maxWidth, int maxHeight)
    {
        var handle = (nint)Interlocked.Increment(ref _nextHandle);
        CreatedDecoders.Add(handle);
        return handle;
    }

    public void CloseDecoder(nint decoder)
    {
        ClosedDecoders.Add(decoder);
    }

    public unsafe uint Decode(nint decoder, nint jpegData, uint jpegSize,
        nint output, uint outputSize, WasmJpegNative.DecodeInfo* info)
    {
        DecodeCalledWith.Add(decoder);

        if (DecodeDelayMs > 0)
            Thread.Sleep(DecodeDelayMs);

        if (ShouldFailDecode)
            return 0;

        info->Width = 16;
        info->Height = 16;
        info->Components = 4;
        info->ColorSpace = 0;

        // Write a recognizable pattern to output (BGRA: blue channel = 0xFF)
        uint written = Math.Min(outputSize, 16u * 16u * 4u);
        var span = new Span<byte>((void*)output, (int)written);
        for (int i = 0; i < span.Length; i += 4)
        {
            span[i] = 0xFF;     // B
            span[i + 1] = 0x00; // G
            span[i + 2] = 0x00; // R
            span[i + 3] = 0xFF; // A
        }

        return written;
    }
}
