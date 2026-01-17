namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Extracts image dimensions from JPEG frame data by parsing SOF markers.
/// </summary>
public static class JpegDimensionExtractor
{
    private const byte MarkerPrefix = 0xFF;
    private const byte SOF0 = 0xC0; // Start Of Frame (Baseline DCT)
    private const byte SOF1 = 0xC1; // Start Of Frame (Extended sequential DCT)
    private const byte SOF2 = 0xC2; // Start Of Frame (Progressive DCT)
    private const byte SOF3 = 0xC3; // Start Of Frame (Lossless)

    /// <summary>
    /// Extracts width and height from JPEG data.
    /// </summary>
    /// <param name="jpegData">The JPEG frame data.</param>
    /// <returns>A tuple of (width, height), or (0, 0) if dimensions cannot be extracted.</returns>
    public static (int Width, int Height) Extract(ReadOnlySpan<byte> jpegData)
    {
        // SOF structure: FF Cx LL LL PP HH HH WW WW ...
        // Where: Cx = SOF marker, LL LL = length, PP = precision, HH HH = height, WW WW = width

        for (var i = 0; i < jpegData.Length - 9; i++)
        {
            if (jpegData[i] != MarkerPrefix) continue;

            var marker = jpegData[i + 1];

            // Check for SOF markers (SOF0, SOF1, SOF2, SOF3)
            if (marker == SOF0 || marker == SOF1 || marker == SOF2 || marker == SOF3)
            {
                var height = (jpegData[i + 5] << 8) | jpegData[i + 6];
                var width = (jpegData[i + 7] << 8) | jpegData[i + 8];
                return (width, height);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Extracts width and height from JPEG data.
    /// </summary>
    /// <param name="jpegData">The JPEG frame data.</param>
    /// <returns>A tuple of (width, height), or (0, 0) if dimensions cannot be extracted.</returns>
    public static (int Width, int Height) Extract(byte[] jpegData)
    {
        return Extract(jpegData.AsSpan());
    }

    /// <summary>
    /// Extracts width and height from JPEG data with a fallback for failures.
    /// </summary>
    /// <param name="jpegData">The JPEG frame data.</param>
    /// <param name="defaultWidth">Default width if extraction fails.</param>
    /// <param name="defaultHeight">Default height if extraction fails.</param>
    /// <returns>A tuple of (width, height).</returns>
    public static (int Width, int Height) ExtractOrDefault(
        ReadOnlySpan<byte> jpegData,
        int defaultWidth = 1024,
        int defaultHeight = 1024)
    {
        var (width, height) = Extract(jpegData);
        return width > 0 && height > 0 ? (width, height) : (defaultWidth, defaultHeight);
    }
}
