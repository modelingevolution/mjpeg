using System.Buffers;
using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Mock IJpegCodec for testing without native library.
/// </summary>
internal sealed class MockJpegCodec : IJpegCodec
{
    public int Quality { get; set; } = 85;
    public DctMethod DctMethod { get; set; } = DctMethod.Integer;

    public int DecodeCallCount { get; private set; }
    public int EncodeCallCount { get; private set; }
    public int GetImageInfoCallCount { get; private set; }

    public FrameHeader GetImageInfo(ReadOnlyMemory<byte> jpegData)
    {
        GetImageInfoCallCount++;
        // Return 2x2 Gray8 frame info
        return new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
    }

    public FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        DecodeCallCount++;
        // Return a simple 2x2 Gray8 frame
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        // Fill with test pattern
        var span = outputBuffer.Span;
        for (int i = 0; i < Math.Min(4, span.Length); i++)
        {
            span[i] = (byte)(100 + i);
        }
        return header;
    }

    public FrameImage Decode(ReadOnlyMemory<byte> jpegData)
    {
        DecodeCallCount++;
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        var data = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            data[i] = (byte)(100 + i);
        }
        return new FrameImage(header, data);
    }

    public FrameHeader DecodeI420(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        DecodeCallCount++;
        // Return a simple 2x2 I420 frame (Y=4 bytes, U=1 byte, V=1 byte)
        var header = new FrameHeader(2, 2, 2, PixelFormat.I420, 6);
        var span = outputBuffer.Span;
        // Y plane
        for (int i = 0; i < 4; i++)
            span[i] = (byte)(100 + i);
        // U plane
        span[4] = 128;
        // V plane
        span[5] = 128;
        return header;
    }

    public FrameImage DecodeI420(ReadOnlyMemory<byte> jpegData)
    {
        DecodeCallCount++;
        var header = new FrameHeader(2, 2, 2, PixelFormat.I420, 6);
        var data = new byte[6];
        // Y plane
        for (int i = 0; i < 4; i++)
            data[i] = (byte)(100 + i);
        // U plane
        data[4] = 128;
        // V plane
        data[5] = 128;
        return new FrameImage(header, data);
    }

    public int Encode(in FrameImage frame, Memory<byte> outputBuffer)
    {
        EncodeCallCount++;
        // Write dummy JPEG data
        var span = outputBuffer.Span;
        span[0] = 0xFF;
        span[1] = 0xD8; // JPEG SOI marker
        return 2;
    }

    public FrameImage Encode(in FrameImage frame)
    {
        EncodeCallCount++;
        var header = new FrameHeader(frame.Header.Width, frame.Header.Height, 2, PixelFormat.Gray8, 2);
        var data = new byte[] { 0xFF, 0xD8 }; // JPEG SOI marker
        return new FrameImage(header, data);
    }

    public void Dispose() { }
}

/// <summary>
/// Tests for MjpegHdrEngine configuration and validation.
/// </summary>
public class MjpegHdrEngineTests
{
    private static Task<IMemoryOwner<byte>> DummyGetImage(ulong frameId)
    {
        var owner = MemoryPool<byte>.Shared.Rent(100);
        return Task.FromResult(owner);
    }

    private static MjpegHdrEngine CreateEngine(Func<ulong, Task<IMemoryOwner<byte>>>? getImage = null)
    {
        return new MjpegHdrEngine(
            getImage ?? DummyGetImage,
            new MockJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared);
    }

    [Fact]
    public void Constructor_ShouldAcceptValidDelegate()
    {
        using var engine = CreateEngine();

        engine.HdrFrameWindowCount.Should().Be(2);
        engine.HdrMode.Should().Be(HdrBlendMode.Average);
    }

    [Fact]
    public void Constructor_NullDelegate_ShouldThrow()
    {
        var action = () => new MjpegHdrEngine(
            null!,
            new MockJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HdrFrameWindowCount_ShouldBeSettable()
    {
        using var engine = CreateEngine();

        engine.HdrFrameWindowCount = 3;

        engine.HdrFrameWindowCount.Should().Be(3);
    }

    [Fact]
    public void HdrMode_ShouldBeSettable()
    {
        using var engine = CreateEngine();

        engine.HdrMode = HdrBlendMode.Weighted;

        engine.HdrMode.Should().Be(HdrBlendMode.Weighted);
    }

    [Fact]
    public void Weights_ShouldBeSettable()
    {
        using var engine = CreateEngine();
        var weights = HdrWeights.CreateLinear2Frame();

        engine.Weights = weights;

        engine.Weights.Should().BeSameAs(weights);
    }

    [Fact]
    public void Get_InvalidWindowCount_ShouldThrow()
    {
        using var engine = CreateEngine();
        engine.HdrFrameWindowCount = 1; // Invalid

        var action = () => engine.Get(0);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*between 2 and 10*");
    }

    [Fact]
    public void Get_WindowCountTooLarge_ShouldThrow()
    {
        using var engine = CreateEngine();
        engine.HdrFrameWindowCount = 11; // Invalid

        var action = () => engine.Get(0);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*between 2 and 10*");
    }

    [Fact]
    public void Get_WeightedModeWithoutWeights_ShouldThrow()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.Weighted;
        engine.Weights = null;

        var action = () => engine.Get(0);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Weights must be set*");
    }

    [Fact]
    public void Get_WeightedModeWithMismatchedWeights_ShouldThrow()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.Weighted;
        engine.HdrFrameWindowCount = 3;
        engine.Weights = new HdrWeights(2); // Mismatch: 2 frames weights for 3 frame window

        var action = () => engine.Get(0);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*NumFrames*must match*");
    }

