using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class HttpFrameGrabberTests
{
    [Fact]
    public async Task Should_grab_frames_from_live_stream()
    {
        await using var grabber = new HttpFrameGrabber("127.0.0.1", 4953);
        await grabber.StartAsync();

        // Wait for first frame (up to 5 seconds)
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!grabber.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        grabber.HasFrame.Should().BeTrue("should have grabbed at least one frame");
        grabber.FrameCount.Should().BeGreaterThan(0);

        // Acquire latest frame
        using var handle = grabber.TryAcquireLatest();
        handle.Should().NotBeNull();
        handle!.Value.Length.Should().BeGreaterThan(0, "JPEG frame should have data");
        handle.Value.Data.Length.Should().Be(handle.Value.Length);

        // Verify it's a valid JPEG (starts with FFD8)
        var data = handle.Value.Data.Span;
        data[0].Should().Be(0xFF);
        data[1].Should().Be(0xD8);
    }

    [Fact]
    public async Task Should_grab_multiple_frames()
    {
        await using var grabber = new HttpFrameGrabber("127.0.0.1", 4953);
        await grabber.StartAsync();

        // Wait for several frames
        await Task.Delay(2000);

        grabber.FrameCount.Should().BeGreaterThan(5, "should have grabbed multiple frames in 2 seconds");

        // Acquire and verify
        using var handle = grabber.TryAcquireLatest();
        handle.Should().NotBeNull();
        MjpegDecoder.IsJpeg(handle!.Value.Data.Span).Should().BeTrue("frame should be valid JPEG");
    }

    [Fact]
    public async Task Handle_ref_counting_should_work()
    {
        await using var grabber = new HttpFrameGrabber("127.0.0.1", 4953);
        await grabber.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!grabber.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        // Acquire two references to the same frame
        var handle1 = grabber.TryAcquireLatest();
        handle1.Should().NotBeNull();

        var handle2Acquired = handle1!.Value.TryAddRef();
        handle2Acquired.Should().BeTrue();
        var handle2 = handle1.Value;

        // Dispose first — buffer should NOT return to pool yet
        handle1.Value.Dispose();

        // Second handle should still have valid data
        handle2.Data.Length.Should().BeGreaterThan(0);
        MjpegDecoder.IsJpeg(handle2.Data.Span).Should().BeTrue();

        // Dispose second — now buffer returns to pool
        handle2.Dispose();
    }

    [Fact]
    public async Task Should_construct_from_uri()
    {
        await using var grabber = new HttpFrameGrabber(new Uri("http://127.0.0.1:4953/"));
        await grabber.StartAsync();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!grabber.HasFrame && DateTime.UtcNow < timeout)
            await Task.Delay(50);

        grabber.HasFrame.Should().BeTrue();

        using var handle = grabber.TryAcquireLatest();
        handle.Should().NotBeNull();
        handle!.Value.Data.Span[0].Should().Be(0xFF);
        handle.Value.Data.Span[1].Should().Be(0xD8);
    }
}
