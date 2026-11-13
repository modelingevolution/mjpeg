using System.Buffers;
using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// A frame and the count it was published under are read together (rocket-welder2 #441 review S-11). The publish is held
/// right after the new frame is swapped in, a reader reads meanwhile, and the pair it gets must be the frame's own: the new
/// frame with the new count, never the new frame with the previous count.
/// </summary>
public class HttpFrameGrabberFrameCountTests
{
    private static ReadOnlySequence<byte> Jpeg(byte marker) => new([0xFF, 0xD8, marker, 0xFF, 0xD9]);

    [Fact]
    public async Task A_frame_is_read_with_its_own_count_while_a_publish_is_in_progress()
    {
        using var grabber = new HttpFrameGrabber("127.0.0.1", 1);   // never started: frames are published directly
        grabber.PublishFrame(Jpeg(1), 5);                             // frame 1, count 1

        (int Count, byte Marker)? seen = null;
        using var readerDone = new ManualResetEventSlim();
        Task? reader = null;
        grabber.AfterSwapForTest = () =>
        {
            // Frame 2 has just been swapped in and the publish is not finished: read now, from another thread.
            reader = Task.Run(() =>
            {
                using var handle = grabber.TryAcquireLatest(out var count);
                seen = (count, handle!.Value.Data.Span[2]);
                readerDone.Set();
            });
            // A reader that must wait for the publish to finish cannot finish here; give it the time to show it does not.
            readerDone.Wait(TimeSpan.FromMilliseconds(500));
        };

        await Task.Run(() => grabber.PublishFrame(Jpeg(2), 5)).WaitAsync(TimeSpan.FromSeconds(10));
        await reader!.WaitAsync(TimeSpan.FromSeconds(10));

        seen.Should().Be((2, (byte)2), "frame 2 is read with count 2, the count it was published under");
        grabber.FrameCount.Should().Be(2);
    }

    /// <summary>S-14: the shared feed (<see cref="MjpegProvider"/>) reads a frame with its own count too, while a publish is in progress.</summary>
    [Fact]
    public async Task The_provider_reads_a_raw_frame_with_its_own_count_while_a_publish_is_in_progress()
    {
        using var grabber = new HttpFrameGrabber("127.0.0.1", 1);
        using var provider = new MjpegProvider(grabber, new JpegCodec());
        grabber.PublishFrame(Jpeg(1), 5);

        (int Count, byte Marker)? seen = null;
        using var readerDone = new ManualResetEventSlim();
        Task? reader = null;
        grabber.AfterSwapForTest = () =>
        {
            reader = Task.Run(() =>
            {
                using var handle = provider.TryAcquireRaw(out var count);
                seen = (count, handle!.Value.Data.Span[2]);
                readerDone.Set();
            });
            readerDone.Wait(TimeSpan.FromMilliseconds(500));
        };

        await Task.Run(() => grabber.PublishFrame(Jpeg(2), 5)).WaitAsync(TimeSpan.FromSeconds(10));
        await reader!.WaitAsync(TimeSpan.FromSeconds(10));

        seen.Should().Be((2, (byte)2), "frame 2 is read with count 2 through the provider");
    }

    /// <summary>S-14: the provider's decoded frame comes with the count of the frame it decoded.</summary>
    [Fact]
    public void The_provider_decodes_a_frame_with_its_own_count()
    {
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "rgb24-reference.jpg"));
        using var grabber = new HttpFrameGrabber("127.0.0.1", 1);
        using var provider = new MjpegProvider(grabber, new JpegCodec());
        provider.TryGetFrame(out var none).Should().BeNull();
        none.Should().Be(0);

        for (var i = 1; i <= 2; i++)
        {
            grabber.PublishFrame(new ReadOnlySequence<byte>(jpeg), jpeg.Length);
            var frame = provider.TryGetFrame(out var count);
            frame.Should().NotBeNull();
            frame!.Value.Dispose();
            count.Should().Be(i);
        }
    }

    [Fact]
    public void The_count_read_with_a_frame_is_the_count_it_was_published_under()
    {
        using var grabber = new HttpFrameGrabber("127.0.0.1", 1);
        grabber.TryAcquireLatest(out var none).Should().BeNull();
        none.Should().Be(0);

        for (byte i = 1; i <= 3; i++)
        {
            grabber.PublishFrame(Jpeg(i), 5);
            using var handle = grabber.TryAcquireLatest(out var count);
            (count, handle!.Value.Data.Span[2]).Should().Be((i, i));
        }
    }
}
