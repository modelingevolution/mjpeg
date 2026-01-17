using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SkiaSharp;

namespace ModelingEvolution.Mjpeg.Benchmarks;

/// <summary>
/// Benchmarks comparing JPEG decode performance across different libraries:
/// - LibJpegWrap (libjpeg-turbo via native P/Invoke)
/// - Emgu.CV (OpenCV)
/// - SkiaSharp
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class JpegDecodeBenchmarks
{
    private byte[] _jpegData1024 = null!;
    private byte[] _jpegData512 = null!;
    private byte[] _outputBuffer1024 = null!;
    private byte[] _outputBuffer512 = null!;
    private JpegCodec _libJpegCodec = null!;

    [GlobalSetup]
    public void Setup()
    {
        _libJpegCodec = new JpegCodec(new JpegCodecOptions
        {
            MaxWidth = 1920,
            MaxHeight = 1080,
            Quality = 85
        });

        // Create test JPEG images using SkiaSharp (reliable cross-platform)
        _jpegData1024 = CreateTestJpeg(1024, 1024);
        _jpegData512 = CreateTestJpeg(512, 512);

        _outputBuffer1024 = new byte[1024 * 1024];
        _outputBuffer512 = new byte[512 * 512];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _libJpegCodec.Dispose();
    }

    private static byte[] CreateTestJpeg(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Gray8));
        var canvas = surface.Canvas;

        // Create gradient pattern
        for (int y = 0; y < height; y++)
        {
            using var paint = new SKPaint { Color = new SKColor((byte)(y * 255 / height), (byte)(y * 255 / height), (byte)(y * 255 / height)) };
            canvas.DrawLine(0, y, width, y, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    // === 1024x1024 Benchmarks ===

    [Benchmark(Baseline = true)]
    public FrameHeader LibJpegTurbo_Decode_1024()
    {
        return _libJpegCodec.Decode(_jpegData1024, _outputBuffer1024);
    }

    [Benchmark]
    public int EmguCV_Decode_1024()
    {
        using var mat = new Mat();
        CvInvoke.Imdecode(_jpegData1024, ImreadModes.Grayscale, mat);
        return mat.Width * mat.Height;
    }

    [Benchmark]
    public int SkiaSharp_Decode_1024()
    {
        using var bitmap = SKBitmap.Decode(_jpegData1024);
        return bitmap.Width * bitmap.Height;
    }

    // === 512x512 Benchmarks ===

    [Benchmark]
    public FrameHeader LibJpegTurbo_Decode_512()
    {
        return _libJpegCodec.Decode(_jpegData512, _outputBuffer512);
    }

    [Benchmark]
    public int EmguCV_Decode_512()
    {
        using var mat = new Mat();
        CvInvoke.Imdecode(_jpegData512, ImreadModes.Grayscale, mat);
        return mat.Width * mat.Height;
    }

    [Benchmark]
    public int SkiaSharp_Decode_512()
    {
        using var bitmap = SKBitmap.Decode(_jpegData512);
        return bitmap.Width * bitmap.Height;
    }
}
