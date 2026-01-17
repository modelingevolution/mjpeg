using System.Text.Json.Serialization;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Describes the layout of raw frame data. JSON serializable for configuration and metadata persistence.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Bytes per row (may include padding for alignment).</param>
/// <param name="Format">Pixel format of the frame data.</param>
/// <param name="Length">Total bytes in the frame data buffer.</param>
public readonly record struct FrameHeader(
    int Width,
    int Height,
    int Stride,
    PixelFormat Format,
    int Length)
{
    /// <summary>
    /// Creates a FrameHeader with calculated stride and length for the given dimensions and format.
    /// </summary>
    public static FrameHeader Create(int width, int height, PixelFormat format)
    {
        int bytesPerPixel = format.GetBytesPerPixel();
        int stride = width * bytesPerPixel;
        int length = stride * height;

        // Adjust for planar YUV formats
        if (format == PixelFormat.I420 || format == PixelFormat.Nv12 || format == PixelFormat.Nv21)
        {
            // Y plane + UV planes (half resolution each)
            length = width * height + (width * height / 2);
        }

        return new FrameHeader(width, height, stride, format, length);
    }

    /// <summary>
    /// Validates that the header dimensions are consistent.
    /// </summary>
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        Stride >= Width * Format.GetBytesPerPixel() &&
        Length >= Stride * Height;
}

/// <summary>
/// JSON serialization context for FrameHeader.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FrameHeader))]
public partial class FrameHeaderJsonContext : JsonSerializerContext
{
}
