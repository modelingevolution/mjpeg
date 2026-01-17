namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Represents detected JPEG markers during MJPEG stream decoding.
/// </summary>
public enum JpegMarker
{
    /// <summary>No marker detected at current position.</summary>
    None,

    /// <summary>Start Of Image marker (0xFF 0xD8) detected.</summary>
    Start,

    /// <summary>End Of Image marker (0xFF 0xD9) detected.</summary>
    End
}
