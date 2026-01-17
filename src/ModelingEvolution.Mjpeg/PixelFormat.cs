namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Pixel format enumeration aligned with libjpeg-turbo TJPF for direct interop.
/// Values 0-11 map directly to TJPF enum. Values 128+ are extended YUV formats.
/// </summary>
public enum PixelFormat : byte
{
    // === libjpeg-turbo compatible formats (TJPF values) ===

    /// <summary>TJPF_RGB - 3 bytes/pixel: R, G, B order</summary>
    Rgb24 = 0,

    /// <summary>TJPF_BGR - 3 bytes/pixel: B, G, R order (OpenCV default)</summary>
    Bgr24 = 1,

    /// <summary>TJPF_RGBX - 4 bytes/pixel: R, G, B, X (X ignored)</summary>
    Rgbx32 = 2,

    /// <summary>TJPF_BGRX - 4 bytes/pixel: B, G, R, X (X ignored)</summary>
    Bgrx32 = 3,

    /// <summary>TJPF_XBGR - 4 bytes/pixel: X, B, G, R</summary>
    Xbgr32 = 4,

    /// <summary>TJPF_XRGB - 4 bytes/pixel: X, R, G, B</summary>
    Xrgb32 = 5,

    /// <summary>TJPF_GRAY - 1 byte/pixel: luminance (OpenCV CV_8UC1)</summary>
    Gray8 = 6,

    /// <summary>TJPF_RGBA - 4 bytes/pixel: R, G, B, A</summary>
    Rgba32 = 7,

    /// <summary>TJPF_BGRA - 4 bytes/pixel: B, G, R, A (OpenCV CV_8UC4)</summary>
    Bgra32 = 8,

    /// <summary>TJPF_ABGR - 4 bytes/pixel: A, B, G, R</summary>
    Abgr32 = 9,

    /// <summary>TJPF_ARGB - 4 bytes/pixel: A, R, G, B</summary>
    Argb32 = 10,

    /// <summary>TJPF_CMYK - 4 bytes/pixel: C, M, Y, K</summary>
    Cmyk32 = 11,

    // === Extended YUV formats (require conversion for JPEG) ===

    /// <summary>YUY2/YUYV packed: 4 bytes per 2 pixels (Y0 U Y1 V)</summary>
    Yuy2 = 128,

    /// <summary>UYVY packed: 4 bytes per 2 pixels (U Y0 V Y1)</summary>
    Uyvy = 129,

    /// <summary>NV12 planar: Y plane + interleaved UV plane (12 bits/pixel)</summary>
    Nv12 = 130,

    /// <summary>NV21 planar: Y plane + interleaved VU plane (12 bits/pixel)</summary>
    Nv21 = 131,

    /// <summary>I420/YV12 planar: Y + U + V planes (12 bits/pixel)</summary>
    I420 = 132,
}

/// <summary>
/// Extension methods for PixelFormat.
/// </summary>
public static class PixelFormatExtensions
{
    /// <summary>
    /// Gets the number of bytes per pixel for the format.
    /// For YUV formats, returns the average bytes per pixel.
    /// </summary>
    public static int GetBytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 or PixelFormat.Bgr24 => 3,
        PixelFormat.Rgbx32 or PixelFormat.Bgrx32 or PixelFormat.Xbgr32 or PixelFormat.Xrgb32 => 4,
        PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Abgr32 or PixelFormat.Argb32 => 4,
        PixelFormat.Cmyk32 => 4,
        PixelFormat.Yuy2 or PixelFormat.Uyvy => 2, // 4 bytes per 2 pixels
        PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.I420 => 2, // 12 bits = 1.5 bytes, round up
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format")
    };

    /// <summary>
    /// Returns true if format requires YUV to RGB conversion for JPEG encoding.
    /// </summary>
    public static bool IsYuvFormat(this PixelFormat format) => (byte)format >= 128;
}
