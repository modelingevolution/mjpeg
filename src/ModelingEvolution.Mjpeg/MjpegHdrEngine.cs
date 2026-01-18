using System.Buffers;
using System.Runtime.CompilerServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// HDR processing engine for MJPEG streams.
/// Fetches frames, decodes JPEG, blends using HDR algorithms, and re-encodes to JPEG.
/// Matches GStreamer gsthdr plugin processing pipeline.
/// Each engine owns its own codec pool for optimal performance with parallel decode.
/// </summary>
public sealed class MjpegHdrEngine : IDisposable
{
    private readonly Func<ulong, Task<IMemoryOwner<byte>>> _getImageByFrameId;
    private readonly ICodecPool _codecPool;
    private readonly IHdrBlend _blend;
    private readonly MemoryPool<byte> _pool;
    private bool _disposed;
    private readonly bool _ownsCodecPool;

    /// <summary>
    /// Number of frames to blend (2-10). Matches GStreamer num-frames property.
    /// </summary>
    public int HdrFrameWindowCount { get; set; } = 2;

    /// <summary>
    /// HDR blending mode. Matches GStreamer blend-mode property.
    /// </summary>
    public HdrBlendMode HdrMode { get; set; } = HdrBlendMode.Average;

    /// <summary>
    /// Weight configuration for Weighted mode. Required when HdrMode is Weighted.
    /// </summary>
    public HdrWeights? Weights { get; set; }

    /// <summary>
    /// JPEG output quality (1-100).
    /// </summary>
    public int JpegQuality => _codecPool.Quality;

    /// <summary>
    /// Creates a new MjpegHdrEngine with default codec pool.
    /// </summary>
    /// <param name="getImageByFrameId">
    /// Async function to fetch JPEG data by frame ID.
    /// Returns IMemoryOwner containing JPEG bytes. Caller takes ownership.
    /// </param>
    /// <param name="maxWidth">Maximum image width to support.</param>
    /// <param name="maxHeight">Maximum image height to support.</param>
    /// <param name="quality">JPEG quality (1-100).</param>
    public MjpegHdrEngine(
        Func<ulong, Task<IMemoryOwner<byte>>> getImageByFrameId,
        int maxWidth = 1920,
        int maxHeight = 1080,
        int quality = 85)
        : this(getImageByFrameId, new JpegCodecPool(maxWidth, maxHeight, quality), new HdrBlend(), MemoryPool<byte>.Shared, ownsCodecPool: true)
    {
    }

    /// <summary>
    /// Creates a new MjpegHdrEngine with custom codec pool.
    /// </summary>
    public MjpegHdrEngine(
        Func<ulong, Task<IMemoryOwner<byte>>> getImageByFrameId,
        ICodecPool codecPool,
        IHdrBlend blend,
        MemoryPool<byte> pool,
        bool ownsCodecPool = false)
    {
        _getImageByFrameId = getImageByFrameId ?? throw new ArgumentNullException(nameof(getImageByFrameId));
        _codecPool = codecPool ?? throw new ArgumentNullException(nameof(codecPool));
        _blend = blend ?? throw new ArgumentNullException(nameof(blend));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _ownsCodecPool = ownsCodecPool;
    }

    /// <summary>
    /// Gets an HDR-processed frame.
    /// Fetches required frames, decodes, blends, and re-encodes to JPEG.
    /// </summary>
    /// <param name="frameId">The target frame ID (most recent frame in the blend window).</param>
    /// <returns>FrameImage containing JPEG data. Caller must dispose.</returns>
    public async Task<FrameImage> GetAsync(ulong frameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateConfiguration();

        // Fetch and decode frames
        var decodedFrames = await FetchAndDecodeFramesAsync(frameId);

        try
        {
            // Blend frames
            using var blendedFrame = BlendFrames(decodedFrames);

            // Encode to JPEG using pooled encoder
            return EncodeWithPooledEncoder(blendedFrame);
        }
        finally
        {
            // Dispose decoded frames
            foreach (var frame in decodedFrames)
            {
                frame.Dispose();
            }
        }
    }

