using System.Buffers;
using System.Runtime.CompilerServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// HDR frame blending implementation matching GStreamer gsthdr plugin algorithms.
/// </summary>
public sealed class HdrBlend : IHdrBlend
{
    private readonly MemoryPool<byte> _pool;

    /// <summary>
    /// Creates a new HdrBlend instance using shared memory pool.
    /// </summary>
    public HdrBlend() : this(MemoryPool<byte>.Shared)
    {
    }

    /// <summary>
    /// Creates a new HdrBlend instance with custom memory pool.
    /// </summary>
    public HdrBlend(MemoryPool<byte> pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    #region Average Blending - matches gsthdr_average.cpp

    /// <inheritdoc/>
    public FrameHeader Average(in FrameImage frameA, in FrameImage frameB, Memory<byte> output)
    {
        ValidateFramePair(frameA, frameB);
        ValidateOutputBuffer(frameA.Header, output);

        Average2FrameCore(frameA.Data.Span, frameB.Data.Span, output.Span);

        return frameA.Header;
    }

    /// <inheritdoc/>
    public FrameHeader Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC, Memory<byte> output)
    {
        ValidateFrameTriple(frameA, frameB, frameC);
        ValidateOutputBuffer(frameA.Header, output);

        Average3FrameCore(frameA.Data.Span, frameB.Data.Span, frameC.Data.Span, output.Span);

        return frameA.Header;
    }

    /// <inheritdoc/>
    public FrameImage Average(in FrameImage frameA, in FrameImage frameB)
    {
        ValidateFramePair(frameA, frameB);

        var owner = _pool.Rent(frameA.Header.Length);
        var output = owner.Memory.Slice(0, frameA.Header.Length);

        Average2FrameCore(frameA.Data.Span, frameB.Data.Span, output.Span);

        return new FrameImage(frameA.Header, owner);
    }

