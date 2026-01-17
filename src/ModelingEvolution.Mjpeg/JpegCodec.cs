using System.Buffers;
using System.Runtime.InteropServices;

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
/// </summary>
public sealed class JpegCodec : IJpegCodec
{
    private readonly nint _encoderPtr;
    private readonly object _lock = new();
    private bool _disposed;
    private int _quality;
    private DctMethod _dctMethod;

    // P/Invoke declarations for LibJpegWrap
    private const string LibraryName = "LibJpegWrap";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint Create(int width, int height, int quality, ulong bufSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Encode")]
    private static extern ulong OnEncode(nint encoder, nint data, nint dstBuffer, ulong dstBufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Close(nint encoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetMode(nint encoder, int mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetQuality(nint encoder, int quality);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DecodeJpegToGray(nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetJpegImageInfo(nint jpegData, ulong jpegSize, out DecodeInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong EncodeGray8ToJpeg(nint grayData, int width, int height, int quality, nint output, ulong outputSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DecodeJpegToI420(nint jpegData, ulong jpegSize, nint output, ulong outputSize, out DecodeInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct DecodeInfo
    {
        public int Width;
        public int Height;
        public int Components;
        public int ColorSpace;
    }

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
        _encoderPtr = Create(options.MaxWidth, options.MaxHeight, options.Quality, bufferSize);

        if (_encoderPtr == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create JPEG encoder. Native library may not be loaded.");
        }

        SetMode(_encoderPtr, (int)options.DctMethod);
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
                lock (_lock)
                {
                    SetQuality(_encoderPtr, value);
                }
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
                lock (_lock)
                {
                    SetMode(_encoderPtr, (int)value);
                }
            }
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public unsafe FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = DecodeJpegToGray(
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

        var result = GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
        if (result == 0)
            throw new InvalidOperationException("Failed to read JPEG header.");

        // Allocate output buffer for grayscale
        var outputSize = info.Width * info.Height;
        var output = new byte[outputSize];

        using var outputHandle = output.AsMemory().Pin();

        var bytesWritten = DecodeJpegToGray(
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputSize,
            out info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image.");

        var header = new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.Gray8, (int)bytesWritten);
        return new FrameImage(header, output);
    }

    /// <inheritdoc/>
    public unsafe FrameHeader DecodeI420(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var inputHandle = jpegData.Pin();
        using var outputHandle = outputBuffer.Pin();

        var bytesWritten = DecodeJpegToI420(
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

        var result = GetJpegImageInfo((nint)inputHandle.Pointer, (ulong)jpegData.Length, out var info);
        if (result == 0)
            throw new InvalidOperationException("Failed to read JPEG header.");

        // Allocate output buffer for I420 (width * height * 1.5)
        int outputSize = info.Width * info.Height * 3 / 2;
        var output = new byte[outputSize];

        using var outputHandle = output.AsMemory().Pin();

        var bytesWritten = DecodeJpegToI420(
            (nint)inputHandle.Pointer,
            (ulong)jpegData.Length,
            (nint)outputHandle.Pointer,
            (ulong)outputSize,
            out info);

        if (bytesWritten == 0)
            throw new InvalidOperationException("Failed to decode JPEG image to I420.");

        var header = new FrameHeader(info.Width, info.Height, info.Width, PixelFormat.I420, (int)bytesWritten);
        return new FrameImage(header, output);
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
        lock (_lock)
        {
            ulong bytesWritten = OnEncode(
                _encoderPtr,
                (nint)inputHandle.Pointer,
                (nint)outputHandle.Pointer,
                (ulong)outputLength);

            return (int)bytesWritten;
        }
    }

    private unsafe int EncodeGray8(FrameHeader header, MemoryHandle inputHandle, MemoryHandle outputHandle, int outputLength)
    {
        ulong bytesWritten = EncodeGray8ToJpeg(
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
        // Allocate worst-case buffer size
        int maxSize = frame.Header.Length;
        var buffer = new byte[maxSize];

        int length = Encode(frame, buffer);

        // Create header for JPEG data (not really a frame, but stores the data)
        var header = new FrameHeader(
            frame.Header.Width,
            frame.Header.Height,
            length,  // For JPEG, "stride" is meaningless, use length
            frame.Header.Format,
            length);

        // Copy to exact-size array
        var exactBuffer = new byte[length];
        buffer.AsSpan(0, length).CopyTo(exactBuffer);

        return new FrameImage(header, exactBuffer);
    }

    /// <summary>
    /// Disposes the native encoder resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (_encoderPtr != nint.Zero)
        {
            Close(_encoderPtr);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~JpegCodec()
    {
        Dispose();
    }
}
