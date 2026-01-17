using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// HDR weight matrix for weighted blending. Uses Q0.8 fixed-point format (0-255).
/// Layout: weights[luminance * numFrames + frameIndex]
/// Weights at each luminance level must sum to 255.
/// Matches GStreamer gsthdr_weighted.cpp implementation.
/// Thread-safe: Weights property can be atomically updated via ImmutableInterlocked.
/// </summary>
public sealed class HdrWeights
{
    private ImmutableArray<byte> _weights;

    /// <summary>
    /// Weight values in Q0.8 fixed-point format (0-255).
    /// Layout: weights[luminance * NumFrames + frameIndex]
    /// Thread-safe atomic read/write.
    /// </summary>
    public ImmutableArray<byte> Weights
    {
        get => _weights;
        set => ImmutableInterlocked.InterlockedExchange(ref _weights, value);
    }

    /// <summary>
    /// Number of frames this weight matrix supports.
    /// </summary>
    public int NumFrames { get; }

    /// <summary>
    /// Number of channels (1 for grayscale, 3 for RGB per-channel weights).
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Total size of weights array.
    /// </summary>
    public int Size => _weights.Length;

    /// <summary>
    /// Creates a weight matrix for the specified number of frames.
    /// Initializes with equal weights (255 / numFrames for each frame).
    /// </summary>
    /// <param name="numFrames">Number of frames (2-10).</param>
    /// <param name="channels">Number of channels (1 for grayscale, 3 for RGB).</param>
    public HdrWeights(int numFrames, int channels = 1)
    {
        if (numFrames < 2 || numFrames > 10)
            throw new ArgumentOutOfRangeException(nameof(numFrames), "NumFrames must be between 2 and 10.");
        if (channels != 1 && channels != 3)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1 (grayscale) or 3 (RGB).");

        NumFrames = numFrames;
        Channels = channels;
        _weights = CreateEqualWeights(numFrames, channels);
    }

    /// <summary>
    /// Creates a weight matrix from existing weights array.
    /// </summary>
    [JsonConstructor]
    public HdrWeights(byte[] weights, int numFrames, int channels)
    {
        if (numFrames < 2 || numFrames > 10)
            throw new ArgumentOutOfRangeException(nameof(numFrames), "NumFrames must be between 2 and 10.");
        if (channels != 1 && channels != 3)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1 (grayscale) or 3 (RGB).");

        int expectedSize = channels * 256 * numFrames;
        if (weights.Length != expectedSize)
            throw new ArgumentException($"Weights array must have {expectedSize} elements, got {weights.Length}.", nameof(weights));

        NumFrames = numFrames;
        Channels = channels;
        _weights = ImmutableArray.Create(weights);
    }

    /// <summary>
    /// Creates a weight matrix from existing immutable weights array.
    /// </summary>
    public HdrWeights(ImmutableArray<byte> weights, int numFrames, int channels)
    {
        if (numFrames < 2 || numFrames > 10)
            throw new ArgumentOutOfRangeException(nameof(numFrames), "NumFrames must be between 2 and 10.");
        if (channels != 1 && channels != 3)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1 (grayscale) or 3 (RGB).");

        int expectedSize = channels * 256 * numFrames;
        if (weights.Length != expectedSize)
            throw new ArgumentException($"Weights array must have {expectedSize} elements, got {weights.Length}.", nameof(weights));

        NumFrames = numFrames;
        Channels = channels;
        _weights = weights;
    }

    /// <summary>
    /// Creates equal weights distribution (255 / numFrames for each frame).
    /// Matches GStreamer init_default_weights_double_buffer().
    /// </summary>
    private static ImmutableArray<byte> CreateEqualWeights(int numFrames, int channels)
    {
        var builder = ImmutableArray.CreateBuilder<byte>(channels * 256 * numFrames);
        builder.Count = channels * 256 * numFrames;

        for (int channel = 0; channel < channels; channel++)
        {
            for (int lum = 0; lum < 256; lum++)
            {
                int remaining = 255;
                for (int frame = 0; frame < numFrames - 1; frame++)
                {
                    byte weight = (byte)(255 / numFrames);
                    builder[channel * 256 * numFrames + lum * numFrames + frame] = weight;
                    remaining -= weight;
                }
                // Last frame gets the remainder to ensure sum is exactly 255
                builder[channel * 256 * numFrames + lum * numFrames + (numFrames - 1)] = (byte)remaining;
            }
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Gets the weight index for a given luminance and frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetWeightIndex(int luminance, int frameIndex, int channel = 0)
    {
        return channel * 256 * NumFrames + luminance * NumFrames + frameIndex;
    }

    /// <summary>
    /// Gets the weight for a specific luminance and frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetWeight(int luminance, int frameIndex, int channel = 0)
    {
        return Weights[GetWeightIndex(luminance, frameIndex, channel)];
    }

    /// <summary>
    /// Creates 2-frame linear weights where dark pixels prefer frame 0, bright pixels prefer frame 1.
    /// </summary>
    public static HdrWeights CreateLinear2Frame()
    {
        var builder = ImmutableArray.CreateBuilder<byte>(512);
        builder.Count = 512;

        for (int lum = 0; lum < 256; lum++)
        {
            // Linear: w0 decreases from 255 to 0, w1 increases from 0 to 255
            byte w1 = (byte)lum;
            byte w0 = (byte)(255 - w1);
            builder[lum * 2] = w0;
            builder[lum * 2 + 1] = w1;
        }

        return new HdrWeights(builder.MoveToImmutable(), numFrames: 2, channels: 1);
    }

    /// <summary>
    /// Creates 2-frame equal weights (127/128 split for all luminances).
    /// </summary>
    public static HdrWeights CreateEqual2Frame()
    {
        return new HdrWeights(2);
    }

    /// <summary>
    /// Creates 2-frame inverse linear weights where dark pixels prefer frame 1, bright pixels prefer frame 0.
    /// </summary>
    public static HdrWeights CreateInverseLinear2Frame()
    {
        var builder = ImmutableArray.CreateBuilder<byte>(512);
        builder.Count = 512;

        for (int lum = 0; lum < 256; lum++)
        {
            // Inverse linear: w0 increases from 0 to 255, w1 decreases from 255 to 0
            byte w0 = (byte)lum;
            byte w1 = (byte)(255 - w0);
            builder[lum * 2] = w0;
            builder[lum * 2 + 1] = w1;
        }

        return new HdrWeights(builder.MoveToImmutable(), numFrames: 2, channels: 1);
    }

    /// <summary>
    /// Creates N-frame equal weights.
    /// </summary>
    public static HdrWeights CreateEqual(int numFrames)
    {
        return new HdrWeights(numFrames);
    }

    /// <summary>
    /// Gets the underlying weights as a span for high-performance access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() => _weights.AsSpan();
}

/// <summary>
/// JSON serialization context for HdrWeights.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HdrWeights))]
public partial class HdrWeightsJsonContext : JsonSerializerContext
{
}
