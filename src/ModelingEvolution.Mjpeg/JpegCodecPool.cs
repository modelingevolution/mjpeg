using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Thread-safe pool of native JPEG encoders and decoders.
/// Each MjpegHdrEngine should own its own pool for optimal performance.
/// </summary>
public sealed class JpegCodecPool : ICodecPool
{
    private readonly ConcurrentBag<nint> _encoderPool = new();
    private readonly ConcurrentBag<nint> _decoderPool = new();
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _quality;
    private readonly DctMethod _dctMethod;
    private bool _disposed;

    // P/Invoke declarations
    private const string LibraryName = "LibJpegWrap";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint Create(int width, int height, int quality, ulong bufSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Close(nint encoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetMode(nint encoder, int mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetQuality(nint encoder, int quality);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CreateDecoder(int maxWidth, int maxHeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void CloseDecoder(nint decoder);

    // Encoder operations
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Encode")]
    private static extern ulong OnEncode(nint encoder, nint data, nint dstBuffer, ulong dstBufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong EncodeGray8ToJpeg(nint grayData, int width, int height, int quality, nint output, ulong outputSize);

    // Decoder operations
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DecoderDecodeI420(nint decoder, nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DecoderDecodeGray(nint decoder, nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetJpegImageInfo(nint jpegData, ulong jpegSize, out DecodeInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct DecodeInfo
    {
        public int Width;
        public int Height;
        public int Components;
        public int ColorSpace;
    }

    /// <summary>
    /// Creates a new codec pool with specified dimensions and quality settings.
    /// </summary>
    public JpegCodecPool(int maxWidth, int maxHeight, int quality = 85, DctMethod dctMethod = DctMethod.Integer)
    {
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _quality = quality;
        _dctMethod = dctMethod;
    }

    /// <summary>
    /// Rents an encoder from the pool. Creates a new one if pool is empty.
    /// Caller must return the encoder using ReturnEncoder.
    /// </summary>
    public nint RentEncoder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_encoderPool.TryTake(out var encoder))
        {
            return encoder;
        }

        // Create new encoder
        ulong bufferSize = (ulong)(_maxWidth * _maxHeight * 3 / 2);
        encoder = Create(_maxWidth, _maxHeight, _quality, bufferSize);

        if (encoder == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG encoder.");
        }

        SetMode(encoder, (int)_dctMethod);
        return encoder;
    }

    /// <summary>
    /// Returns an encoder to the pool.
    /// </summary>
    public void ReturnEncoder(nint encoder)
    {
        if (_disposed)
        {
            // Pool is disposed, close the encoder
            Close(encoder);
            return;
        }

        _encoderPool.Add(encoder);
    }

    /// <summary>
    /// Rents a decoder from the pool. Creates a new one if pool is empty.
    /// Caller must return the decoder using ReturnDecoder.
    /// </summary>
    public nint RentDecoder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_decoderPool.TryTake(out var decoder))
        {
            return decoder;
        }

        // Create new decoder
        decoder = CreateDecoder(_maxWidth, _maxHeight);

        if (decoder == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG decoder.");
        }

        return decoder;
    }

    /// <summary>
    /// Returns a decoder to the pool.
    /// </summary>
    public void ReturnDecoder(nint decoder)
    {
        if (_disposed)
        {
            // Pool is disposed, close the decoder
            CloseDecoder(decoder);
            return;
        }

        _decoderPool.Add(decoder);
    }

    /// <summary>
    /// Gets image info from JPEG data without full decode.
    /// </summary>
    public unsafe FrameHeader GetImageInfo(ReadOnlyMemory<byte> jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        var result = GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
        if (result == 0)
            throw new InvalidOperationException("Failed to read JPEG header.");

        var outputSize = info.Width * info.Height;
        return new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.Gray8, outputSize);
    }

    /// <summary>
    /// Decodes JPEG to I420 format using a pooled decoder.
    /// </summary>
    public unsafe FrameHeader DecodeI420(nint decoder, ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = DecoderDecodeI420(
            decoder,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length,
            out var info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image to I420.");

        int i420Length = info.Width * info.Height * 3 / 2;
        return new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.I420, i420Length);
    }

    /// <summary>
    /// Decodes JPEG to grayscale format using a pooled decoder.
    /// </summary>
    public unsafe FrameHeader DecodeGray(nint decoder, ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = DecoderDecodeGray(
            decoder,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length,
            out var info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image.");

        return new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.Gray8, (int)bytesWritten);
    }

    /// <summary>
    /// Encodes I420 frame to JPEG using a pooled encoder.
    /// </summary>
    public unsafe int EncodeI420(nint encoder, ReadOnlyMemory<byte> frameData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = frameData.Pin();
        using var outputHandle = outputBuffer.Pin();

        ulong bytesWritten = OnEncode(
            encoder,
            (nint)inputHandle.Pointer,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length);

        return (int)bytesWritten;
    }

    /// <summary>
    /// Encodes Gray8 frame to JPEG.
    /// </summary>
    public unsafe int EncodeGray8(int width, int height, ReadOnlyMemory<byte> frameData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = frameData.Pin();
        using var outputHandle = outputBuffer.Pin();

        ulong bytesWritten = EncodeGray8ToJpeg(
            (nint)inputHandle.Pointer,
            width,
            height,
            _quality,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to encode Gray8 image to JPEG.");

        return (int)bytesWritten;
    }

    /// <summary>
    /// Maximum image width supported by this pool.
    /// </summary>
    public int MaxWidth => _maxWidth;

    /// <summary>
    /// Maximum image height supported by this pool.
    /// </summary>
    public int MaxHeight => _maxHeight;

    /// <summary>
    /// JPEG quality setting.
    /// </summary>
    public int Quality => _quality;

    /// <summary>
    /// Disposes all pooled encoders and decoders.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain and close all encoders
        while (_encoderPool.TryTake(out var encoder))
        {
            Close(encoder);
        }

        // Drain and close all decoders
        while (_decoderPool.TryTake(out var decoder))
        {
            CloseDecoder(decoder);
        }
    }
}