    private FrameImage EncodeWithPooledEncoder(FrameImage frame)
    {
        var encoder = _codecPool.RentEncoder();
        try
        {
            // Rent output buffer from pool - may be larger than needed
            int maxSize = frame.Header.Length;
            var outputOwner = _pool.Rent(maxSize);

            int length = frame.Header.Format switch
            {
                PixelFormat.I420 => _codecPool.EncodeI420(encoder, frame.Data, outputOwner.Memory),
                PixelFormat.Gray8 => _codecPool.EncodeGray8(frame.Header.Width, frame.Header.Height, frame.Data, outputOwner.Memory),
                _ => throw new NotSupportedException($"Only I420 and Gray8 formats are supported. Got: {frame.Header.Format}")
            };

            // Create header with actual encoded length
            var header = new FrameHeader(
                frame.Header.Width,
                frame.Header.Height,
                length,
                frame.Header.Format,
                length);

            return new FrameImage(header, outputOwner);
        }
        finally
        {
            _codecPool.ReturnEncoder(encoder);
        }
    }

    /// <summary>
    /// Gets an HDR-processed frame synchronously.
    /// </summary>
    public FrameImage Get(ulong frameId)
    {
        return GetAsync(frameId).GetAwaiter().GetResult();
    }

    private void ValidateConfiguration()
    {
        if (HdrFrameWindowCount < 2 || HdrFrameWindowCount > 10)
            throw new InvalidOperationException("HdrFrameWindowCount must be between 2 and 10.");

        if (HdrMode == HdrBlendMode.Weighted && Weights == null)
            throw new InvalidOperationException("Weights must be set when using Weighted mode.");

        if (HdrMode == HdrBlendMode.Weighted && Weights!.NumFrames != HdrFrameWindowCount)
            throw new InvalidOperationException($"Weights.NumFrames ({Weights.NumFrames}) must match HdrFrameWindowCount ({HdrFrameWindowCount}).");

        if (HdrMode == HdrBlendMode.GrayToRgb && HdrFrameWindowCount != 3)
            throw new InvalidOperationException("GrayToRgb mode requires exactly 3 frames.");
    }

    private async Task<FrameImage[]> FetchAndDecodeFramesAsync(ulong frameId)
    {
        var frameCount = HdrFrameWindowCount;
        var decodeTasks = ArrayPool<Task<FrameImage>>.Shared.Rent(frameCount);

        try
        {
            // Start fetch + decode tasks in parallel
            for (int i = 0; i < frameCount; i++)
            {
                ulong targetFrameId = frameId >= (ulong)i ? frameId - (ulong)i : 0;
                decodeTasks[i] = FetchAndDecodeOneFrameAsync(targetFrameId);
            }

            // Await all and collect results
            var frames = new FrameImage[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frames[i] = await decodeTasks[i];
            }

            // Validate all frames have same dimensions
            var firstHeader = frames[0].Header;
            for (int i = 1; i < frameCount; i++)
            {
                if (frames[i].Header.Width != firstHeader.Width ||
                    frames[i].Header.Height != firstHeader.Height)
                {
                    // Dispose already decoded frames on error
                    foreach (var frame in frames)
                    {
                        frame.Dispose();
                    }
                    throw new InvalidOperationException(
                        $"Frame {i} dimensions ({frames[i].Header.Width}x{frames[i].Header.Height}) " +
                        $"don't match frame 0 ({firstHeader.Width}x{firstHeader.Height}).");
                }
            }

            return frames;
        }
        finally
        {
            ArrayPool<Task<FrameImage>>.Shared.Return(decodeTasks, clearArray: true);
        }
    }

    private async Task<FrameImage> FetchAndDecodeOneFrameAsync(ulong frameId)
    {
        // Fetch JPEG data (pooled)
        using var jpegOwner = await _getImageByFrameId(frameId);

        // Get dimensions to know how much to rent
        var info = _codecPool.GetImageInfo(jpegOwner.Memory);

        // Calculate I420 size: width * height * 1.5
        int i420Size = info.Width * info.Height * 3 / 2;

        // Rent decode buffer from pool
        var decodeOwner = _pool.Rent(i420Size);

        // Rent decoder from pool
        var decoder = _codecPool.RentDecoder();
        try
        {
            // Decode to I420 into pooled buffer (preserves color)
            var header = _codecPool.DecodeI420(decoder, jpegOwner.Memory, decodeOwner.Memory);

            // Return FrameImage that owns the pooled memory
            // Will be disposed in GetAsync finally block, returning buffer to pool
            return new FrameImage(header, decodeOwner);
        }
        catch
        {
            decodeOwner.Dispose();
            throw;
        }
        finally
        {
            _codecPool.ReturnDecoder(decoder);
        }
    }

