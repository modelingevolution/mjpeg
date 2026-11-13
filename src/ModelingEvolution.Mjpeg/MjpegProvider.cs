using System.Buffers;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Provides decoded MJPEG frames from an <see cref="HttpFrameGrabber"/>.
/// Acquires the latest raw JPEG, decodes to grayscale via <see cref="IJpegCodec"/>,
/// and returns a <see cref="FrameImage"/> backed by <see cref="MemoryPool{T}"/>.
/// Caller disposes the <see cref="FrameImage"/> to return the buffer to the pool.
/// </summary>
public sealed class MjpegProvider : IAsyncDisposable, IDisposable
{
    private readonly HttpFrameGrabber _grabber;
    private readonly IJpegCodec _codec;
    private readonly bool _ownsGrabber;
    private readonly bool _ownsCodec;
    private int _frameSize;
    private FrameHeader _cachedHeader;

    /// <summary>
    /// Creates a provider from an existing grabber and codec.
    /// </summary>
    public MjpegProvider(HttpFrameGrabber grabber, IJpegCodec codec)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>
    /// Creates a self-contained provider that owns both the grabber and codec.
    /// </summary>
    public MjpegProvider(Uri streamUri)
    {
        _grabber = new HttpFrameGrabber(streamUri);
        _codec = new JpegCodec();
        _ownsGrabber = true;
        _ownsCodec = true;
    }

    /// <summary>
    /// Creates a self-contained provider that owns the grabber.
    /// </summary>
    public MjpegProvider(Uri streamUri, IJpegCodec codec)
    {
        _grabber = new HttpFrameGrabber(streamUri);
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _ownsGrabber = true;
    }

    /// <summary>
    /// Returns true if at least one frame has been grabbed.
    /// </summary>
    public bool HasFrame => _grabber.HasFrame;

    /// <summary>
    /// Total number of raw frames grabbed. Read on its own it says nothing about which frame <see cref="TryGetFrame()"/> or
    /// <see cref="TryAcquireRaw()"/> returns; use their <c>out int frameCount</c> overloads for a frame with its own count.
    /// </summary>
    public int FrameCount => _grabber.FrameCount;

    /// <summary>
    /// Cached frame dimensions from the first decoded frame.
    /// Only valid after the first successful <see cref="TryGetFrame"/> call.
    /// </summary>
    public FrameHeader FrameHeader => _cachedHeader;

    /// <summary>
    /// Starts the background frame grabbing loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default) => _grabber.StartAsync(ct);

    /// <summary>
    /// Acquires the latest frame, decodes JPEG to grayscale, returns a pooled <see cref="FrameImage"/>.
    /// Returns null if no frame is available.
    /// Caller must dispose the returned <see cref="FrameImage"/> to return the buffer to the pool.
    /// </summary>
    public FrameImage? TryGetFrame() => TryGetFrame(out _);

    /// <summary>
    /// As <see cref="TryGetFrame()"/>, and <paramref name="frameCount"/> is the <see cref="FrameCount"/> that frame was published
    /// under, read with the frame in one locked read (<see cref="HttpFrameGrabber.TryAcquireLatest(out int)"/>). 0 when null.
    /// </summary>
    public FrameImage? TryGetFrame(out int frameCount)
    {
        var handle = _grabber.TryAcquireLatest(out frameCount);
        if (handle == null) return null;

        try
        {
            // First frame: learn dimensions
            if (_frameSize == 0)
            {
                _cachedHeader = _codec.GetImageInfo(handle.Value.Data);
                _frameSize = _cachedHeader.Width * _cachedHeader.Height;
            }

            // Rent buffer from pool and decode directly into it
            var owner = MemoryPool<byte>.Shared.Rent(_frameSize);
            var header = _codec.Decode(handle.Value.Data, owner.Memory);
            return new FrameImage(header, owner);
        }
        finally
        {
            handle.Value.Dispose();
        }
    }

    /// <summary>
    /// Acquires the latest raw JPEG frame without decoding.
    /// Returns null if no frame is available.
    /// Caller must dispose the returned handle.
    /// </summary>
    public JpegFrameHandle? TryAcquireRaw() => _grabber.TryAcquireLatest();

    /// <summary>
    /// As <see cref="TryAcquireRaw()"/>, and <paramref name="frameCount"/> is the count that frame was published under, from one
    /// locked read (<see cref="HttpFrameGrabber.TryAcquireLatest(out int)"/>). 0 when null.
    /// </summary>
    public JpegFrameHandle? TryAcquireRaw(out int frameCount) => _grabber.TryAcquireLatest(out frameCount);

    public async ValueTask DisposeAsync()
    {
        if (_ownsGrabber) await _grabber.DisposeAsync();
        if (_ownsCodec) _codec.Dispose();
    }

    public void Dispose()
    {
        if (_ownsGrabber) _grabber.Dispose();
        if (_ownsCodec) _codec.Dispose();
    }
}
