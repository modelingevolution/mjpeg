using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class MjpegProviderTests
{
    [Fact]
    public async Task Should_decode_frame_to_grayscale()
    {
        await using var provider = new MjpegProvider(new Uri("http://127.0.0.1:4953/"));
        await provider.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!provider.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        provider.HasFrame.Should().BeTrue();

        using var frame = provider.TryGetFrame();
        frame.Should().NotBeNull();
        frame!.Value.Header.Format.Should().Be(PixelFormat.Gray8);
        frame.Value.Header.Width.Should().BeGreaterThan(0);
        frame.Value.Header.Height.Should().BeGreaterThan(0);
        frame.Value.Data.Length.Should().Be(frame.Value.Header.Width * frame.Value.Header.Height);
    }

    [Fact]
    public async Task Should_cache_frame_dimensions()
    {
        await using var provider = new MjpegProvider(new Uri("http://127.0.0.1:4953/"));
        await provider.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!provider.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        using var frame1 = provider.TryGetFrame();
        frame1.Should().NotBeNull();

        // FrameHeader should be cached after first decode
        provider.FrameHeader.Width.Should().Be(frame1!.Value.Header.Width);
        provider.FrameHeader.Height.Should().Be(frame1.Value.Header.Height);

        // Second frame should have same dimensions
        await Task.Delay(200);
        using var frame2 = provider.TryGetFrame();
        frame2.Should().NotBeNull();
        frame2!.Value.Header.Width.Should().Be(frame1.Value.Header.Width);
        frame2.Value.Header.Height.Should().Be(frame1.Value.Header.Height);
    }

    [Fact]
    public async Task Should_provide_raw_jpeg_via_TryAcquireRaw()
    {
        await using var provider = new MjpegProvider(new Uri("http://127.0.0.1:4953/"));
        await provider.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!provider.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        using var handle = provider.TryAcquireRaw();
        handle.Should().NotBeNull();
        MjpegDecoder.IsJpeg(handle!.Value.Data.Span).Should().BeTrue();
    }
}
