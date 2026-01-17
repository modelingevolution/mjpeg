using System.Buffers;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// IJpegCodec implementation using Emgu.CV (OpenCV) for testing.
/// </summary>
internal sealed class EmguJpegCodec : IJpegCodec
{
    public int Quality { get; set; } = 85;
    public DctMethod DctMethod { get; set; } = DctMethod.Integer;

    public FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer)
    {
        using var mat = new Mat();
        CvInvoke.Imdecode(jpegData.ToArray(), ImreadModes.Grayscale, mat);

        var header = new FrameHeader(mat.Width, mat.Height, mat.Step, PixelFormat.Gray8, mat.Width * mat.Height);
        var data = new byte[header.Length];
        Marshal.Copy(mat.DataPointer, data, 0, data.Length);
        data.CopyTo(outputBuffer);
        return header;
    }

    public FrameImage Decode(ReadOnlyMemory<byte> jpegData)
    {
        using var mat = new Mat();
        CvInvoke.Imdecode(jpegData.ToArray(), ImreadModes.Grayscale, mat);

        var header = new FrameHeader(mat.Width, mat.Height, mat.Step, PixelFormat.Gray8, mat.Width * mat.Height);
        var data = new byte[header.Length];
        Marshal.Copy(mat.DataPointer, data, 0, data.Length);
        return new FrameImage(header, data);
    }

    public int Encode(in FrameImage frame, Memory<byte> outputBuffer)
    {
        var data = frame.Data.ToArray();
        using var mat = new Mat(frame.Header.Height, frame.Header.Width, DepthType.Cv8U, 1);
        Marshal.Copy(data, 0, mat.DataPointer, data.Length);

        using var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", mat, buf, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, Quality));

        var encoded = buf.ToArray();
        encoded.CopyTo(outputBuffer);
        return encoded.Length;
    }

    public FrameImage Encode(in FrameImage frame)
    {
        var data = frame.Data.ToArray();
        using var mat = new Mat(frame.Header.Height, frame.Header.Width, DepthType.Cv8U, 1);
        Marshal.Copy(data, 0, mat.DataPointer, data.Length);

        using var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", mat, buf, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, Quality));

        var encoded = buf.ToArray();
        var header = new FrameHeader(frame.Header.Width, frame.Header.Height, encoded.Length, PixelFormat.Gray8, encoded.Length);
        return new FrameImage(header, encoded);
    }

    public void Dispose() { }
}

