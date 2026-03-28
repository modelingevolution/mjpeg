using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace ModelingEvolution.Mjpeg.Wasm.Tests;

public class WasmJpegDecodePipelineTests : IDisposable
{
    private readonly List<SKBitmap> _bitmaps = new();
    private static readonly byte[] FakeJpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };

    private SKBitmap RentBitmap(int width = 16, int height = 16)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bitmaps.Add(bmp);
        return bmp;
    }

    public void Dispose()
    {
        foreach (var bmp in _bitmaps) bmp.Dispose();
    }

    [Fact]
    public async Task Constructor_CreatesPooledDecoders()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 3);
        fake.CreatedDecoders.Count.Should().Be(3);
    }

    [Fact]
    public async Task PushAndRead_SingleFrame_Succeeds()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 1);

        var bitmap = RentBitmap();
        await pipeline.PushAsync(new DecodeRequest(1, FakeJpeg, bitmap));

        using var cts = new CancellationTokenSource(5000);
        var result = await pipeline.ReadAsync(cts.Token);

        result.FrameId.Should().Be(1);
        result.Success.Should().BeTrue();
        result.Bitmap.Should().BeSameAs(bitmap);
    }

    [Fact]
    public async Task PushAndRead_MultipleFrames_MaintainsOrder()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 2);

        // BoundedCapacity = workerCount * 2 = 4, so push/read must be concurrent
        // to avoid deadlock when pushing more than capacity.
        const int frameCount = 8;
        using var cts = new CancellationTokenSource(10000);

        var producer = Task.Run(async () =>
        {
            for (int i = 0; i < frameCount; i++)
                await pipeline.PushAsync(new DecodeRequest((ulong)i, FakeJpeg, RentBitmap()), cts.Token);
        });

        for (ulong i = 0; i < frameCount; i++)
        {
            var result = await pipeline.ReadAsync(cts.Token);
            result.FrameId.Should().Be(i, $"frame {i} should come out in order");
        }

        await producer;
    }

    [Fact]
    public async Task Decode_Failure_ReturnsFalseSuccess()
    {
        var fake = new FakeNativeDecoder { ShouldFailDecode = true };
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 1);

        await pipeline.PushAsync(new DecodeRequest(1, FakeJpeg, RentBitmap()));

        using var cts = new CancellationTokenSource(5000);
        var result = await pipeline.ReadAsync(cts.Token);

        result.Success.Should().BeFalse();
        result.FrameId.Should().Be(1);
    }

    [Fact]
    public async Task Reset_AcceptsNewWork()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 1);

        await pipeline.PushAsync(new DecodeRequest(1, FakeJpeg, RentBitmap()));
        using var cts1 = new CancellationTokenSource(5000);
        await pipeline.ReadAsync(cts1.Token);

        pipeline.Reset();

        await pipeline.PushAsync(new DecodeRequest(2, FakeJpeg, RentBitmap()));
        using var cts2 = new CancellationTokenSource(5000);
        var result = await pipeline.ReadAsync(cts2.Token);

        result.FrameId.Should().Be(2);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Pool_DecoderReused_Sequential()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 1);

        using var cts = new CancellationTokenSource(10000);
        for (int i = 0; i < 5; i++)
        {
            await pipeline.PushAsync(new DecodeRequest((ulong)i, FakeJpeg, RentBitmap()));
            await pipeline.ReadAsync(cts.Token);
        }

        fake.CreatedDecoders.Count.Should().Be(1);
        fake.DecodeCalledWith.Count.Should().Be(5);
    }

    [Fact]
    public async Task Pool_PreCreatesWorkerCountDecoders()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 4);
        fake.CreatedDecoders.Count.Should().Be(4);
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllDecoders()
    {
        var fake = new FakeNativeDecoder();
        var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 3);

        await pipeline.PushAsync(new DecodeRequest(1, FakeJpeg, RentBitmap()));
        using var cts = new CancellationTokenSource(5000);
        await pipeline.ReadAsync(cts.Token);

        await pipeline.DisposeAsync();

        fake.ClosedDecoders.Count.Should().Be(3);
    }

    [Fact]
    public async Task TryRead_ReturnsFalse_WhenEmpty()
    {
        var fake = new FakeNativeDecoder();
        await using var pipeline = new WasmJpegDecodePipeline(fake, 1920, 1080, workerCount: 1);
        pipeline.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void RowBytes_NoPadding_ForBgra8888()
    {
        using var bitmap = new SKBitmap(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.RowBytes.Should().Be(1920 * 4);
    }

    [Fact]
    public void RowBytes_SmallBitmap_NoPadding()
    {
        using var bitmap = new SKBitmap(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.RowBytes.Should().Be(16 * 4);
    }
}
