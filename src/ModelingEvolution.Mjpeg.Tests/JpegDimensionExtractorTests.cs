using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class JpegDimensionExtractorTests
{
    [Fact]
    public void Extract_ValidSOF0_ReturnsDimensions()
    {
        // Minimal JPEG with SOF0 marker indicating 640x480
        byte[] jpeg =
        [
            0xFF, 0xD8,                         // SOI
            0xFF, 0xC0,                         // SOF0
            0x00, 0x0B,                         // Length (11 bytes)
            0x08,                               // Precision (8 bits)
            0x01, 0xE0,                         // Height (480)
            0x02, 0x80,                         // Width (640)
            0x03,                               // Components
            0xFF, 0xD9                          // EOI
        ];

        var (width, height) = JpegDimensionExtractor.Extract(jpeg);

        width.Should().Be(640);
        height.Should().Be(480);
    }

    [Fact]
    public void Extract_ValidSOF2_ReturnsDimensions()
    {
        // JPEG with SOF2 (Progressive) marker indicating 1920x1080
        byte[] jpeg =
        [
            0xFF, 0xD8,                         // SOI
            0xFF, 0xC2,                         // SOF2 (Progressive)
            0x00, 0x0B,                         // Length
            0x08,                               // Precision
            0x04, 0x38,                         // Height (1080)
            0x07, 0x80,                         // Width (1920)
            0x03,                               // Components
            0xFF, 0xD9                          // EOI
        ];

        var (width, height) = JpegDimensionExtractor.Extract(jpeg);

        width.Should().Be(1920);
        height.Should().Be(1080);
    }

    [Fact]
    public void Extract_NoSOFMarker_ReturnsZero()
    {
        byte[] jpeg = [0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9];

        var (width, height) = JpegDimensionExtractor.Extract(jpeg);

        width.Should().Be(0);
        height.Should().Be(0);
    }

    [Fact]
    public void Extract_EmptyData_ReturnsZero()
    {
        var (width, height) = JpegDimensionExtractor.Extract(ReadOnlySpan<byte>.Empty);

        width.Should().Be(0);
        height.Should().Be(0);
    }

    [Fact]
    public void ExtractOrDefault_NoSOFMarker_ReturnsDefaults()
    {
        byte[] jpeg = [0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9];

        var (width, height) = JpegDimensionExtractor.ExtractOrDefault(jpeg, 800, 600);

        width.Should().Be(800);
        height.Should().Be(600);
    }

    [Fact]
    public void ExtractOrDefault_ValidSOF_ReturnsDimensions()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0B, 0x08,
            0x00, 0x64,                         // Height (100)
            0x00, 0xC8,                         // Width (200)
            0x03,
            0xFF, 0xD9
        ];

        var (width, height) = JpegDimensionExtractor.ExtractOrDefault(jpeg, 800, 600);

        width.Should().Be(200);
        height.Should().Be(100);
    }
}