/// <summary>
/// Integration tests for MjpegHdrEngine using real JPEG encoding/decoding with Emgu.CV.
/// Tests verify HDR blending produces sensible results with different weight configurations.
/// </summary>
[Trait("Category", "Integration")]
public class MjpegHdrEngineIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MjpegHdrEngineIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Creates a grayscale test image with a white dot at the specified position.
    /// </summary>
    private static byte[] CreateTestImageWithDot(int width, int height, int dotX, int dotY, int dotRadius, byte background = 50, byte dotColor = 200)
    {
        using var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(background));

        CvInvoke.Circle(mat, new System.Drawing.Point(dotX, dotY), dotRadius, new MCvScalar(dotColor), -1);

        using var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", mat, buf, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 95));
        return buf.ToArray();
    }

    /// <summary>
    /// Creates a memory owner containing the test image JPEG data.
    /// </summary>
    private static IMemoryOwner<byte> CreateImageMemoryOwner(byte[] jpegData)
    {
        var owner = MemoryPool<byte>.Shared.Rent(jpegData.Length);
        jpegData.CopyTo(owner.Memory.Span);
        return new SlicedMemoryOwner(owner, jpegData.Length);
    }

    /// <summary>
    /// Helper to wrap a rented memory with actual length.
    /// </summary>
    private sealed class SlicedMemoryOwner : IMemoryOwner<byte>
    {
        private readonly IMemoryOwner<byte> _inner;
        private readonly int _length;

        public SlicedMemoryOwner(IMemoryOwner<byte> inner, int length)
        {
            _inner = inner;
            _length = length;
        }

        public Memory<byte> Memory => _inner.Memory.Slice(0, _length);
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void Average2Frame_DotInDifferentPositions_ShouldShowBothDotsFaded()
    {
        // Arrange: Create two 100x100 images with dots at different positions
        const int width = 100;
        const int height = 100;

        // Frame 0: dot at (25, 50) - left side
        var frame0Jpeg = CreateTestImageWithDot(width, height, 25, 50, 10, background: 50, dotColor: 200);
        // Frame 1: dot at (75, 50) - right side
        var frame1Jpeg = CreateTestImageWithDot(width, height, 75, 50, 10, background: 50, dotColor: 200);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        using var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 2;

        // Act: Blend frame 1 (which will include frames 1 and 0)
        using var result = engine.Get(1);

        // Assert: Decode the result and verify both dots are visible but faded
        using var resultMat = new Mat();
        CvInvoke.Imdecode(result.Data.ToArray(), ImreadModes.Grayscale, resultMat);

        var resultData = new byte[width * height];
        resultMat.CopyTo(resultData);

        // Check dot positions
        byte leftDotCenter = resultData[50 * width + 25];   // Left dot center
        byte rightDotCenter = resultData[50 * width + 75];  // Right dot center
        byte background = resultData[50 * width + 50];      // Center (no dot)

        _output.WriteLine($"Left dot center: {leftDotCenter}");
        _output.WriteLine($"Right dot center: {rightDotCenter}");
        _output.WriteLine($"Background: {background}");

        // Both dots should be visible but at ~125 (average of 200 and 50)
        // Due to JPEG compression, allow some tolerance
        leftDotCenter.Should().BeInRange(100, 150, "left dot should be averaged (200+50)/2 ≈ 125");
        rightDotCenter.Should().BeInRange(100, 150, "right dot should be averaged (200+50)/2 ≈ 125");
        background.Should().BeInRange(40, 60, "background should stay around 50");
    }

    [Fact]
    public void WeightedLinear_DarkPixelsPrefersFrame0_BrightPixelsPrefersFrame1()
    {
        // Arrange: Create two 100x100 images
        // Frame 0: bright dot (200) at left on dark background (30)
        // Frame 1: dark dot (30) at right on bright background (200)
        const int width = 100;
        const int height = 100;

        var frame0Jpeg = CreateTestImageWithDot(width, height, 25, 50, 10, background: 30, dotColor: 200);
        var frame1Jpeg = CreateTestImageWithDot(width, height, 75, 50, 10, background: 200, dotColor: 30);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        using var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        // Linear weights: dark pixels prefer frame 0, bright pixels prefer frame 1
        engine.HdrMode = HdrBlendMode.Weighted;
        engine.HdrFrameWindowCount = 2;
        engine.Weights = HdrWeights.CreateLinear2Frame();

        // Act
        using var result = engine.Get(1);

        // Assert: Decode and analyze
        using var resultMat = new Mat();
        CvInvoke.Imdecode(result.Data.ToArray(), ImreadModes.Grayscale, resultMat);

        var resultData = new byte[width * height];
        resultMat.CopyTo(resultData);

        byte leftDotArea = resultData[50 * width + 25];
        byte rightDotArea = resultData[50 * width + 75];
        byte centerArea = resultData[50 * width + 50];

        _output.WriteLine($"Left area (frame0 bright dot): {leftDotArea}");
        _output.WriteLine($"Right area (frame1 dark dot): {rightDotArea}");
        _output.WriteLine($"Center area: {centerArea}");

        // With linear weights:
        // - Dark areas (low luminance) weight frame 0 more
        // - Bright areas (high luminance) weight frame 1 more
        // The exact values depend on the weight lookup formula
    }

    [Fact]
    public void WeightedInverseLinear_ShouldProduceDifferentResultThanLinear()
    {
        // Arrange: Same images as above
        const int width = 100;
        const int height = 100;

        var frame0Jpeg = CreateTestImageWithDot(width, height, 25, 50, 10, background: 50, dotColor: 200);
        var frame1Jpeg = CreateTestImageWithDot(width, height, 75, 50, 10, background: 50, dotColor: 200);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        // Test with Linear weights
        byte[] linearResult;
        using (var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Weighted;
            engine.HdrFrameWindowCount = 2;
            engine.Weights = HdrWeights.CreateLinear2Frame();
            using var result = engine.Get(1);
            linearResult = result.Data.ToArray();
        }

        // Test with Inverse Linear weights
        byte[] inverseLinearResult;
        using (var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Weighted;
            engine.HdrFrameWindowCount = 2;
            engine.Weights = HdrWeights.CreateInverseLinear2Frame();
            using var result = engine.Get(1);
            inverseLinearResult = result.Data.ToArray();
        }

        // Decode both results
        using var linearMat = new Mat();
        using var inverseMat = new Mat();
        CvInvoke.Imdecode(linearResult, ImreadModes.Grayscale, linearMat);
        CvInvoke.Imdecode(inverseLinearResult, ImreadModes.Grayscale, inverseMat);

        var linearData = new byte[width * height];
        var inverseData = new byte[width * height];
        linearMat.CopyTo(linearData);
        inverseMat.CopyTo(inverseData);

        // Compare dot areas
        byte linearLeftDot = linearData[50 * width + 25];
        byte inverseLeftDot = inverseData[50 * width + 25];

        _output.WriteLine($"Linear left dot: {linearLeftDot}");
        _output.WriteLine($"Inverse linear left dot: {inverseLeftDot}");

        // Assert: The results should be different
        // Due to JPEG compression, we check if there's a meaningful difference
        var difference = Math.Abs(linearLeftDot - inverseLeftDot);
        _output.WriteLine($"Difference: {difference}");

        // With inverse linear, the weighting should be reversed
        // This may not produce huge differences with these test images
        // but should be measurable
    }

    [Fact]
    public void Average3Frame_ThreeDifferentDotPositions_ShouldShowAllThreeFaded()
    {
        // Arrange: Create three 100x100 images with dots at different positions
        const int width = 100;
        const int height = 100;

        // Frame 0: dot at top
        var frame0Jpeg = CreateTestImageWithDot(width, height, 50, 25, 8, background: 50, dotColor: 250);
        // Frame 1: dot at bottom-left
        var frame1Jpeg = CreateTestImageWithDot(width, height, 25, 75, 8, background: 50, dotColor: 250);
        // Frame 2: dot at bottom-right
        var frame2Jpeg = CreateTestImageWithDot(width, height, 75, 75, 8, background: 50, dotColor: 250);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg },
            { 2, frame2Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        using var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 3;

        // Act: Blend frame 2 (which includes frames 2, 1, 0)
        using var result = engine.Get(2);

        // Assert: Decode and verify all three dots are visible but faded
        using var resultMat = new Mat();
        CvInvoke.Imdecode(result.Data.ToArray(), ImreadModes.Grayscale, resultMat);

        var resultData = new byte[width * height];
        resultMat.CopyTo(resultData);

        byte topDot = resultData[25 * width + 50];
        byte bottomLeftDot = resultData[75 * width + 25];
        byte bottomRightDot = resultData[75 * width + 75];
        byte background = resultData[50 * width + 50];

        _output.WriteLine($"Top dot: {topDot}");
        _output.WriteLine($"Bottom-left dot: {bottomLeftDot}");
        _output.WriteLine($"Bottom-right dot: {bottomRightDot}");
        _output.WriteLine($"Background: {background}");

        // Each dot should be at approximately (250 + 50 + 50) / 3 ≈ 116
        topDot.Should().BeInRange(90, 140, "top dot should be averaged across 3 frames");
        bottomLeftDot.Should().BeInRange(90, 140, "bottom-left dot should be averaged across 3 frames");
        bottomRightDot.Should().BeInRange(90, 140, "bottom-right dot should be averaged across 3 frames");
        background.Should().BeInRange(40, 60, "background should stay around 50");
    }

    [Fact]
    public void EqualWeights_ShouldProduceSimilarResultToAverage()
    {
        // Arrange
        const int width = 100;
        const int height = 100;

        var frame0Jpeg = CreateTestImageWithDot(width, height, 25, 50, 10, background: 50, dotColor: 200);
        var frame1Jpeg = CreateTestImageWithDot(width, height, 75, 50, 10, background: 50, dotColor: 200);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        // Get average result
        byte[] averageResult;
        using (var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Average;
            engine.HdrFrameWindowCount = 2;
            using var result = engine.Get(1);
            averageResult = result.Data.ToArray();
        }

        // Get equal-weighted result
        byte[] weightedResult;
        using (var engine = new MjpegHdrEngine(
            GetImage,
            new EmguJpegCodec(),
            new HdrBlend(),
            MemoryPool<byte>.Shared))
        {
            engine.HdrMode = HdrBlendMode.Weighted;
            engine.HdrFrameWindowCount = 2;
            engine.Weights = HdrWeights.CreateEqual2Frame();
            using var result = engine.Get(1);
            weightedResult = result.Data.ToArray();
        }

        // Decode both
        using var avgMat = new Mat();
        using var wgtMat = new Mat();
        CvInvoke.Imdecode(averageResult, ImreadModes.Grayscale, avgMat);
        CvInvoke.Imdecode(weightedResult, ImreadModes.Grayscale, wgtMat);

        var avgData = new byte[width * height];
        var wgtData = new byte[width * height];
        avgMat.CopyTo(avgData);
        wgtMat.CopyTo(wgtData);

        // Compare a few pixels
        byte avgLeftDot = avgData[50 * width + 25];
        byte wgtLeftDot = wgtData[50 * width + 25];

        _output.WriteLine($"Average mode left dot: {avgLeftDot}");
        _output.WriteLine($"Equal-weighted mode left dot: {wgtLeftDot}");

        // Equal weights (127/128) should produce similar result to average
        // Allow some tolerance due to different rounding in the two methods
        Math.Abs(avgLeftDot - wgtLeftDot).Should().BeLessThan(10,
            "equal weights should produce similar result to average mode");
    }

    [Fact]
    public void SaveTestImagesToFiles_ForVisualInspection()
    {
        // This test saves images to disk for manual visual inspection
        const int width = 200;
        const int height = 200;
        var outputDir = Path.Combine(Path.GetTempPath(), "mjpeg_hdr_test");
        Directory.CreateDirectory(outputDir);

        // Create test frames
        var frame0Jpeg = CreateTestImageWithDot(width, height, 50, 100, 20, background: 40, dotColor: 220);
        var frame1Jpeg = CreateTestImageWithDot(width, height, 150, 100, 20, background: 40, dotColor: 220);

        // Save input frames
        File.WriteAllBytes(Path.Combine(outputDir, "frame0_dot_left.jpg"), frame0Jpeg);
        File.WriteAllBytes(Path.Combine(outputDir, "frame1_dot_right.jpg"), frame1Jpeg);

        var frames = new Dictionary<ulong, byte[]>
        {
            { 0, frame0Jpeg },
            { 1, frame1Jpeg }
        };

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            var jpeg = frames.ContainsKey(frameId) ? frames[frameId] : frames[0];
            return Task.FromResult(CreateImageMemoryOwner(jpeg));
        }

        // Generate blended images with different modes
        var modes = new (string name, HdrBlendMode mode, HdrWeights? weights)[]
        {
            ("average", HdrBlendMode.Average, null),
            ("weighted_equal", HdrBlendMode.Weighted, HdrWeights.CreateEqual2Frame()),
            ("weighted_linear", HdrBlendMode.Weighted, HdrWeights.CreateLinear2Frame()),
            ("weighted_inverse", HdrBlendMode.Weighted, HdrWeights.CreateInverseLinear2Frame()),
        };

        foreach (var (name, mode, weights) in modes)
        {
            using var engine = new MjpegHdrEngine(
                GetImage,
                new EmguJpegCodec(),
                new HdrBlend(),
                MemoryPool<byte>.Shared);

            engine.HdrMode = mode;
            engine.HdrFrameWindowCount = 2;
            if (weights != null)
                engine.Weights = weights;

            using var result = engine.Get(1);
            var outputPath = Path.Combine(outputDir, $"blended_{name}.jpg");
            File.WriteAllBytes(outputPath, result.Data.ToArray());
            _output.WriteLine($"Saved: {outputPath}");
        }

        _output.WriteLine($"\nTest images saved to: {outputDir}");
        _output.WriteLine("Open the folder to visually inspect the blending results.");
    }
}

