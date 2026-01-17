using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace ModelingEvolution.Mjpeg.Benchmarks;

/// <summary>
/// Performance benchmarks for HDR blending algorithms with Full HD (1920x1080) images.
/// Tests Average and Weighted blending for Gray8, RGB24, and YUY2 pixel formats.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HdrBlendBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;

    // Frame sizes for different formats
    private const int Gray8Size = Width * Height;           // 2,073,600 bytes
    private const int Rgb24Size = Width * Height * 3;       // 6,220,800 bytes
    private const int Yuy2Size = Width * Height * 2;        // 4,147,200 bytes

    private HdrBlend _blend = null!;
    private HdrWeights _weights2Frame = null!;
    private HdrWeights _weights3Frame = null!;

    // Gray8 frames
    private FrameImage _gray8Frame0 = default;
    private FrameImage _gray8Frame1 = default;
    private FrameImage _gray8Frame2 = default;
    private byte[] _gray8Output = null!;

    // RGB24 frames
    private FrameImage _rgb24Frame0 = default;
    private FrameImage _rgb24Frame1 = default;
    private FrameImage _rgb24Frame2 = default;
    private byte[] _rgb24Output = null!;

    // YUY2 frames
    private FrameImage _yuy2Frame0 = default;
    private FrameImage _yuy2Frame1 = default;
    private FrameImage _yuy2Frame2 = default;
    private byte[] _yuy2Output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _blend = new HdrBlend(MemoryPool<byte>.Shared);
        _weights2Frame = HdrWeights.CreateLinear2Frame();
        _weights3Frame = new HdrWeights(3);

        // Initialize Gray8 frames with random data
        var random = new Random(42);
        _gray8Frame0 = CreateRandomFrame(Width, Height, Gray8Size, PixelFormat.Gray8, random);
        _gray8Frame1 = CreateRandomFrame(Width, Height, Gray8Size, PixelFormat.Gray8, random);
        _gray8Frame2 = CreateRandomFrame(Width, Height, Gray8Size, PixelFormat.Gray8, random);
        _gray8Output = new byte[Gray8Size];

        // Initialize RGB24 frames
        _rgb24Frame0 = CreateRandomFrame(Width, Height, Rgb24Size, PixelFormat.Rgb24, random);
        _rgb24Frame1 = CreateRandomFrame(Width, Height, Rgb24Size, PixelFormat.Rgb24, random);
        _rgb24Frame2 = CreateRandomFrame(Width, Height, Rgb24Size, PixelFormat.Rgb24, random);
        _rgb24Output = new byte[Rgb24Size];

        // Initialize YUY2 frames
        _yuy2Frame0 = CreateRandomFrame(Width, Height, Yuy2Size, PixelFormat.Yuy2, random);
        _yuy2Frame1 = CreateRandomFrame(Width, Height, Yuy2Size, PixelFormat.Yuy2, random);
        _yuy2Frame2 = CreateRandomFrame(Width, Height, Yuy2Size, PixelFormat.Yuy2, random);
        _yuy2Output = new byte[Yuy2Size];
    }

    private static FrameImage CreateRandomFrame(int width, int height, int size, PixelFormat format, Random random)
    {
        var data = new byte[size];
        random.NextBytes(data);
        var header = new FrameHeader(width, height, width * format.GetBytesPerPixel(), format, size);
        return new FrameImage(header, data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _gray8Frame0.Dispose();
        _gray8Frame1.Dispose();
        _gray8Frame2.Dispose();
        _rgb24Frame0.Dispose();
        _rgb24Frame1.Dispose();
        _rgb24Frame2.Dispose();
        _yuy2Frame0.Dispose();
        _yuy2Frame1.Dispose();
        _yuy2Frame2.Dispose();
    }

    #region Gray8 Benchmarks (1920x1080 = 2,073,600 bytes)

    [Benchmark(Description = "Gray8 Average 2-frame")]
    public void Gray8_Average_2Frame()
    {
        _blend.Average(_gray8Frame0, _gray8Frame1, _gray8Output);
    }

    [Benchmark(Description = "Gray8 Average 3-frame")]
    public void Gray8_Average_3Frame()
    {
        _blend.Average(_gray8Frame0, _gray8Frame1, _gray8Frame2, _gray8Output);
    }

    [Benchmark(Description = "Gray8 Weighted 2-frame")]
    public void Gray8_Weighted_2Frame()
    {
        _blend.Weighted(_gray8Frame0, _gray8Frame1, _weights2Frame, _gray8Output);
    }

    [Benchmark(Description = "Gray8 Weighted 3-frame")]
    public void Gray8_Weighted_3Frame()
    {
        _blend.Weighted(_gray8Frame0, _gray8Frame1, _gray8Frame2, _weights3Frame, _gray8Output);
    }

    #endregion

    #region RGB24 Benchmarks (1920x1080x3 = 6,220,800 bytes)

    [Benchmark(Description = "RGB24 Average 2-frame")]
    public void Rgb24_Average_2Frame()
    {
        _blend.Average(_rgb24Frame0, _rgb24Frame1, _rgb24Output);
    }

    [Benchmark(Description = "RGB24 Average 3-frame")]
    public void Rgb24_Average_3Frame()
    {
        _blend.Average(_rgb24Frame0, _rgb24Frame1, _rgb24Frame2, _rgb24Output);
    }

    [Benchmark(Description = "RGB24 Weighted 2-frame")]
    public void Rgb24_Weighted_2Frame()
    {
        _blend.Weighted(_rgb24Frame0, _rgb24Frame1, _weights2Frame, _rgb24Output);
    }

    [Benchmark(Description = "RGB24 Weighted 3-frame")]
    public void Rgb24_Weighted_3Frame()
    {
        _blend.Weighted(_rgb24Frame0, _rgb24Frame1, _rgb24Frame2, _weights3Frame, _rgb24Output);
    }

    #endregion

    #region YUY2 Benchmarks (1920x1080x2 = 4,147,200 bytes)

    [Benchmark(Description = "YUY2 Average 2-frame")]
    public void Yuy2_Average_2Frame()
    {
        _blend.Average(_yuy2Frame0, _yuy2Frame1, _yuy2Output);
    }

    [Benchmark(Description = "YUY2 Average 3-frame")]
    public void Yuy2_Average_3Frame()
    {
        _blend.Average(_yuy2Frame0, _yuy2Frame1, _yuy2Frame2, _yuy2Output);
    }

    [Benchmark(Description = "YUY2 Weighted 2-frame")]
    public void Yuy2_Weighted_2Frame()
    {
        _blend.Weighted(_yuy2Frame0, _yuy2Frame1, _weights2Frame, _yuy2Output);
    }

    [Benchmark(Description = "YUY2 Weighted 3-frame")]
    public void Yuy2_Weighted_3Frame()
    {
        _blend.Weighted(_yuy2Frame0, _yuy2Frame1, _yuy2Frame2, _weights3Frame, _yuy2Output);
    }

    #endregion
}

