namespace ModelingEvolution.Mjpeg;

/// <summary>
/// State machine decoder for detecting JPEG frame boundaries when scanning in reverse.
/// Useful for seeking backwards through MJPEG streams.
/// </summary>
public sealed class ReverseMjpegDecoder
{
    private Func<byte, JpegMarker> _state;

    public ReverseMjpegDecoder() => _state = DetectStart_1;

    private JpegMarker DetectStart_1(byte b)
    {
        if (b == 0xD8)
            _state = DetectStart_2;
        return JpegMarker.None;
    }

    private JpegMarker DetectStart_2(byte b)
    {
        if (b == 0xFF)
            _state = DetectEnd_1;
        else
        {
            _state = DetectStart_1;
            return JpegMarker.None;
        }
        return JpegMarker.Start;
    }

    private JpegMarker DetectEnd_1(byte b)
    {
        if (b == 0xD9)
            _state = DetectEnd_2;
        else
        {
            _state = DetectEnd_1;
        }

        return JpegMarker.None;
    }

    private JpegMarker DetectEnd_2(byte b)
    {
        if (b == 0xFF)
            _state = DetectStart_1;

        return JpegMarker.End;
    }

    /// <summary>
    /// Processes a single byte (in reverse order) and returns the detected marker.
    /// </summary>
    /// <param name="b">The byte to process.</param>
    /// <returns>The detected marker, or None if no marker boundary at this position.</returns>
    public JpegMarker Decode(byte b) => _state(b);

    /// <summary>
    /// Resets the decoder state machine to initial state.
    /// </summary>
    public void Reset() => _state = DetectStart_1;
}
