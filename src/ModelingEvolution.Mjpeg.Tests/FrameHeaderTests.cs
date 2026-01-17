using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class FrameHeaderTests
{
    [Fact]
    public void FrameHeader_ShouldSerializeToJson()
    {
        // Arrange
        var header = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);

        // Act
        var json = JsonSerializer.Serialize(header, FrameHeaderJsonContext.Default.FrameHeader);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("1920");
        json.Should().Contain("1080");
        json.Should().Contain("5760");
        json.Should().Contain("6220800");
    }

    [Fact]
    public void FrameHeader_ShouldDeserializeFromJson()
    {
        // Arrange
        var original = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);
        var json = JsonSerializer.Serialize(original, FrameHeaderJsonContext.Default.FrameHeader);

        // Act
        var deserialized = JsonSerializer.Deserialize(json, FrameHeaderJsonContext.Default.FrameHeader);

        // Assert
        deserialized.Should().Be(original);
        deserialized.Width.Should().Be(1920);
        deserialized.Height.Should().Be(1080);
        deserialized.Stride.Should().Be(5760);
        deserialized.Format.Should().Be(PixelFormat.Rgb24);
        deserialized.Length.Should().Be(6220800);
    }

    [Fact]
    public void FrameHeader_ShouldRoundTripAllFormats()
    {
        // Arrange
        var formats = Enum.GetValues<PixelFormat>();

        foreach (var format in formats)
        {
            var original = new FrameHeader(640, 480, 1920, format, 921600);
            var json = JsonSerializer.Serialize(original, FrameHeaderJsonContext.Default.FrameHeader);

            // Act
            var deserialized = JsonSerializer.Deserialize(json, FrameHeaderJsonContext.Default.FrameHeader);

            // Assert
            deserialized.Should().Be(original, $"Format {format} should round-trip correctly");
        }
    }

    [Fact]
    public void FrameHeader_DefaultFormat_ShouldBeRgb24()
    {
        // Rgb24 has value 0, which is default
        var header = default(FrameHeader);
        header.Format.Should().Be(PixelFormat.Rgb24);
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24, 3)]
    [InlineData(PixelFormat.Bgr24, 3)]
    [InlineData(PixelFormat.Rgba32, 4)]
    [InlineData(PixelFormat.Bgra32, 4)]
    [InlineData(PixelFormat.Gray8, 1)]
    [InlineData(PixelFormat.Cmyk32, 4)]
    public void PixelFormat_ShouldHaveCorrectBytesPerPixel(PixelFormat format, int expectedBpp)
    {
        // Use the extension method to get bytes per pixel
        var bpp = format.GetBytesPerPixel();
        bpp.Should().Be(expectedBpp);
    }

    [Fact]
    public void FrameHeader_Equality_ShouldWork()
    {
        var header1 = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);
        var header2 = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);
        var header3 = new FrameHeader(1280, 720, 3840, PixelFormat.Rgb24, 2764800);

        header1.Should().Be(header2);
        header1.Should().NotBe(header3);
        (header1 == header2).Should().BeTrue();
        (header1 != header3).Should().BeTrue();
    }

    [Fact]
    public void FrameHeader_GetHashCode_ShouldBeConsistent()
    {
        var header1 = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);
        var header2 = new FrameHeader(1920, 1080, 5760, PixelFormat.Rgb24, 6220800);

        header1.GetHashCode().Should().Be(header2.GetHashCode());
    }
}