/// <summary>
/// Throughput benchmarks showing MB/s processing rate.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HdrBlendThroughputBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Gray8Size = Width * Height;

    private HdrBlend _blend = null!;
    private HdrWeights _weights2Frame = null!;
    private FrameImage _frame0 = default;
    private FrameImage _frame1 = default;
    private byte[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _blend = new HdrBlend();
        _weights2Frame = HdrWeights.CreateLinear2Frame();

        var random = new Random(42);
        var data0 = new byte[Gray8Size];
        var data1 = new byte[Gray8Size];
        random.NextBytes(data0);
        random.NextBytes(data1);

        var header = new FrameHeader(Width, Height, Width, PixelFormat.Gray8, Gray8Size);
        _frame0 = new FrameImage(header, data0);
        _frame1 = new FrameImage(header, data1);
        _output = new byte[Gray8Size];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _frame0.Dispose();
        _frame1.Dispose();
    }

    [Benchmark(Description = "Gray8 1080p Average (2.07 MB)")]
    public void Gray8_1080p_Average()
    {
        _blend.Average(_frame0, _frame1, _output);
    }

    [Benchmark(Description = "Gray8 1080p Weighted (2.07 MB)")]
    public void Gray8_1080p_Weighted()
    {
        _blend.Weighted(_frame0, _frame1, _weights2Frame, _output);
    }
}