    private FrameImage BlendFrames(FrameImage[] frames)
    {
        return HdrMode switch
        {
            HdrBlendMode.Average => BlendAverage(frames),
            HdrBlendMode.Weighted => BlendWeighted(frames),
            HdrBlendMode.GrayToRgb => BlendGrayToRgb(frames),
            _ => throw new NotSupportedException($"Unsupported HDR mode: {HdrMode}")
        };
    }

    private FrameImage BlendAverage(FrameImage[] frames)
    {
        return frames.Length switch
        {
            2 => _blend.Average(frames[0], frames[1]),
            3 => _blend.Average(frames[0], frames[1], frames[2]),
            _ => BlendAverageN(frames)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private FrameImage BlendAverageN(FrameImage[] frames)
    {
        // For N > 3 frames, iteratively blend
        // This matches GStreamer's blend_average_fixed<N> behavior
        var header = frames[0].Header;
        var outputOwner = _pool.Rent(header.Length);
        var output = outputOwner.Memory.Slice(0, header.Length);
        var outputSpan = output.Span;

        int length = header.Length;
        int n = frames.Length;

        for (int i = 0; i < length; i++)
        {
            uint sum = 0;
            for (int f = 0; f < n; f++)
            {
                sum += frames[f].Data.Span[i];
            }
            // GStreamer formula: (sum + N/2) / N
            outputSpan[i] = (byte)((sum + (uint)(n / 2)) / (uint)n);
        }

        return new FrameImage(header, outputOwner);
    }

    private FrameImage BlendWeighted(FrameImage[] frames)
    {
        var header = frames[0].Header;
        var outputOwner = _pool.Rent(header.Length);
        var output = outputOwner.Memory.Slice(0, header.Length);

        if (frames.Length == 2)
        {
            _blend.Weighted(frames[0], frames[1], Weights!, output);
        }
        else if (frames.Length == 3)
        {
            _blend.Weighted(frames[0], frames[1], frames[2], Weights!, output);
        }
        else
        {
            BlendWeightedN(frames, output.Span);
        }

        return new FrameImage(header, outputOwner);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void BlendWeightedN(FrameImage[] frames, Span<byte> output)
    {
        // N-frame weighted blend matching GStreamer blend_weighted_gray8_nf
        int length = frames[0].Header.Length;
        int numFrames = frames.Length;
        var weights = Weights!.AsSpan();

        for (int i = 0; i < length; i++)
        {
            // Calculate average luminance from all frames
            uint lumSum = 0;
            for (int f = 0; f < numFrames; f++)
            {
                lumSum += frames[f].Data.Span[i];
            }
            int lum = (int)(lumSum / (uint)numFrames);

            // Calculate weighted sum using Q0.8 fixed-point
            uint sum = 0;
            for (int f = 0; f < numFrames; f++)
            {
                int weightIdx = lum * numFrames + f;
                sum += (uint)frames[f].Data.Span[i] * weights[weightIdx];
            }

            // Shift by 8 to convert from Q0.8 back to integer
            uint result = sum >> 8;
            output[i] = (byte)(result > 255 ? 255 : result);
        }
    }

    private FrameImage BlendGrayToRgb(FrameImage[] frames)
    {
        if (frames.Length != 3)
            throw new InvalidOperationException("GrayToRgb requires exactly 3 frames.");

        // Validate Gray8 format
        for (int i = 0; i < 3; i++)
        {
            if (frames[i].Header.Format != PixelFormat.Gray8)
                throw new InvalidOperationException($"GrayToRgb requires Gray8 format, frame {i} is {frames[i].Header.Format}.");
        }

        int pixelCount = frames[0].Header.Width * frames[0].Header.Height;
        int outputLength = pixelCount * 3;
        var outputOwner = _pool.Rent(outputLength);
        var output = outputOwner.Memory.Slice(0, outputLength);

        var outputHeader = _blend.GrayToRgb(frames[0], frames[1], frames[2], output);

        return new FrameImage(outputHeader, outputOwner);
    }

    /// <summary>
    /// Disposes the engine and its resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (_ownsCodecPool)
        {
            _codecPool.Dispose();
        }
        _disposed = true;
    }
}
