namespace ModelingEvolution.Mjpeg;

/// <summary>
/// HDR blending algorithm to use.
/// Matches GStreamer GstHdrBlendMode enum.
/// </summary>
public enum HdrBlendMode : byte
{
    /// <summary>Equal weight for all frames. GST_HDR_BLEND_MODE_AVERAGE.</summary>
    Average = 0,

    /// <summary>Per-luminance weighted blending using Q0.8 weights. GST_HDR_BLEND_MODE_WEIGHTED.</summary>
    Weighted = 1,

    /// <summary>Map 3 Gray8 frames to RGB channels. GST_HDR_BLEND_MODE_GRAY8_2_RGB.</summary>
    GrayToRgb = 2
}

/// <summary>
/// Interface for HDR frame blending operations.
/// Matches GStreamer gsthdr plugin algorithms.
/// </summary>
public interface IHdrBlend
{
    /// <summary>
    /// Blends 2 frames using simple averaging with rounding: (a + b + 1) >> 1
    /// </summary>
    FrameHeader Average(in FrameImage frameA, in FrameImage frameB, Memory<byte> output);

    /// <summary>
    /// Blends 3 frames using simple averaging with rounding: (sum + N/2) / N
    /// </summary>
    FrameHeader Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC,
        Memory<byte> output);

    /// <summary>
    /// Blends 2 frames, allocating output buffer.
    /// </summary>
    FrameImage Average(in FrameImage frameA, in FrameImage frameB);

    /// <summary>
    /// Blends 3 frames, allocating output buffer.
    /// </summary>
    FrameImage Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC);

    /// <summary>
    /// Blends 2 frames using Q0.8 weighted blending.
    /// Weight lookup: weightBase = (pix0 + pix1) &amp; ~0x1
    /// Result: (pix0 * w0 + pix1 * w1) >> 8
    /// </summary>
    FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB,
        in HdrWeights weights, Memory<byte> output);

    /// <summary>
    /// Blends 3 frames using Q0.8 weighted blending.
    /// Weight lookup: lum = average, weightIdx = lum * 3 + frameIdx
    /// Result: sum >> 8
    /// </summary>
    FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC,
        in HdrWeights weights, Memory<byte> output);

    /// <summary>
    /// Combines 3 Gray8 frames into RGB24.
    /// frameR -> Red channel, frameG -> Green channel, frameB -> Blue channel.
    /// </summary>
    FrameHeader GrayToRgb(in FrameImage frameR, in FrameImage frameG, in FrameImage frameB,
        Memory<byte> output);
}
