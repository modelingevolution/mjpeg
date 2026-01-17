using System.Buffers;
using System.Runtime.CompilerServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// HDR processing engine for MJPEG streams.
/// Fetches frames, decodes JPEG, blends using HDR algorithms, and re-encodes to JPEG.
/// Matches GStreamer gsthdr plugin processing pipeline.
/// </summary>
public sealed class MjpegHdrEngine : IDisposable
{
    private readonly Func<ulong, Task<IMemoryOwner<byte>>> _getImageByFrameId;
    private readonly IJpegCodec _codec;
    private readonly IHdrBlend _blend;
    private readonly MemoryPool<byte> _pool;
    private bool _disposed;

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
    public int JpegQuality
    {
        get => _codec.Quality;
        set => _codec.Quality = value;
    }

    /// <summary>
    /// Creates a new MjpegHdrEngine.
    /// </summary>
    /// <param name="getImageByFrameId">
    /// Async function to fetch JPEG data by frame ID.
    /// Returns IMemoryOwner containing JPEG bytes. Caller takes ownership.
    /// </param>
    public MjpegHdrEngine(Func<ulong, Task<IMemoryOwner<byte>>> getImageByFrameId)
        : this(getImageByFrameId, new JpegCodec(), new HdrBlend(), MemoryPool<byte>.Shared)
    {
    }

    /// <summary>
    /// Creates a new MjpegHdrEngine with custom dependencies.
    /// </summary>
    public MjpegHdrEngine(
        Func<ulong, Task<IMemoryOwner<byte>>> getImageByFrameId,
        IJpegCodec codec,
        IHdrBlend blend,
        MemoryPool<byte> pool)
    {
        _getImageByFrameId = getImageByFrameId ?? throw new ArgumentNullException(nameof(getImageByFrameId));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _blend = blend ?? throw new ArgumentNullException(nameof(blend));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
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

            // Encode to JPEG
            return _codec.Encode(blendedFrame);
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
        var decodeTasks = new Task<FrameImage>[frameCount];

        // Start fetch + decode tasks in parallel
        // Each task fetches JPEG data and decodes it, allowing parallel decode operations
        for (int i = 0; i < frameCount; i++)
        {
            ulong targetFrameId = frameId >= (ulong)i ? frameId - (ulong)i : 0;
            decodeTasks[i] = FetchAndDecodeOneFrameAsync(targetFrameId);
        }

        // Wait for all parallel fetch+decode operations
        var frames = await Task.WhenAll(decodeTasks);

        // Validate all frames have same dimensions
        var firstHeader = frames[0].Header;
        for (int i = 1; i < frames.Length; i++)
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

    private async Task<FrameImage> FetchAndDecodeOneFrameAsync(ulong frameId)
    {
        // Fetch JPEG data
        using var jpegOwner = await _getImageByFrameId(frameId);

        // Decode immediately (runs in parallel with other fetch+decode tasks)
        return _codec.Decode(jpegOwner.Memory);
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

        _codec.Dispose();
        _disposed = true;
    }
}