    /// <inheritdoc/>
    public FrameImage Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC)
    {
        ValidateFrameTriple(frameA, frameB, frameC);

        var owner = _pool.Rent(frameA.Header.Length);
        var output = owner.Memory.Slice(0, frameA.Header.Length);

        Average3FrameCore(frameA.Data.Span, frameB.Data.Span, frameC.Data.Span, output.Span);

        return new FrameImage(frameA.Header, owner);
    }

    /// <summary>
    /// 2-frame average with rounding: (pix0 + pix1 + 1) >> 1
    /// Matches GStreamer blend_average_fixed&lt;2&gt; at line 204.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Average2FrameCore(ReadOnlySpan<byte> src0, ReadOnlySpan<byte> src1, Span<byte> output)
    {
        int length = src0.Length;

        for (int i = 0; i < length; i++)
        {
            int pix0 = src0[i];
            int pix1 = src1[i];
            // Average with rounding: (pix0 + pix1 + 1) >> 1
            output[i] = (byte)((pix0 + pix1 + 1) >> 1);
        }
    }

    /// <summary>
    /// 3-frame average with rounding: (sum + N/2) / N
    /// Matches GStreamer blend_average_fixed template at line 136.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Average3FrameCore(ReadOnlySpan<byte> src0, ReadOnlySpan<byte> src1, ReadOnlySpan<byte> src2, Span<byte> output)
    {
        int length = src0.Length;
        const int N = 3;

        for (int i = 0; i < length; i++)
        {
            uint sum = (uint)(src0[i] + src1[i] + src2[i]);
            // Division with rounding: (sum + N/2) / N
            output[i] = (byte)((sum + N / 2) / N);
        }
    }

    #endregion

    #region Weighted Blending - matches gsthdr_weighted.cpp

    /// <inheritdoc/>
    public FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB, in HdrWeights weights, Memory<byte> output)
    {
        ValidateFramePair(frameA, frameB);
        ValidateOutputBuffer(frameA.Header, output);

        if (weights.NumFrames != 2)
            throw new ArgumentException("Weights must be configured for 2 frames.", nameof(weights));

        Weighted2FrameCore(frameA.Data.Span, frameB.Data.Span, weights.AsSpan(), output.Span);

        return frameA.Header;
    }

    /// <inheritdoc/>
    public FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC,
        in HdrWeights weights, Memory<byte> output)
    {
        ValidateFrameTriple(frameA, frameB, frameC);
        ValidateOutputBuffer(frameA.Header, output);

        if (weights.NumFrames != 3)
            throw new ArgumentException("Weights must be configured for 3 frames.", nameof(weights));

        Weighted3FrameCore(frameA.Data.Span, frameB.Data.Span, frameC.Data.Span, weights.AsSpan(), output.Span);

        return frameA.Header;
    }

    /// <summary>
    /// 2-frame weighted blend using Q0.8 fixed-point arithmetic.
    /// Matches GStreamer blend_weighted_gray8_2f at lines 287-303.
    ///
    /// Algorithm:
    /// 1. weight_base = (pix0 + pix1) &amp; ~0x1  (sum with LSB cleared)
    /// 2. w0 = weights[weight_base], w1 = weights[weight_base + 1]
    /// 3. result = (pix0 * w0 + pix1 * w1) >> 8
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Weighted2FrameCore(ReadOnlySpan<byte> src0, ReadOnlySpan<byte> src1, ReadOnlySpan<byte> weights, Span<byte> output)
    {
        int length = src0.Length;

        for (int i = 0; i < length; i++)
        {
            int pix0 = src0[i];
            int pix1 = src1[i];

            // Calculate weight index using sum of both pixels
            // Mathematical equivalence: (pix0 + pix1)/2 * 2 = (pix0 + pix1) & ~0x1
            // The & ~0x1 clears the LSB, making the sum even, which gives us
            // 256 possible indices (0,2,4...510) into our 512-element weight array.
            // Each even index points to a pair of weights [w0, w1] for the two frames.
            int weightBase = (pix0 + pix1) & ~0x1;

            // Q0.8 fixed-point arithmetic: multiply and shift by 8
            byte w0 = weights[weightBase];
            byte w1 = weights[weightBase + 1];
            uint result = ((uint)pix0 * w0 + (uint)pix1 * w1) >> 8;

            output[i] = (byte)(result > 255 ? 255 : result);
        }
    }

    /// <summary>
    /// N-frame weighted blend using Q0.8 fixed-point arithmetic.
    /// Matches GStreamer blend_weighted_gray8_nf at lines 369-393.
    ///
    /// Algorithm:
    /// 1. lum = average luminance of all frames
    /// 2. For each frame: weight_idx = lum * num_frames + frame_idx
    /// 3. sum += pixel_value * weights[weight_idx]
    /// 4. result = sum >> 8
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Weighted3FrameCore(ReadOnlySpan<byte> src0, ReadOnlySpan<byte> src1, ReadOnlySpan<byte> src2,
        ReadOnlySpan<byte> weights, Span<byte> output)
    {
        int length = src0.Length;
        const int numFrames = 3;

        for (int i = 0; i < length; i++)
        {
            int pix0 = src0[i];
            int pix1 = src1[i];
            int pix2 = src2[i];

            // Calculate average luminance from all frames
            int lum = (pix0 + pix1 + pix2) / numFrames;

            // Calculate weighted sum using Q0.8 fixed-point
            int weightIdx0 = lum * numFrames + 0;
            int weightIdx1 = lum * numFrames + 1;
            int weightIdx2 = lum * numFrames + 2;

            uint sum = (uint)pix0 * weights[weightIdx0]
                     + (uint)pix1 * weights[weightIdx1]
                     + (uint)pix2 * weights[weightIdx2];

            // Shift by 8 to convert from Q0.8 back to integer
            uint result = sum >> 8;
            output[i] = (byte)(result > 255 ? 255 : result);
        }
    }

    #endregion

    #region GrayToRgb - matches gsthdr_average.cpp blend_gray8_to_rgb

    /// <inheritdoc/>
    public FrameHeader GrayToRgb(in FrameImage frameR, in FrameImage frameG, in FrameImage frameB, Memory<byte> output)
    {
        ValidateGrayFrames(frameR, frameG, frameB);

        int pixelCount = frameR.Header.Width * frameR.Header.Height;
        int outputLength = pixelCount * 3;

        if (output.Length < outputLength)
            throw new ArgumentException($"Output buffer too small. Need {outputLength}, got {output.Length}.", nameof(output));

        // Matches GStreamer blend_gray8_to_rgb at lines 57-69
        // RGB format is interleaved as R, G, B for each pixel
        GrayToRgbCore(frameR.Data.Span, frameG.Data.Span, frameB.Data.Span, output.Span);

        return new FrameHeader(
            frameR.Header.Width,
            frameR.Header.Height,
            frameR.Header.Width * 3,
            PixelFormat.Rgb24,
            outputLength);
    }

    /// <summary>
    /// Combines 3 Gray8 frames into interleaved RGB24.
    /// Matches GStreamer blend_gray8_to_rgb at lines 64-68.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void GrayToRgbCore(ReadOnlySpan<byte> red, ReadOnlySpan<byte> green, ReadOnlySpan<byte> blue, Span<byte> output)
    {
        int length = red.Length;
        int outIdx = 0;

        for (int i = 0; i < length; i++)
        {
            // Interleaved RGB format - 3 bytes per pixel
            output[outIdx++] = red[i];    // R
            output[outIdx++] = green[i];  // G
            output[outIdx++] = blue[i];   // B
        }
    }

    #endregion

    #region Validation

    private static void ValidateFramePair(in FrameImage a, in FrameImage b)
    {
        if (a.Header.Width != b.Header.Width || a.Header.Height != b.Header.Height)
            throw new ArgumentException("Frame dimensions must match.");

        if (a.Header.Format != b.Header.Format)
            throw new ArgumentException("Frame formats must match.");

        if (a.Header.Length != b.Header.Length)
            throw new ArgumentException("Frame lengths must match.");
    }

    private static void ValidateFrameTriple(in FrameImage a, in FrameImage b, in FrameImage c)
    {
        ValidateFramePair(a, b);
        ValidateFramePair(a, c);
    }

    private static void ValidateGrayFrames(in FrameImage r, in FrameImage g, in FrameImage b)
    {
        if (r.Header.Format != PixelFormat.Gray8)
            throw new ArgumentException("GrayToRgb requires Gray8 format.", nameof(r));
        if (g.Header.Format != PixelFormat.Gray8)
            throw new ArgumentException("GrayToRgb requires Gray8 format.", nameof(g));
        if (b.Header.Format != PixelFormat.Gray8)
            throw new ArgumentException("GrayToRgb requires Gray8 format.", nameof(b));

        ValidateFrameTriple(r, g, b);
    }

    private static void ValidateOutputBuffer(in FrameHeader header, Memory<byte> output)
    {
        if (output.Length < header.Length)
            throw new ArgumentException($"Output buffer too small. Need {header.Length}, got {output.Length}.", nameof(output));
    }

    #endregion
}
