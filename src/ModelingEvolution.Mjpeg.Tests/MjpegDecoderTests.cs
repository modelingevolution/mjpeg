using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class MjpegDecoderTests
{
    [Fact]
    public void Decode_DetectsStartMarker()
    {
        var decoder = new MjpegDecoder();

        decoder.Decode(0xFF).Should().Be(JpegMarker.None);
        decoder.Decode(0xD8).Should().Be(JpegMarker.Start);
    }

    [Fact]
    public void Decode_DetectsEndMarker()
    {
        var decoder = new MjpegDecoder();

        // First, enter the "inside frame" state
        decoder.Decode(0xFF);
        decoder.Decode(0xD8);

        // Now detect end
        decoder.Decode(0xFF).Should().Be(JpegMarker.None);
        decoder.Decode(0xD9).Should().Be(JpegMarker.End);
    }

    [Fact]
    public void Decode_IgnoresFalsePositives()
    {
        var decoder = new MjpegDecoder();

        // FF followed by non-D8 should not trigger start
        decoder.Decode(0xFF).Should().Be(JpegMarker.None);
        decoder.Decode(0x00).Should().Be(JpegMarker.None);
        decoder.Decode(0xFF).Should().Be(JpegMarker.None);
        decoder.Decode(0xD8).Should().Be(JpegMarker.Start);
    }

    [Fact]
    public void Decode_HandlesMultipleFrames()
    {
        var decoder = new MjpegDecoder();
        var markers = new List<JpegMarker>();

        // First frame
        byte[] frame1 = [0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9];
        foreach (var b in frame1)
        {
            var marker = decoder.Decode(b);
            if (marker != JpegMarker.None) markers.Add(marker);
        }

        // Second frame
        byte[] frame2 = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        foreach (var b in frame2)
        {
            var marker = decoder.Decode(b);
            if (marker != JpegMarker.None) markers.Add(marker);
        }

        markers.Should().BeEquivalentTo([
            JpegMarker.Start, JpegMarker.End,
            JpegMarker.Start, JpegMarker.End
        ]);
    }

    [Fact]
    public void IsJpeg_ValidFrame_ReturnsTrue()
    {
        byte[] validJpeg = [0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9];
        MjpegDecoder.IsJpeg(validJpeg).Should().BeTrue();
    }

    [Fact]
    public void IsJpeg_InvalidStart_ReturnsFalse()
    {
        byte[] invalidJpeg = [0x00, 0x00, 0x00, 0x00, 0xFF, 0xD9];
        MjpegDecoder.IsJpeg(invalidJpeg).Should().BeFalse();
    }

    [Fact]
    public void IsJpeg_InvalidEnd_ReturnsFalse()
    {
        byte[] invalidJpeg = [0xFF, 0xD8, 0x00, 0x00, 0x00, 0x00];
        MjpegDecoder.IsJpeg(invalidJpeg).Should().BeFalse();
    }

    [Fact]
    public void IsJpeg_TooShort_ReturnsFalse()
    {
        byte[] tooShort = [0xFF, 0xD8];
        MjpegDecoder.IsJpeg(tooShort).Should().BeFalse();
    }

    [Fact]
    public void Reset_ReturnsToInitialState()
    {
        var decoder = new MjpegDecoder();

        // Enter middle of frame detection
        decoder.Decode(0xFF);
        decoder.Decode(0xD8);
        decoder.Decode(0xFF);

        // Reset
        decoder.Reset();

        // Should need full start sequence again
        decoder.Decode(0xD8).Should().Be(JpegMarker.None); // Not preceded by FF after reset
        decoder.Decode(0xFF).Should().Be(JpegMarker.None);
        decoder.Decode(0xD8).Should().Be(JpegMarker.Start);
    }
}
