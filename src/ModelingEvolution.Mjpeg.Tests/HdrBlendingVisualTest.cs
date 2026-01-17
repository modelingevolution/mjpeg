using System.Buffers;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// xUnit collection to run integration tests sequentially (Emgu.CV may not be thread-safe)
/// </summary>
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollection { }

/// <summary>
/// Visual tests for HDR blending that create meaningful test cases
/// demonstrating the difference between blending modes.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class HdrBlendingVisualTest
{
    private readonly ITestOutputHelper _output;
    private readonly string _outputDir;

    public HdrBlendingVisualTest(ITestOutputHelper output)
    {
        _output = output;
        _outputDir = Path.Combine(Path.GetTempPath(), "mjpeg_hdr_visual_test");
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Creates a test image simulating different exposure levels.
    /// Short exposure: dark areas black, bright areas preserved
    /// Long exposure: dark areas visible, bright areas clipped to white
    /// </summary>
    [Fact]
    public void HdrBlending_SimulatedExposures_ShouldShowWeightedDifference()
    {
        const int width = 200;
        const int height = 200;

        // Create a scene with gradient from dark to bright
        // Frame 0 (short exposure): dark areas are black (0-50), bright areas preserved (150-220)
        // Frame 1 (long exposure): dark areas visible (80-150), bright areas clipped (255)

        byte[] shortExposure = CreateExposureImage(width, height, isShortExposure: true);
        byte[] longExposure = CreateExposureImage(width, height, isShortExposure: false);

        // Save input frames
        SaveGrayscaleImage(shortExposure, width, height, Path.Combine(_outputDir, "short_exposure.jpg"));
        SaveGrayscaleImage(longExposure, width, height, Path.Combine(_outputDir, "long_exposure.jpg"));

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, EncodeToJpeg(shortExposure, width, height) },
            { 1, EncodeToJpeg(longExposure, width, height) }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        // Test Average blending
        using (var engine = new MjpegHdrEngine(GetImage, new JpegCodec(new JpegCodecOptions { MaxWidth = width, MaxHeight = height }), new HdrBlend(), MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Average;
            engine.HdrFrameWindowCount = 2;
            using var result = engine.Get(1);
            File.WriteAllBytes(Path.Combine(_outputDir, "hdr_average.jpg"), result.Data.ToArray());
        }

        // Test Linear weighted blending (dark prefers frame 0, bright prefers frame 1)
        using (var engine = new MjpegHdrEngine(GetImage, new JpegCodec(new JpegCodecOptions { MaxWidth = width, MaxHeight = height }), new HdrBlend(), MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Weighted;
            engine.HdrFrameWindowCount = 2;
            engine.Weights = HdrWeights.CreateLinear2Frame();
            using var result = engine.Get(1);
            File.WriteAllBytes(Path.Combine(_outputDir, "hdr_weighted_linear.jpg"), result.Data.ToArray());
        }

        // Test Inverse Linear weighted blending (dark prefers frame 1, bright prefers frame 0)
        using (var engine = new MjpegHdrEngine(GetImage, new JpegCodec(new JpegCodecOptions { MaxWidth = width, MaxHeight = height }), new HdrBlend(), MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Weighted;
            engine.HdrFrameWindowCount = 2;
            engine.Weights = HdrWeights.CreateInverseLinear2Frame();
            using var result = engine.Get(1);
            File.WriteAllBytes(Path.Combine(_outputDir, "hdr_weighted_inverse.jpg"), result.Data.ToArray());
        }

        _output.WriteLine($"Test images saved to: {_outputDir}");
        _output.WriteLine("short_exposure.jpg - Simulated short exposure (dark areas black, bright preserved)");
        _output.WriteLine("long_exposure.jpg - Simulated long exposure (dark areas visible, bright clipped)");
        _output.WriteLine("hdr_average.jpg - Simple average of both frames");
        _output.WriteLine("hdr_weighted_linear.jpg - Linear weighted (should recover both dark and bright details)");
        _output.WriteLine("hdr_weighted_inverse.jpg - Inverse weighted (opposite effect)");
    }

    /// <summary>
    /// Creates an exposure-simulated image with horizontal gradient and objects.
    /// </summary>
    private static byte[] CreateExposureImage(int width, int height, bool isShortExposure)
    {
        var data = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Base scene: horizontal gradient from dark (left) to bright (right)
                float sceneValue = (float)x / width; // 0.0 to 1.0

                // Add some objects: circles at different brightness levels
                float distToCircle1 = Distance(x, y, 50, 100);  // Dark object at left
                float distToCircle2 = Distance(x, y, 150, 100); // Bright object at right

                if (distToCircle1 < 30)
                    sceneValue = 0.15f; // Dark object
                else if (distToCircle2 < 30)
                    sceneValue = 0.85f; // Bright object

                // Apply exposure simulation
                byte pixelValue;
                if (isShortExposure)
                {
                    // Short exposure: dark areas become black, bright areas preserved
                    // Simulate with a curve that clips darks
                    float exposed = Math.Max(0, (sceneValue - 0.3f) * 1.5f);
                    exposed = Math.Min(1, exposed);
                    pixelValue = (byte)(exposed * 220); // Don't fully saturate
                }
                else
                {
                    // Long exposure: dark areas visible, bright areas clip to white
                    // Simulate with a curve that clips brights
                    float exposed = sceneValue * 2.0f;
                    exposed = Math.Min(1, exposed);
                    pixelValue = (byte)(exposed * 255);
                }

                data[y * width + x] = pixelValue;
            }
        }

        return data;
    }

    private static float Distance(int x1, int y1, int x2, int y2)
    {
        int dx = x1 - x2;
        int dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static void SaveGrayscaleImage(byte[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, DepthType.Cv8U, 1);
        Marshal.Copy(data, 0, mat.DataPointer, data.Length);
        CvInvoke.Imwrite(path, mat);
    }

    private static byte[] EncodeToJpeg(byte[] grayData, int width, int height)
    {
        // Convert grayscale to BGR (3 channels) for color JPEG encoding
        using var grayMat = new Mat(height, width, DepthType.Cv8U, 1);
        Marshal.Copy(grayData, 0, grayMat.DataPointer, grayData.Length);
        using var bgrMat = new Mat();
        CvInvoke.CvtColor(grayMat, bgrMat, ColorConversion.Gray2Bgr);
        using var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", bgrMat, buf, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 95));
        return buf.ToArray();
    }

    private static IMemoryOwner<byte> CreateImageMemoryOwner(byte[] jpegData)
    {
        var owner = MemoryPool<byte>.Shared.Rent(jpegData.Length);
        jpegData.CopyTo(owner.Memory.Span);
        return new SlicedMemoryOwner(owner, jpegData.Length);
    }

    private sealed class SlicedMemoryOwner : IMemoryOwner<byte>
    {
        private readonly IMemoryOwner<byte> _inner;
        private readonly int _length;
        public SlicedMemoryOwner(IMemoryOwner<byte> inner, int length) { _inner = inner; _length = length; }
        public Memory<byte> Memory => _inner.Memory.Slice(0, _length);
        public void Dispose() => _inner.Dispose();
    }
}