    [Fact]
    public void Get_GrayToRgbModeWithWrongWindowCount_ShouldThrow()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.GrayToRgb;
        engine.HdrFrameWindowCount = 2; // Should be 3 for GrayToRgb

        var action = () => engine.Get(0);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*GrayToRgb*requires exactly 3 frames*");
    }

    [Fact]
    public void Get_ValidAverageConfig_ShouldReturnResult()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 2;

        using var result = engine.Get(0);

        result.Data.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Get_ValidWeightedConfig_ShouldReturnResult()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.Weighted;
        engine.HdrFrameWindowCount = 2;
        engine.Weights = HdrWeights.CreateLinear2Frame();

        using var result = engine.Get(0);

        result.Data.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Get_Valid3FrameAverage_ShouldReturnResult()
    {
        using var engine = CreateEngine();
        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 3;

        using var result = engine.Get(5);

        result.Data.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var engine = CreateEngine();

        var action = () => engine.Dispose();

        action.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        var engine = CreateEngine();

        engine.Dispose();
        var action = () => engine.Dispose();

        action.Should().NotThrow();
    }

    [Fact]
    public void Get_AfterDispose_ShouldThrow()
    {
        var engine = CreateEngine();
        engine.Dispose();

        var action = () => engine.Get(0);

        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetAsync_ShouldFetchCorrectFrameIds()
    {
        var fetchedIds = new List<ulong>();

        Task<IMemoryOwner<byte>> TrackingGetImage(ulong frameId)
        {
            fetchedIds.Add(frameId);
            var owner = MemoryPool<byte>.Shared.Rent(100);
            return Task.FromResult(owner);
        }

        using var engine = CreateEngine(TrackingGetImage);
        engine.HdrFrameWindowCount = 3;

        using var result = await engine.GetAsync(10);

        // Should fetch frames 10, 9, 8 (current and 2 previous)
        fetchedIds.Should().HaveCount(3);
        fetchedIds.Should().Contain(10UL);
        fetchedIds.Should().Contain(9UL);
        fetchedIds.Should().Contain(8UL);
    }

    [Fact]
    public async Task GetAsync_FrameIdZero_ShouldHandleEdgeCase()
    {
        var fetchedIds = new List<ulong>();

        Task<IMemoryOwner<byte>> TrackingGetImage(ulong frameId)
        {
            fetchedIds.Add(frameId);
            var owner = MemoryPool<byte>.Shared.Rent(100);
            return Task.FromResult(owner);
        }

        using var engine = CreateEngine(TrackingGetImage);
        engine.HdrFrameWindowCount = 3;

        using var result = await engine.GetAsync(0);

        // For frameId=0 with window=3: should request 0, 0, 0 (clamped to 0)
        fetchedIds.Should().HaveCount(3);
        fetchedIds.Should().AllSatisfy(id => id.Should().Be(0));
    }

    [Fact]
    public async Task GetAsync_FrameIdOne_ShouldHandleEdgeCase()
    {
        var fetchedIds = new List<ulong>();

        Task<IMemoryOwner<byte>> TrackingGetImage(ulong frameId)
        {
            fetchedIds.Add(frameId);
            var owner = MemoryPool<byte>.Shared.Rent(100);
            return Task.FromResult(owner);
        }

        using var engine = CreateEngine(TrackingGetImage);
        engine.HdrFrameWindowCount = 3;

        using var result = await engine.GetAsync(1);

        // For frameId=1 with window=3: should request 1, 0, 0 (clamped)
        fetchedIds.Should().HaveCount(3);
        fetchedIds.Should().Contain(1UL);
        fetchedIds.Count(id => id == 0UL).Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_ShouldCallDecodeForEachFrame()
    {
        var mockCodec = new MockJpegCodec();
        using var engine = new MjpegHdrEngine(
            DummyGetImage,
            mockCodec,
            new HdrBlend(),
            MemoryPool<byte>.Shared);
        engine.HdrFrameWindowCount = 3;

        using var result = await engine.GetAsync(10);

        mockCodec.DecodeCallCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_ShouldCallEncodeOnce()
    {
        var mockCodec = new MockJpegCodec();
        using var engine = new MjpegHdrEngine(
            DummyGetImage,
            mockCodec,
            new HdrBlend(),
            MemoryPool<byte>.Shared);
        engine.HdrFrameWindowCount = 2;

        using var result = await engine.GetAsync(10);

        mockCodec.EncodeCallCount.Should().Be(1);
    }
}
