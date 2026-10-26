using System.Buffers;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Options for configuring JpegCodec.
/// </summary>
public class JpegCodecOptions
{
    /// <summary>Maximum image width to support.</summary>
    public int MaxWidth { get; set; } = 1920;

    /// <summary>Maximum image height to support.</summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>Default JPEG quality (1-100).</summary>
    public int Quality { get; set; } = 85;

    /// <summary>DCT algorithm to use.</summary>
    public DctMethod DctMethod { get; set; } = DctMethod.Integer;
}

/// <summary>
/// JPEG encoder/decoder using LibJpegWrap native library.
/// Standalone codec for direct encode/decode operations.
/// For HDR processing with parallel decode, use JpegCodecPool instead.
/// </summary>
public sealed class JpegCodec : IJpegCodec
{
    private readonly nint _encoderPtr;
    private readonly nint _decoderPtr;
    private bool _disposed;
    private int _quality;
    private DctMethod _dctMethod;

    /// <summary>
    /// Creates a new JpegCodec with default options.
    /// </summary>
    public JpegCodec() : this(new JpegCodecOptions())
    {
    }

    /// <summary>
    /// Creates a new JpegCodec with specified options.
    /// </summary>
    public JpegCodec(JpegCodecOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _quality = options.Quality;
        _dctMethod = options.DctMethod;

        ulong bufferSize = (ulong)(options.MaxWidth * options.MaxHeight * 3 / 2);
        _encoderPtr = JpegTurboNative.Create(options.MaxWidth, options.MaxHeight, options.Quality, bufferSize);

        if (_encoderPtr == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG encoder. Native library may not be loaded.");
        }

        JpegTurboNative.SetMode(_encoderPtr, (int)options.DctMethod);

        // Create pooled decoder
        _decoderPtr = JpegTurboNative.CreateDecoder(options.MaxWidth, options.MaxHeight);

        if (_decoderPtr == nint.Zero)
        {
            JpegTurboNative.Close(_encoderPtr);
            throw new InvalidOperationException("Failed to create JPEG decoder. Native library may not be loaded.");
        }
    }

    /// <inheritdoc/>
    public int Quality
    {
        get => _quality;
        set
        {
            if (value < 1 || value > 100)
                throw new ArgumentOutOfRangeException(nameof(value), "Quality must be between 1 and 100.");

            if (_quality != value)
            {
                _quality = value;
                JpegTurboNative.SetQuality(_encoderPtr, value);
            }
        }
    }

    /// <inheritdoc/>
    public DctMethod DctMethod
    {
        get => _dctMethod;
        set
        {
            if (_dctMethod != value)
            {
                _dctMethod = value;
                JpegTurboNative.SetMode(_encoderPtr, (int)value);
            }
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public unsafe FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = JpegTurboNative.DecoderDecodeGray(
            _decoderPtr,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length,
            out var info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image.");

        return new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.Gray8, (int)bytesWritten);
    }

    /// <inheritdoc/>
    public unsafe FrameImage Decode(ReadOnlyMemory<byte> jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // First get dimensions
        using var inputHandle = jpegData.Pin();

        var result = JpegTurboNative.GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
        if (result == 0)
            throw new InvalidOperationException("Failed to read JPEG header.");

        // Rent output buffer for grayscale from pool
        var outputSize = info.Width * info.Height;
        var outputOwner = MemoryPool<byte>.Shared.Rent(outputSize);

        using var outputHandle = outputOwner.Memory.Pin();

        var bytesWritten = JpegTurboNative.DecoderDecodeGray(
            _decoderPtr,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputSize,
            out info);

        if (bytesWritten == 0)
        {
            outputOwner.Dispose();
            throw new InvalidOperationException("Failed to decode JPEG image.");
        }

        var header = new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.Gray8, (int)bytesWritten);
        return new FrameImage(header, outputOwner);
    }

    /// <inheritdoc/>
    public unsafe FrameHeader DecodeI420(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = JpegTurboNative.DecoderDecodeI420(
            _decoderPtr,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputBuffer.Length,
            out var info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image to I420.");

        // I420 length = width * height * 1.5
        int i420Length = info.Width * info.Height * 3 / 2;
        return new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.I420, i420Length);
    }

