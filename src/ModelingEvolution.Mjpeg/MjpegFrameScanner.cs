namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Information about a detected JPEG frame within an MJPEG stream.
/// </summary>
/// <param name="StartOffset">Byte offset where the frame starts (at SOI marker).</param>
/// <param name="Size">Size of the frame in bytes (including SOI and EOI markers).</param>
/// <param name="FrameIndex">Zero-based index of this frame in the stream.</param>
public readonly record struct FrameInfo(long StartOffset, long Size, ulong FrameIndex);

/// <summary>
/// Scans MJPEG streams to extract frame boundary information.
/// </summary>
public static class MjpegFrameScanner
{
    /// <summary>
    /// Scans a stream and yields frame information for each detected JPEG frame.
    /// </summary>
    /// <param name="stream">The MJPEG stream to scan.</param>
    /// <param name="bufferSize">Size of the read buffer (default 64KB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of frame information.</returns>
    public static async IAsyncEnumerable<FrameInfo> ScanAsync(
        Stream stream,
        int bufferSize = 64 * 1024,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decoder = new MjpegDecoder();
        var buffer = new byte[bufferSize];
        long position = 0;
        long frameStart = -1;
        ulong frameIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0) break;

            for (var i = 0; i < bytesRead; i++)
            {
                var absolutePosition = position + i;
                var marker = decoder.Decode(buffer[i]);

                switch (marker)
                {
                    case JpegMarker.Start when frameStart < 0:
                        frameStart = absolutePosition - 1; // -1 because SOI is 2 bytes (FF D8)
                        break;

                    case JpegMarker.End when frameStart >= 0:
                        var frameSize = absolutePosition - frameStart + 1;
                        yield return new FrameInfo(frameStart, frameSize, frameIndex);
                        frameIndex++;
                        frameStart = -1;
                        break;
                }
            }

            position += bytesRead;
        }
    }

    /// <summary>
    /// Scans a byte array and returns all frame information.
    /// </summary>
    /// <param name="data">The MJPEG data to scan.</param>
    /// <returns>List of frame information.</returns>
    public static List<FrameInfo> Scan(ReadOnlySpan<byte> data)
    {
        var frames = new List<FrameInfo>();
        var decoder = new MjpegDecoder();
        long frameStart = -1;
        ulong frameIndex = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var marker = decoder.Decode(data[i]);

            switch (marker)
            {
                case JpegMarker.Start when frameStart < 0:
                    frameStart = i - 1;
                    break;

                case JpegMarker.End when frameStart >= 0:
                    var frameSize = i - frameStart + 1;
                    frames.Add(new FrameInfo(frameStart, frameSize, frameIndex));
                    frameIndex++;
                    frameStart = -1;
                    break;
            }
        }

        return frames;
    }
}
