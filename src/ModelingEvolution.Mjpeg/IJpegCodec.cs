namespace ModelingEvolution.Mjpeg;

/// <summary>
/// DCT algorithm used for JPEG compression.
/// </summary>
public enum DctMethod : byte
{
    /// <summary>JDCT_ISLOW - slower but more accurate</summary>
    Integer = 0,

    /// <summary>JDCT_FASTEST - faster but less accurate</summary>
    Float = 1
}

/// <summary>
/// Chroma subsampling used when encoding Rgb24 to JPEG.
/// </summary>
public enum JpegSubsampling : byte
{
    /// <summary>4:2:0 - chroma at half resolution in both directions; libjpeg's default (<c>jpeg_set_defaults</c>).</summary>
    Ratio420 = 0,

    /// <summary>4:4:4 - chroma at full resolution; larger files, no colour bleeding at sharp edges.</summary>
    Ratio444 = 1
}

/// <summary>
/// JPEG encoding and decoding interface.
/// Thread-safe for concurrent encoding/decoding operations.
/// </summary>
public interface IJpegCodec : IDisposable
{
    /// <summary>
    /// Gets image dimensions from JPEG header without full decode.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <returns>Frame header with dimensions (Length will be width*height for Gray8).</returns>
    FrameHeader GetImageInfo(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Decodes JPEG data to grayscale (Gray8).
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <param name="outputBuffer">Buffer to receive decoded pixels. Must be large enough.</param>
    /// <returns>Frame header describing the decoded image.</returns>
    FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG data to grayscale (Gray8), allocating output buffer.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <returns>FrameImage containing the decoded pixels. Caller must dispose.</returns>
    FrameImage Decode(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Decodes JPEG data to I420 (YUV 4:2:0 planar) format.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <param name="outputBuffer">Buffer to receive decoded pixels. Must be large enough (width*height*1.5).</param>
    /// <returns>Frame header describing the decoded image.</returns>
    FrameHeader DecodeI420(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG data to I420 (YUV 4:2:0 planar) format, allocating output buffer.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <returns>FrameImage containing the decoded pixels in I420 format. Caller must dispose.</returns>
    FrameImage DecodeI420(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Encodes raw frame to JPEG.
    /// </summary>
    /// <param name="frame">The raw frame to encode.</param>
    /// <param name="outputBuffer">Buffer to receive JPEG data. Must be large enough.</param>
    /// <returns>Number of bytes written to outputBuffer.</returns>
    int Encode(in FrameImage frame, Memory<byte> outputBuffer);

    /// <summary>
    /// Encodes raw frame to JPEG, allocating output buffer.
    /// </summary>
    /// <param name="frame">The raw frame to encode.</param>
    /// <returns>FrameImage containing the JPEG data. Caller must dispose.</returns>
    FrameImage Encode(in FrameImage frame);

    /// <summary>
    /// Decodes JPEG data to packed Rgb24 (R, G, B; stride = width * 3) at full resolution.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <param name="outputBuffer">Buffer to receive decoded pixels. Must hold at least width * height * 3 bytes.</param>
    /// <returns>Frame header describing the decoded image (<see cref="PixelFormat.Rgb24"/>).</returns>
    /// <exception cref="InvalidOperationException">The data is not a decodable JPEG, or the output buffer is too small.</exception>
    /// <exception cref="NotSupportedException">The bundled native library has no Rgb24 support on this platform (linux-arm64, win-x64); it ships for linux-x64.</exception>
    FrameHeader DecodeRgb24(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG data to packed Rgb24 (R, G, B; stride = width * 3), renting the output buffer from the shared pool.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <returns>FrameImage containing the decoded pixels. Caller must dispose.</returns>
    /// <exception cref="InvalidOperationException">The data is not a decodable JPEG.</exception>
    /// <exception cref="NotSupportedException">The bundled native library has no Rgb24 support on this platform (linux-arm64, win-x64); it ships for linux-x64.</exception>
    FrameImage DecodeRgb24(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Encodes an Rgb24 frame to a baseline JPEG.
    /// </summary>
    /// <param name="frame">The frame to encode. Its format must be <see cref="PixelFormat.Rgb24"/>; rows are read with <see cref="FrameHeader.Stride"/>.</param>
    /// <param name="quality">JPEG quality (1-100), applied with libjpeg's <c>jpeg_set_quality</c> (baseline tables).</param>
    /// <param name="outputBuffer">Buffer to receive JPEG data. Must be large enough.</param>
    /// <param name="subsampling">Chroma subsampling; <see cref="JpegSubsampling.Ratio420"/> (libjpeg's default) unless specified.</param>
    /// <returns>Number of bytes written to outputBuffer.</returns>
    /// <exception cref="NotSupportedException">The frame is not <see cref="PixelFormat.Rgb24"/>, or the platform has no native Rgb24 support (linux-arm64, win-x64).</exception>
    /// <exception cref="ArgumentException">The header is invalid or the frame data is shorter than <c>Stride * (Height - 1) + Width * 3</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 1-100.</exception>
    /// <exception cref="InvalidOperationException">The encoder failed or the output buffer is too small.</exception>
    int EncodeRgb24(in FrameImage frame, int quality, Memory<byte> outputBuffer, JpegSubsampling subsampling = JpegSubsampling.Ratio420);

    /// <summary>
    /// Encodes an Rgb24 frame to a baseline JPEG, renting the output buffer from the shared pool.
    /// </summary>
    /// <param name="frame">The frame to encode. Its format must be <see cref="PixelFormat.Rgb24"/>.</param>
    /// <param name="quality">JPEG quality (1-100).</param>
    /// <param name="subsampling">Chroma subsampling; <see cref="JpegSubsampling.Ratio420"/> (libjpeg's default) unless specified.</param>
    /// <returns>FrameImage containing the JPEG data (Length = encoded size). Caller must dispose.</returns>
    /// <exception cref="NotSupportedException">The frame is not <see cref="PixelFormat.Rgb24"/>, or the platform has no native Rgb24 support (linux-arm64, win-x64).</exception>
    /// <exception cref="ArgumentException">The header is invalid or the frame data is shorter than <c>Stride * (Height - 1) + Width * 3</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 1-100.</exception>
    /// <exception cref="InvalidOperationException">The encoder failed.</exception>
    FrameImage EncodeRgb24(in FrameImage frame, int quality, JpegSubsampling subsampling = JpegSubsampling.Ratio420);

    /// <summary>
    /// Default JPEG quality (1-100). Higher values produce better quality but larger files.
    /// </summary>
    int Quality { get; set; }

    /// <summary>
    /// DCT algorithm: Integer (slower, accurate) or Float (faster).
    /// </summary>
    DctMethod DctMethod { get; set; }
}