    /// <inheritdoc/>
    public unsafe FrameImage DecodeI420(ReadOnlyMemory<byte> jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // First get dimensions
        using var inputHandle = jpegData.Pin();

        var result = JpegTurboNative.GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
        if (result == 0)
            throw new InvalidOperationException("Failed to read JPEG header.");

        // Rent output buffer for I420 (width * height * 1.5) from pool
        int outputSize = info.Width * info.Height * 3 / 2;
        var outputOwner = MemoryPool<byte>.Shared.Rent(outputSize);

        using var outputHandle = outputOwner.Memory.Pin();

        var bytesWritten = JpegTurboNative.DecoderDecodeI420(
            _decoderPtr,
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputSize,
            out info);

        if (bytesWritten == 0)
        {
            outputOwner.Dispose();
            throw new InvalidOperationException("Failed to decode JPEG image to I420.");
        }

        var header = new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.I420, (int)bytesWritten);
        return new FrameImage(header, outputOwner);
    }

    /// <inheritdoc/>
    public unsafe int Encode(in FrameImage frame, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = frame.Data.Pin();
        using var outputHandle = outputBuffer.Pin();

        return frame.Header.Format switch
        {
            PixelFormat.I420 => EncodeI420(inputHandle, outputHandle, outputBuffer.Length),
            PixelFormat.Gray8 => EncodeGray8(frame.Header, inputHandle, outputHandle, outputBuffer.Length),
            _ => throw new NotSupportedException(
                $"Only I420 and Gray8 formats are supported. Got: {frame.Header.Format}")
        };
    }

    private unsafe int EncodeI420(MemoryHandle inputHandle, MemoryHandle outputHandle, int outputLength)
    {
        ulong bytesWritten = JpegTurboNative.Encode(
            _encoderPtr,
            (nint)inputHandle.Pointer,
            (nint)outputHandle.Pointer,
            (ulong)outputLength);

        return (int)bytesWritten;
    }

    private unsafe int EncodeGray8(FrameHeader header, MemoryHandle inputHandle, MemoryHandle outputHandle, int outputLength)
    {
        ulong bytesWritten = JpegTurboNative.EncodeGray8ToJpeg(
            (nint)inputHandle.Pointer,
            header.Width,
            header.Height,
            _quality,
            (nint)outputHandle.Pointer,
            (ulong)outputLength);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to encode Gray8 image to JPEG.");

        return (int)bytesWritten;
    }

    /// <inheritdoc/>
    public FrameImage Encode(in FrameImage frame)
    {
        // Rent from pool - may be larger than needed
        int maxSize = frame.Header.Length;
        var owner = MemoryPool<byte>.Shared.Rent(maxSize);

        int length = Encode(frame, owner.Memory);

        // Create header with actual encoded length
        var header = new FrameHeader(
            frame.Header.Width,
            frame.Header.Height,
            length,
            frame.Header.Format,
            length);

        // FrameImage slices to header.Length automatically
        return new FrameImage(header, owner);
    }

    /// <inheritdoc/>
    /// <remarks>Stateless native call: safe to use concurrently on one codec instance.</remarks>
    public unsafe FrameHeader DecodeRgb24(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        nint input = (nint)inputHandle.Pointer;
        nint output = (nint)outputHandle.Pointer;
        ulong inputLength = (ulong)jpegData.Length;
        ulong outputLength = (ulong)outputBuffer.Length;

        var (bytesWritten, info) = RequireRgb24Support(() =>
        {
            var n = JpegTurboNative.DecodeJpegToRgb24(input, inputLength, output, outputLength, out var decoded);
            return (n, decoded);
        });

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image to Rgb24 (corrupt data or output buffer too small).");

        return new FrameHeader(info.Width, info.Height, info.Width * 3, PixelFormat.Rgb24, (int)bytesWritten);
    }

    /// <inheritdoc/>
    /// <remarks>Stateless native call: safe to use concurrently on one codec instance.</remarks>
    public unsafe FrameImage DecodeRgb24(ReadOnlyMemory<byte> jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        FrameHeader info;
        using (var inputHandle = jpegData.Pin())
        {
            if (JpegTurboNative.GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var header) == 0)
                throw new InvalidOperationException("Failed to read JPEG header.");
            info = new FrameHeader(header.Width, header.Height, header.Width * 3, PixelFormat.Rgb24, header.Width * header.Height * 3);
        }

        var owner = MemoryPool<byte>.Shared.Rent(info.Length);
        try
        {
            var decoded = DecodeRgb24(jpegData, owner.Memory);
            return new FrameImage(decoded, owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Stateless native call: safe to use concurrently on one codec instance. Uses the integer DCT.</remarks>
    public unsafe int EncodeRgb24(in FrameImage frame, int quality, Memory<byte> outputBuffer, JpegSubsampling subsampling = JpegSubsampling.Ratio420)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRgb24Encode(frame.Header, quality, subsampling);

        using var inputHandle = frame.Data.Pin();
        using var outputHandle = outputBuffer.Pin();
        var header = frame.Header;
        nint input = (nint)inputHandle.Pointer;
        nint output = (nint)outputHandle.Pointer;
        ulong outputLength = (ulong)outputBuffer.Length;

        var bytesWritten = RequireRgb24Support(() => JpegTurboNative.EncodeRgb24ToJpeg(
            input, header.Width, header.Height, header.Stride, quality, (int)subsampling, output, outputLength));

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to encode Rgb24 image to JPEG (output buffer too small or encoder error).");

        return (int)bytesWritten;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Rents a worst-case buffer (libjpeg-turbo's <c>tjBufSize</c> bound for the chosen subsampling) from the shared pool.
    /// </remarks>
    public FrameImage EncodeRgb24(in FrameImage frame, int quality, JpegSubsampling subsampling = JpegSubsampling.Ratio420)
    {
        ValidateRgb24Encode(frame.Header, quality, subsampling);

        var owner = MemoryPool<byte>.Shared.Rent(MaxJpegSize(frame.Header.Width, frame.Header.Height, subsampling));
        try
        {
            int length = EncodeRgb24(frame, quality, owner.Memory, subsampling);
            var header = new FrameHeader(frame.Header.Width, frame.Header.Height, length, frame.Header.Format, length);
            return new FrameImage(header, owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static void ValidateRgb24Encode(in FrameHeader header, int quality, JpegSubsampling subsampling)
    {
        if (header.Format != PixelFormat.Rgb24)
            throw new NotSupportedException($"EncodeRgb24 requires PixelFormat.Rgb24. Got: {header.Format}");
        if (header.Width <= 0 || header.Height <= 0 || header.Stride < header.Width * 3)
            throw new ArgumentException($"Invalid Rgb24 frame header: {header}", nameof(header));
        if (quality < 1 || quality > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 1 and 100.");
        if (!Enum.IsDefined(subsampling))
            throw new ArgumentOutOfRangeException(nameof(subsampling));
    }

    // libjpeg-turbo tjBufSize: PAD(w, mcuw) * PAD(h, mcuh) * (2 + chromasf) + 2048, chromasf = 4 * 64 / (mcuw * mcuh).
    private static int MaxJpegSize(int width, int height, JpegSubsampling subsampling)
    {
        int mcu = subsampling == JpegSubsampling.Ratio444 ? 8 : 16;
        long chroma = 4L * 64 / (mcu * mcu);
        long size = (long)Pad(width, mcu) * Pad(height, mcu) * (2 + chroma) + 2048;
        return checked((int)size);

        static int Pad(int v, int p) => (v + p - 1) / p * p;
    }

    private static T RequireRgb24Support<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (EntryPointNotFoundException e)
        {
            throw new NotSupportedException(
                "Rgb24 JPEG decode/encode is not available in the LibJpegWrap native library for this platform " +
                $"({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}); it ships for linux-x64.", e);
        }
    }

    /// <summary>
    /// Disposes the native encoder and decoder resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (_encoderPtr != nint.Zero)
        {
            JpegTurboNative.Close(_encoderPtr);
        }

        if (_decoderPtr != nint.Zero)
        {
            JpegTurboNative.CloseDecoder(_decoderPtr);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~JpegCodec()
    {
        Dispose();
    }
}
