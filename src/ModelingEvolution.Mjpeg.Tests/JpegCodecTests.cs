using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Tests for JpegCodec that verify the native LibJpegWrap library works correctly.
/// These tests require libjpeg62 to be installed on the system.
/// </summary>
public class JpegCodecTests
{
    [Fact]
    public void Constructor_ShouldLoadNativeLibrary()
    {
        // This test verifies that the native library can be loaded
        // and a codec instance can be created
        using var codec = new JpegCodec();

        codec.Should().NotBeNull();
        codec.Quality.Should().Be(85); // Default quality
    }

    [Fact]
    public void Constructor_WithOptions_ShouldApplySettings()
    {
        var options = new JpegCodecOptions
        {
            MaxWidth = 640,
            MaxHeight = 480,
            Quality = 90,
            DctMethod = DctMethod.Float
        };

        using var codec = new JpegCodec(options);

        codec.Quality.Should().Be(90);
        codec.DctMethod.Should().Be(DctMethod.Float);
    }

    [Fact]
    public void Quality_SetValue_ShouldUpdateCodec()
    {
        using var codec = new JpegCodec();

        codec.Quality = 50;

        codec.Quality.Should().Be(50);
    }

    [Fact]
    public void Quality_InvalidValue_ShouldThrow()
    {
        using var codec = new JpegCodec();

        var act = () => codec.Quality = 0;

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DctMethod_SetValue_ShouldUpdateCodec()
    {
        using var codec = new JpegCodec();

        codec.DctMethod = DctMethod.Float;

        codec.DctMethod.Should().Be(DctMethod.Float);
    }

    [Fact]
    public void Encode_I420Frame_ShouldProduceJpeg()
    {
        using var codec = new JpegCodec(new JpegCodecOptions
        {
            MaxWidth = 64,
            MaxHeight = 64,
            Quality = 80
        });

        // Create a simple 64x64 I420 frame (Y plane + U/V quarter-size planes)
        // I420: Y = width*height, U = width*height/4, V = width*height/4
        int width = 64;
        int height = 64;
        int ySize = width * height;
        int uvSize = (width / 2) * (height / 2);
        int totalSize = ySize + uvSize * 2;

        var frameData = new byte[totalSize];

        // Fill Y plane with gradient
        for (int i = 0; i < ySize; i++)
        {
            frameData[i] = (byte)(i % 256);
        }

        // Fill U and V planes with mid-gray (128 = neutral chroma)
        for (int i = ySize; i < totalSize; i++)
        {
            frameData[i] = 128;
        }

        var header = new FrameHeader(width, height, width, PixelFormat.I420, totalSize);
        var frame = new FrameImage(header, frameData);

        var outputBuffer = new byte[totalSize * 2]; // Generous buffer

        var bytesWritten = codec.Encode(frame, outputBuffer);

        bytesWritten.Should().BeGreaterThan(0);

        // Verify JPEG magic bytes (FFD8)
        outputBuffer[0].Should().Be(0xFF);
        outputBuffer[1].Should().Be(0xD8);
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        var codec = new JpegCodec();

        codec.Dispose();
        codec.Dispose(); // Second dispose should be safe
    }

    [Fact]
    public void Encode_AfterDispose_ShouldThrow()
    {
        var codec = new JpegCodec(new JpegCodecOptions
        {
            MaxWidth = 64,
            MaxHeight = 64
        });
        codec.Dispose();

        var header = new FrameHeader(64, 64, 64, PixelFormat.I420, 64 * 64 * 3 / 2);
        var frame = new FrameImage(header, new byte[header.Length]);
        var output = new byte[10000];

        var act = () => codec.Encode(frame, output);

        act.Should().Throw<ObjectDisposedException>();
    }
}
