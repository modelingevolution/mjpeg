using System.Collections.Concurrent;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Thread-safe pool of native JPEG encoders and decoders.
/// Each MjpegHdrEngine should own its own pool for optimal performance.
/// </summary>
public sealed class JpegCodecPool : ICodecPool
{
    private readonly ConcurrentQueue<nint> _encoderPool = new();
    private readonly ConcurrentQueue<nint> _decoderPool = new();
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _quality;
    private readonly DctMethod _dctMethod;
    private bool _disposed;

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

        if (_encoderPool.TryDequeue(out var encoder))
        {
            return encoder;
        }

        // Create new encoder
        ulong bufferSize = (ulong)(_maxWidth * _maxHeight * 3 / 2);
        encoder = JpegTurboNative.Create(_maxWidth, _maxHeight, _quality, bufferSize);

        if (encoder == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG encoder. Native library may not be loaded.");
        }

        JpegTurboNative.SetMode(encoder, (int)_dctMethod);
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
            JpegTurboNative.Close(encoder);
            return;
        }

        _encoderPool.Enqueue(encoder);
    }

    /// <summary>
    /// Rents a decoder from the pool. Creates a new one if pool is empty.
    /// Caller must return the decoder using ReturnDecoder.
    /// </summary>
    public nint RentDecoder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_decoderPool.TryDequeue(out var decoder))
        {
            return decoder;
        }

        // Create new decoder
        decoder = JpegTurboNative.CreateDecoder(_maxWidth, _maxHeight);

        if (decoder == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG decoder. Native library may not be loaded.");
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
            JpegTurboNative.CloseDecoder(decoder);
            return;
        }

        _decoderPool.Enqueue(decoder);
    }

    /// <summary>
    /// Gets image info from JPEG data without full decode.
    /// </summary>
    public unsafe FrameHeader GetImageInfo(ReadOnlyMemory<byte> jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        var result = JpegTurboNative.GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
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

        var bytesWritten = JpegTurboNative.DecoderDecodeI420(
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

        var bytesWritten = JpegTurboNative.DecoderDecodeGray(
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

        ulong bytesWritten = JpegTurboNative.Encode(
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

        ulong bytesWritten = JpegTurboNative.EncodeGray8ToJpeg(
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
        while (_encoderPool.TryDequeue(out var encoder))
        {
            JpegTurboNative.Close(encoder);
        }

        // Drain and close all decoders
        while (_decoderPool.TryDequeue(out var decoder))
        {
            JpegTurboNative.CloseDecoder(decoder);
        }
    }
}
