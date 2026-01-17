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
/// JPEG encoding and decoding interface.
/// Thread-safe for concurrent encoding/decoding operations.
/// </summary>
public interface IJpegCodec : IDisposable
{
    /// <summary>
    /// Decodes JPEG data to raw pixel data.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <param name="outputBuffer">Buffer to receive decoded pixels. Must be large enough.</param>
    /// <returns>Frame header describing the decoded image.</returns>
    FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG data, allocating output buffer.
    /// </summary>
    /// <param name="jpegData">The JPEG compressed data.</param>
    /// <returns>FrameImage containing the decoded pixels. Caller must dispose.</returns>
    FrameImage Decode(ReadOnlyMemory<byte> jpegData);

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
    /// Default JPEG quality (1-100). Higher values produce better quality but larger files.
    /// </summary>
    int Quality { get; set; }

    /// <summary>
    /// DCT algorithm: Integer (slower, accurate) or Float (faster).
    /// </summary>
    DctMethod DctMethod { get; set; }
}
