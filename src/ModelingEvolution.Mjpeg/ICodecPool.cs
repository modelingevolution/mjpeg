namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Interface for codec pooling operations.
/// Allows mocking in tests and alternative implementations.
/// </summary>
public interface ICodecPool : IDisposable
{
    /// <summary>
    /// Maximum image width supported by this pool.
    /// </summary>
    int MaxWidth { get; }

    /// <summary>
    /// Maximum image height supported by this pool.
    /// </summary>
    int MaxHeight { get; }

    /// <summary>
    /// JPEG quality setting.
    /// </summary>
    int Quality { get; }

    /// <summary>
    /// Rents an encoder from the pool.
    /// </summary>
    nint RentEncoder();

    /// <summary>
    /// Returns an encoder to the pool.
    /// </summary>
    void ReturnEncoder(nint encoder);

    /// <summary>
    /// Rents a decoder from the pool.
    /// </summary>
    nint RentDecoder();

    /// <summary>
    /// Returns a decoder to the pool.
    /// </summary>
    void ReturnDecoder(nint decoder);

    /// <summary>
    /// Gets image info from JPEG data.
    /// </summary>
    FrameHeader GetImageInfo(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Decodes JPEG to I420 format using a pooled decoder.
    /// </summary>
    FrameHeader DecodeI420(nint decoder, ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG to grayscale format using a pooled decoder.
    /// </summary>
    FrameHeader DecodeGray(nint decoder, ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Encodes I420 frame to JPEG using a pooled encoder.
    /// </summary>
    int EncodeI420(nint encoder, ReadOnlyMemory<byte> frameData, Memory<byte> outputBuffer);

    /// <summary>
    /// Encodes Gray8 frame to JPEG.
    /// </summary>
    int EncodeGray8(int width, int height, ReadOnlyMemory<byte> frameData, Memory<byte> outputBuffer);
}
