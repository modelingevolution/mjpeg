using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

public class MjpegFrameScannerTests
{
    [Fact]
    public void Scan_SingleFrame_ReturnsOneFrameInfo()
    {
        byte[] data = [0xFF, 0xD8, 0x00, 0x01, 0x02, 0xFF, 0xD9];

        var frames = MjpegFrameScanner.Scan(data);

        frames.Should().HaveCount(1);
        frames[0].StartOffset.Should().Be(0);
        frames[0].Size.Should().Be(7);
        frames[0].FrameIndex.Should().Be(0);
    }

    [Fact]
    public void Scan_MultipleFrames_ReturnsAllFrameInfo()
    {
        byte[] data =
        [
            // Frame 0: 6 bytes
            0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9,
            // Frame 1: 8 bytes
            0xFF, 0xD8, 0x01, 0x02, 0x03, 0x04, 0xFF, 0xD9
        ];

        var frames = MjpegFrameScanner.Scan(data);

        frames.Should().HaveCount(2);

        frames[0].StartOffset.Should().Be(0);
        frames[0].Size.Should().Be(6);
        frames[0].FrameIndex.Should().Be(0);

        frames[1].StartOffset.Should().Be(6);
        frames[1].Size.Should().Be(8);
        frames[1].FrameIndex.Should().Be(1);
    }

    [Fact]
    public void Scan_NoFrames_ReturnsEmptyList()
    {
        byte[] data = [0x00, 0x01, 0x02, 0x03];

        var frames = MjpegFrameScanner.Scan(data);

        frames.Should().BeEmpty();
    }

    [Fact]
    public void Scan_IncompleteFrame_ReturnsEmptyList()
    {
        byte[] data = [0xFF, 0xD8, 0x00, 0x01]; // Missing EOI

        var frames = MjpegFrameScanner.Scan(data);

        frames.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanAsync_SingleFrame_ReturnsOneFrameInfo()
    {
        byte[] data = [0xFF, 0xD8, 0x00, 0x01, 0x02, 0xFF, 0xD9];
        using var stream = new MemoryStream(data);

        var frames = new List<FrameInfo>();
        await foreach (var frame in MjpegFrameScanner.ScanAsync(stream))
        {
            frames.Add(frame);
        }

        frames.Should().HaveCount(1);
        frames[0].StartOffset.Should().Be(0);
        frames[0].Size.Should().Be(7);
    }

    [Fact]
    public async Task ScanAsync_SupportsCancellation()
    {
        byte[] data =
        [
            0xFF, 0xD8, 0x00, 0xFF, 0xD9,
            0xFF, 0xD8, 0x01, 0xFF, 0xD9
        ];
        using var stream = new MemoryStream(data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in MjpegFrameScanner.ScanAsync(stream, cancellationToken: cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
