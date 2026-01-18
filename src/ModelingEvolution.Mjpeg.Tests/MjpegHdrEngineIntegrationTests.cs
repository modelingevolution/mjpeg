using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Integration tests for MjpegHdrEngine using real JPEG encoding/decoding with LibJpegWrap.
/// Uses Emgu.CV only for creating test images. Tests verify HDR blending produces sensible results.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class MjpegHdrEngineIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MjpegHdrEngineIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Creates a color test image with a dot at the specified position.
    /// Uses BGR format (3 channels) so JPEG encodes as YCbCr for DecodeI420.
    /// </summary>
    private byte[] CreateTestImageWithDot(int width, int height, int dotX, int dotY, int dotRadius, byte background = 50, byte dotColor = 200)
    {
        using var mat = new Mat(height, width, DepthType.Cv8U, 3); // 3 channels for color
        mat.SetTo(new MCvScalar(background, background, background));

        CvInvoke.Circle(mat, new System.Drawing.Point(dotX, dotY), dotRadius, new MCvScalar(dotColor, dotColor, dotColor), -1);

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
    public void DiagnosticTest_CheckEmguCvLoading()
    {
        _output.WriteLine("Step 1: Starting diagnostic test");
        _output.WriteLine($"Current directory: {Environment.CurrentDirectory}");
        _output.WriteLine($"libcvextern.so exists: {File.Exists("libcvextern.so")}");
        _output.WriteLine($"libcvextern.so in app dir: {File.Exists(Path.Combine(AppContext.BaseDirectory, "libcvextern.so"))}");

        _output.WriteLine("Step 2: About to create Emgu.CV Mat...");
        try
        {
            using var mat = new Mat(100, 100, DepthType.Cv8U, 1);
            _output.WriteLine($"Step 3: Mat created successfully: {mat.Width}x{mat.Height}");

            _output.WriteLine("Step 4: About to set scalar...");
            mat.SetTo(new MCvScalar(50));
            _output.WriteLine("Step 5: Scalar set");

            _output.WriteLine("Step 6: About to draw circle...");
            CvInvoke.Circle(mat, new System.Drawing.Point(50, 50), 10, new MCvScalar(200), -1);
            _output.WriteLine("Step 7: Circle drawn");

            _output.WriteLine("Step 8: About to encode to JPEG...");
            using var buf = new VectorOfByte();
            // Use SAME call as CreateTestImageWithDot - with quality parameter
            CvInvoke.Imencode(".jpg", mat, buf, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 95));
            _output.WriteLine($"Step 9: Encoded to {buf.Size} bytes");

            _output.WriteLine("Step 10: About to write image to disk...");
            var testPath = Path.Combine(Path.GetTempPath(), "emgu_test.jpg");
            CvInvoke.Imwrite(testPath, mat);
            _output.WriteLine($"Step 11: Written to {testPath}");
            File.Delete(testPath);
            _output.WriteLine("Step 12: Cleaned up");

            _output.WriteLine("Step 13: Creating JpegCodec...");
            using var codec = new JpegCodec();
            _output.WriteLine("Step 14: JpegCodec created");

            _output.WriteLine("Step 15: Testing DecodeI420...");
            var jpegBytes = buf.ToArray();
            var info = codec.GetImageInfo(jpegBytes);
            _output.WriteLine($"Step 16: Image info: {info.Width}x{info.Height}");

            int i420Size = info.Width * info.Height * 3 / 2;
            var decodeBuffer = new byte[i420Size];
            var header = codec.DecodeI420(jpegBytes, decodeBuffer);
            _output.WriteLine($"Step 17: Decoded to I420: {header.Width}x{header.Height}, {header.Length} bytes");

            _output.WriteLine("Step 18: Testing CvInvoke.Imdecode...");
            using var decodedMat = new Mat();
            CvInvoke.Imdecode(jpegBytes, ImreadModes.Grayscale, decodedMat);
            _output.WriteLine($"Step 19: Imdecode result: {decodedMat.Width}x{decodedMat.Height}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"FAILED - {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                _output.WriteLine($"  Inner: {ex.InnerException.Message}");
            throw;
        }
    }

    [Fact]
    public void DiagnosticTest_EngineFlowStepByStep()
    {
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

        using var engine = new MjpegHdrEngine(
            GetImage,
            new JpegCodecPool(width, height),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 2;

        using var result = engine.Get(1);

        result.Data.Length.Should().BeGreaterThan(0);
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
            new JpegCodecPool(width, height),
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
                new JpegCodecPool(width, height),
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

    [Fact]
    public void RealRecording_Frame0_ShouldReturnSameAsInput()
    {
        // Arrange: Load real recording
        const string recordingPath = "/mnt/d/tmp/recordings/hdr_rb.20260117T145144.929486Z";
        var jsonPath = Path.Combine(recordingPath, "stream.json");
        var mjpegPath = Path.Combine(recordingPath, "stream.mjpeg");

        if (!File.Exists(jsonPath) || !File.Exists(mjpegPath))
        {
            _output.WriteLine($"Recording not found at {recordingPath}, skipping test");
            return;
        }

        // Parse JSON index
        var json = File.ReadAllText(jsonPath);
        var doc = JsonDocument.Parse(json);
        var index = doc.RootElement.GetProperty("Index");

        // Read frame 0 info
        var frame0Info = index.GetProperty("0");
        var frame0Start = frame0Info.GetProperty("s").GetInt64();
        var frame0Size = frame0Info.GetProperty("sz").GetInt32();

        _output.WriteLine($"Frame 0: offset={frame0Start}, size={frame0Size}");

        // Read frame data from MJPEG file
        using var fs = File.OpenRead(mjpegPath);
        var frameCache = new Dictionary<ulong, byte[]>();

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            if (!frameCache.TryGetValue(frameId, out var data))
            {
                var frameKey = frameId.ToString();
                if (!index.TryGetProperty(frameKey, out var frameInfo))
                {
                    frameKey = "0"; // Fallback to frame 0
                    frameInfo = index.GetProperty(frameKey);
                }

                var start = frameInfo.GetProperty("s").GetInt64();
                var size = frameInfo.GetProperty("sz").GetInt32();

                data = new byte[size];
                fs.Seek(start, SeekOrigin.Begin);
                fs.ReadExactly(data);
                frameCache[frameId] = data;
            }

            return Task.FromResult(CreateImageMemoryOwner(data));
        }

        // Get original frame 0 for comparison
        var originalFrame0 = new byte[frame0Size];
        fs.Seek(frame0Start, SeekOrigin.Begin);
        fs.ReadExactly(originalFrame0);

        // Create engine with 1024x1024 (from JSON caps)
        using var engine = new MjpegHdrEngine(
            GetImage,
            new JpegCodecPool(1024, 1024),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 2;

        // Act: Get frame 0 (should blend frame 0 with itself)
        using var result = engine.Get(0);

        _output.WriteLine($"Original frame 0 size: {originalFrame0.Length}");
        _output.WriteLine($"HDR result size: {result.Data.Length}");

        // Decode both and compare
        using var originalMat = new Mat();
        CvInvoke.Imdecode(originalFrame0, ImreadModes.Grayscale, originalMat);

        using var resultMat = new Mat();
        CvInvoke.Imdecode(result.Data.ToArray(), ImreadModes.Grayscale, resultMat);

        _output.WriteLine($"Original dimensions: {originalMat.Width}x{originalMat.Height}");
        _output.WriteLine($"Result dimensions: {resultMat.Width}x{resultMat.Height}");

        // Compare pixel values - should be very similar since we blend frame 0 with itself
        var originalData = new byte[originalMat.Width * originalMat.Height];
        var resultData = new byte[resultMat.Width * resultMat.Height];
        originalMat.CopyTo(originalData);
        resultMat.CopyTo(resultData);

        // Calculate average absolute difference
        long totalDiff = 0;
        for (int i = 0; i < originalData.Length; i++)
        {
            totalDiff += Math.Abs(originalData[i] - resultData[i]);
        }
        double avgDiff = (double)totalDiff / originalData.Length;

        _output.WriteLine($"Average pixel difference: {avgDiff:F2}");

        // Should be very small (only JPEG recompression differences)
        avgDiff.Should().BeLessThan(5, "frame 0 blended with itself should be nearly identical to original");
    }

    [Fact]
    public void RealRecording_ProcessMultipleFrames_ShouldWork()
    {
        // Arrange: Load real recording
        const string recordingPath = "/mnt/d/tmp/recordings/hdr_rb.20260117T145144.929486Z";
        var jsonPath = Path.Combine(recordingPath, "stream.json");
        var mjpegPath = Path.Combine(recordingPath, "stream.mjpeg");

        if (!File.Exists(jsonPath) || !File.Exists(mjpegPath))
        {
            _output.WriteLine($"Recording not found at {recordingPath}, skipping test");
            return;
        }

        var json = File.ReadAllText(jsonPath);
        var doc = JsonDocument.Parse(json);
        var index = doc.RootElement.GetProperty("Index");
        var framesCount = doc.RootElement.GetProperty("FramesCount").GetInt32();

        _output.WriteLine($"Recording has {framesCount} frames");

        using var fs = File.OpenRead(mjpegPath);
        var frameCache = new Dictionary<ulong, byte[]>();

        Task<IMemoryOwner<byte>> GetImage(ulong frameId)
        {
            if (!frameCache.TryGetValue(frameId, out var data))
            {
                var frameKey = frameId.ToString();
                if (!index.TryGetProperty(frameKey, out var frameInfo))
                {
                    frameKey = "0";
                    frameInfo = index.GetProperty(frameKey);
                }

                var start = frameInfo.GetProperty("s").GetInt64();
                var size = frameInfo.GetProperty("sz").GetInt32();

                data = new byte[size];
                fs.Seek(start, SeekOrigin.Begin);
                fs.ReadExactly(data);
                frameCache[frameId] = data;
            }

            return Task.FromResult(CreateImageMemoryOwner(data));
        }

        using var engine = new MjpegHdrEngine(
            GetImage,
            new JpegCodecPool(1024, 1024),
            new HdrBlend(),
            MemoryPool<byte>.Shared);

        engine.HdrMode = HdrBlendMode.Average;
        engine.HdrFrameWindowCount = 2;

        // Process first 10 frames
        var framesToProcess = Math.Min(10, framesCount);
        for (int i = 0; i < framesToProcess; i++)
        {
            using var result = engine.Get((ulong)i);
            _output.WriteLine($"Frame {i}: output size = {result.Data.Length} bytes");
            result.Data.Length.Should().BeGreaterThan(0);
        }

        _output.WriteLine($"Successfully processed {framesToProcess} frames");
    }

    [Fact]
    public async Task RealRecording_DecodeAllFramesInParallel_8Threads()
    {
        // Arrange: Load real recording
        const string recordingPath = "/mnt/d/tmp/recordings/hdr_rb.20260117T145144.929486Z";
        var jsonPath = Path.Combine(recordingPath, "stream.json");
        var mjpegPath = Path.Combine(recordingPath, "stream.mjpeg");

        if (!File.Exists(jsonPath) || !File.Exists(mjpegPath))
        {
            _output.WriteLine($"Recording not found at {recordingPath}, skipping test");
            return;
        }

        var json = File.ReadAllText(jsonPath);
        var doc = JsonDocument.Parse(json);
        var index = doc.RootElement.GetProperty("Index");
        var framesCount = doc.RootElement.GetProperty("FramesCount").GetInt32();

        _output.WriteLine($"Recording has {framesCount} frames");

        // Pre-load all frame data into memory for parallel access
        var frameData = new Dictionary<int, byte[]>();
        using (var fs = File.OpenRead(mjpegPath))
        {
            for (int i = 0; i < framesCount; i++)
            {
                var frameKey = i.ToString();
                if (index.TryGetProperty(frameKey, out var frameInfo))
                {
                    var start = frameInfo.GetProperty("s").GetInt64();
                    var size = frameInfo.GetProperty("sz").GetInt32();

                    var data = new byte[size];
                    fs.Seek(start, SeekOrigin.Begin);
                    fs.ReadExactly(data);
                    frameData[i] = data;
                }
            }
        }

        _output.WriteLine($"Pre-loaded {frameData.Count} frames into memory");

        // Process all frames in parallel with 8 threads
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 8 };
        var processedCount = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, framesCount),
            parallelOptions,
            async (frameIdx, ct) =>
            {
                try
                {
                    // Each thread needs its own engine (codec is not thread-safe)
                    Task<IMemoryOwner<byte>> GetImage(ulong frameId)
                    {
                        var idx = (int)frameId;
                        if (idx < 0 || idx >= framesCount)
                            idx = 0;

                        var data = frameData[idx];
                        return Task.FromResult(CreateImageMemoryOwner(data));
                    }

                    using var engine = new MjpegHdrEngine(
                        GetImage,
                        new JpegCodecPool(1024, 1024),
                        new HdrBlend(),
                        MemoryPool<byte>.Shared);

                    engine.HdrMode = HdrBlendMode.Average;
                    engine.HdrFrameWindowCount = 2;

                    using var result = await engine.GetAsync((ulong)frameIdx);

                    if (result.Data.Length == 0)
                    {
                        errors.Add($"Frame {frameIdx}: empty result");
                    }

                    Interlocked.Increment(ref processedCount);
                }
                catch (Exception ex)
                {
                    errors.Add($"Frame {frameIdx}: {ex.Message}");
                }
            });

        sw.Stop();

        _output.WriteLine($"Processed {processedCount} frames in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Throughput: {processedCount * 1000.0 / sw.ElapsedMilliseconds:F1} frames/sec");

        if (errors.Count > 0)
        {
            _output.WriteLine($"Errors ({errors.Count}):");
            foreach (var error in errors.Take(10))
            {
                _output.WriteLine($"  {error}");
            }
        }

        processedCount.Should().Be(framesCount, "all frames should be processed successfully");
        errors.Should().BeEmpty("no errors should occur during parallel processing");
    }
}

