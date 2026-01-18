using System.Runtime.CompilerServices;

namespace ModelingEvolution.Mjpeg.Cli;

/// <summary>
/// Represents detected JPEG markers during MJPEG stream decoding.
/// </summary>
internal enum JpegMarker
{
    None,
    Start,  // SOI (0xFF 0xD8)
    End     // EOI (0xFF 0xD9)
}

/// <summary>
/// Information about a detected JPEG frame within an MJPEG stream.
/// </summary>
internal readonly record struct FrameInfo(long StartOffset, long Size, ulong FrameIndex);

/// <summary>
/// State machine decoder for detecting JPEG frame boundaries in MJPEG streams.
/// </summary>
internal sealed class MjpegDecoder
{
    private Func<byte, JpegMarker?> _state;

    public MjpegDecoder() => _state = DetectStart_1;

    private JpegMarker? DetectStart_1(byte b)
    {
        if (b == 0xFF) _state = DetectStart_2;
        return null;
    }

    private JpegMarker? DetectStart_2(byte b)
    {
        if (b == 0xD8)
        {
            _state = DetectEnd_1;
            return JpegMarker.Start;
        }
        _state = DetectStart_1;
        return null;
    }

    private JpegMarker? DetectEnd_1(byte b)
    {
        if (b == 0xFF) _state = DetectEnd_2;
        return null;
    }

    private JpegMarker? DetectEnd_2(byte b)
    {
        if (b == 0xD9)
        {
            _state = DetectStart_1;
            return JpegMarker.End;
        }
        _state = DetectEnd_1;
        return null;
    }

    public JpegMarker Decode(byte b) => _state(b) ?? JpegMarker.None;
}

/// <summary>
/// Scans MJPEG streams to extract frame boundary information.
/// </summary>
internal static class MjpegScanner
{
    /// <summary>
    /// Scans a stream and yields frame information for each detected JPEG frame.
    /// </summary>
    public static async IAsyncEnumerable<FrameInfo> ScanAsync(
        Stream stream,
        int bufferSize = 64 * 1024,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                        frameStart = absolutePosition - 1; // -1 for 2-byte SOI marker
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
    /// Scans an MJPEG file and builds a frame index with format detection.
    /// </summary>
    public static async Task<(SortedList<ulong, FrameIndex> Index, PixelFormat Format)> ScanFileAsync(
        string filePath,
        int assumedFps = 25,
        CancellationToken ct = default)
    {
        var index = new SortedList<ulong, FrameIndex>();
        var format = PixelFormat.I420; // Default
        var frameIntervalMicroseconds = (ulong)(1_000_000 / assumedFps);

        await using var stream = File.OpenRead(filePath);

        await foreach (var frame in ScanAsync(stream, cancellationToken: ct))
        {
            // Detect format from first frame
            if (frame.FrameIndex == 0)
            {
                format = await DetectFormatAsync(filePath, frame);
            }

            index[frame.FrameIndex] = new FrameIndex
            {
                Start = (ulong)frame.StartOffset,
                Size = (ulong)frame.Size,
                RelativeTimestampMicroseconds = frame.FrameIndex * frameIntervalMicroseconds
            };
        }

        return (index, format);
    }

    private static async Task<PixelFormat> DetectFormatAsync(string filePath, FrameInfo frame)
    {
        var buffer = new byte[Math.Min(frame.Size, 1024)];
        await using var stream = File.OpenRead(filePath);
        stream.Seek(frame.StartOffset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer);

        // Find SOF marker and get component count
        for (int i = 0; i < buffer.Length - 9; i++)
        {
            if (buffer[i] == 0xFF && (buffer[i + 1] == 0xC0 || buffer[i + 1] == 0xC2))
            {
                var components = buffer[i + 9];
                return components == 1 ? PixelFormat.Gray8 : PixelFormat.I420;
            }
        }

        return PixelFormat.I420;
    }
}
