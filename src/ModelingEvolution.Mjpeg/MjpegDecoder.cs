namespace ModelingEvolution.Mjpeg;

/// <summary>
/// State machine decoder for detecting JPEG frame boundaries in MJPEG streams.
/// Processes bytes sequentially and emits markers when frame start/end is detected.
/// </summary>
public sealed class MjpegDecoder
{
    private Func<byte, JpegMarker?> _state;

    public MjpegDecoder() => _state = DetectStart_1;

    /// <summary>
    /// Validates if a memory buffer contains a valid JPEG frame.
    /// Checks for SOI marker at start and EOI marker at end.
    /// </summary>
    public static bool IsJpeg(Memory<byte> frame)
    {
        if (frame.Length < 4) return false;

        var span = frame.Span;
        if (span[0] == 0xFF && span[1] == 0xD8)
        {
            var last = frame.Length - 2;
            if (span[last] == 0xFF && span[last + 1] == 0xD9)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates if a span contains a valid JPEG frame.
    /// </summary>
    public static bool IsJpeg(ReadOnlySpan<byte> span)
    {
        if (span.Length < 4) return false;

        if (span[0] == 0xFF && span[1] == 0xD8)
        {
            var last = span.Length - 2;
            if (span[last] == 0xFF && span[last + 1] == 0xD9)
                return true;
        }

        return false;
    }

    private JpegMarker? DetectStart_1(byte b)
    {
        if (b == 0xFF)
            _state = DetectStart_2;
        return null;
    }

    private JpegMarker? DetectStart_2(byte b)
    {
        if (b == 0xD8)
            _state = DetectEnd_1;
        else
        {
            _state = DetectStart_1;
            return null;
        }
        return JpegMarker.Start;
    }

    private JpegMarker? DetectEnd_1(byte b)
    {
        if (b == 0xFF)
            _state = DetectEnd_2;

        return null;
    }

    private JpegMarker? DetectEnd_2(byte b)
    {
        if (b == 0xD9)
            _state = DetectStart_1;
        else
        {
            _state = DetectEnd_1;
            return null;
        }
        return JpegMarker.End;
    }

    /// <summary>
    /// Processes a single byte and returns the detected marker.
    /// </summary>
    /// <param name="b">The byte to process.</param>
    /// <returns>The detected marker, or None if no marker boundary at this position.</returns>
    public JpegMarker Decode(byte b) => _state(b) ?? JpegMarker.None;

    /// <summary>
    /// Resets the decoder state machine to initial state.
    /// </summary>
    public void Reset() => _state = DetectStart_1;
}
