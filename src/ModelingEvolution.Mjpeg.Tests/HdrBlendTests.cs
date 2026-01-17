using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Tests for HdrBlend matching GStreamer gsthdr plugin algorithms.
/// </summary>
public class HdrBlendTests
{
    private readonly HdrBlend _blend = new();

    #region Average 2-Frame Tests - matches gsthdr_average.cpp blend_average_fixed<2>

    [Fact]
    public void Average_TwoFrames_ShouldUseRoundingFormula()
    {
        // GStreamer formula: (pix0 + pix1 + 1) >> 1
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [100, 101, 200, 201];
        byte[] dataB = [100, 100, 200, 200];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[4];

        _blend.Average(frameA, frameB, output);

        // (100 + 100 + 1) >> 1 = 100
        output[0].Should().Be(100);
        // (101 + 100 + 1) >> 1 = 101 (rounds up)
        output[1].Should().Be(101);
        // (200 + 200 + 1) >> 1 = 200
        output[2].Should().Be(200);
        // (201 + 200 + 1) >> 1 = 201 (rounds up)
        output[3].Should().Be(201);
    }

    [Fact]
    public void Average_TwoFrames_WithZeroAndMax_ShouldRoundUp()
    {
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [0, 0, 0, 0];
        byte[] dataB = [255, 255, 255, 255];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[4];

        _blend.Average(frameA, frameB, output);

        // (0 + 255 + 1) >> 1 = 128 (rounds up from 127.5)
        output.Should().AllSatisfy(b => b.Should().Be(128));
    }

    [Fact]
    public void Average_TwoFrames_AllocatingOverload_ShouldWork()
    {
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [50, 50, 50, 50];
        byte[] dataB = [100, 100, 100, 100];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);

        using var result = _blend.Average(frameA, frameB);

        result.Header.Should().Be(header);
        result.OwnsMemory.Should().BeTrue();
        // (50 + 100 + 1) >> 1 = 75
        result.Data.ToArray().Should().AllSatisfy(b => b.Should().Be(75));
    }

    [Fact]
    public void Average_TwoFrames_DifferentDimensions_ShouldThrow()
    {
        var headerA = new FrameHeader(100, 100, 100, PixelFormat.Gray8, 10000);
        var headerB = new FrameHeader(200, 100, 200, PixelFormat.Gray8, 20000);
        var frameA = new FrameImage(headerA, new byte[10000]);
        var frameB = new FrameImage(headerB, new byte[20000]);
        var output = new byte[10000];

        var action = () => _blend.Average(frameA, frameB, output);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*dimensions*");
    }

    #endregion

    #region Average 3-Frame Tests - matches gsthdr_average.cpp blend_average_fixed<3>

    [Fact]
    public void Average_ThreeFrames_ShouldUseRoundingDivision()
    {
        // GStreamer formula: (sum + N/2) / N where N=3
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [30, 31, 32, 33];
        byte[] dataB = [60, 61, 62, 63];
        byte[] dataC = [90, 91, 92, 93];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var frameC = new FrameImage(header, dataC);
        var output = new byte[4];

        _blend.Average(frameA, frameB, frameC, output);

        // (30 + 60 + 90 + 1) / 3 = 60
        output[0].Should().Be(60);
        // (31 + 61 + 91 + 1) / 3 = 61
        output[1].Should().Be(61);
        // (32 + 62 + 92 + 1) / 3 = 62
        output[2].Should().Be(62);
        // (33 + 63 + 93 + 1) / 3 = 63
        output[3].Should().Be(63);
    }

    [Fact]
    public void Average_ThreeFrames_AllocatingOverload_ShouldWork()
    {
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [0, 0, 0, 0];
        byte[] dataB = [127, 127, 127, 127];
        byte[] dataC = [255, 255, 255, 255];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var frameC = new FrameImage(header, dataC);

        using var result = _blend.Average(frameA, frameB, frameC);

        result.Header.Should().Be(header);
        result.OwnsMemory.Should().BeTrue();
        // (0 + 127 + 255 + 1) / 3 = 127
        result.Data.ToArray().Should().AllSatisfy(b => b.Should().Be(127));
    }

    #endregion

    #region Weighted 2-Frame Tests - matches gsthdr_weighted.cpp blend_weighted_gray8_2f

    [Fact]
    public void Weighted_TwoFrames_EqualWeights_ShouldBlendEqually()
    {
        // GStreamer algorithm:
        // weightBase = (pix0 + pix1) & ~0x1
        // result = (pix0 * w0 + pix1 * w1) >> 8
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [100, 100, 100, 100];
        byte[] dataB = [200, 200, 200, 200];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[4];

        var weights = HdrWeights.CreateEqual2Frame();

        _blend.Weighted(frameA, frameB, weights, output);

        // weightBase = (100 + 200) & ~0x1 = 300 & ~1 = 300
        // But weights array is 512 elements (256*2), so index 300 is valid
        // For equal weights: w0=127, w1=128
        // result = (100*127 + 200*128) >> 8 = (12700 + 25600) >> 8 = 38300 >> 8 = 149
        output.Should().AllSatisfy(b => b.Should().BeInRange(148, 150));
    }

    [Fact]
    public void Weighted_TwoFrames_Linear_DarkPixelsPreferFrame0()
    {
        var header = new FrameHeader(1, 1, 1, PixelFormat.Gray8, 1);
        byte[] dataA = [50];  // Dark pixel in frame 0
        byte[] dataB = [50];  // Same dark pixel in frame 1
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[1];

        var weights = HdrWeights.CreateLinear2Frame();

        _blend.Weighted(frameA, frameB, weights, output);

        // weightBase = (50 + 50) & ~0x1 = 100
        // luminance 50: w0 = 255-50 = 205, w1 = 50
        // result = (50*205 + 50*50) >> 8 = (10250 + 2500) >> 8 = 12750 >> 8 = 49
        output[0].Should().BeInRange(49, 51);
    }

    [Fact]
    public void Weighted_TwoFrames_Linear_BrightPixelsPreferFrame1()
    {
        var header = new FrameHeader(1, 1, 1, PixelFormat.Gray8, 1);
        byte[] dataA = [200];  // Bright pixel in frame 0
        byte[] dataB = [200];  // Same bright pixel in frame 1
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[1];

        var weights = HdrWeights.CreateLinear2Frame();

        _blend.Weighted(frameA, frameB, weights, output);

        // weightBase = (200 + 200) & ~0x1 = 400
        // luminance 200: w0 = 255-200 = 55, w1 = 200
        // result = (200*55 + 200*200) >> 8 = (11000 + 40000) >> 8 = 51000 >> 8 = 199
        output[0].Should().BeInRange(198, 201);
    }

    [Fact]
    public void Weighted_TwoFrames_VerifyWeightBaseFormula()
    {
        // Test the exact GStreamer formula: weightBase = (pix0 + pix1) & ~0x1
        var header = new FrameHeader(1, 1, 1, PixelFormat.Gray8, 1);

        // Create custom weights where we can verify the lookup
        // Start with equal weights and modify specific indices
        var weightData = new byte[512];
        for (int lum = 0; lum < 256; lum++)
        {
            weightData[lum * 2] = 127;
            weightData[lum * 2 + 1] = 128;
        }
        // Set specific weights at index 200 (for pix0=100, pix1=100, weightBase=200)
        // w0 at index 200, w1 at index 201
        weightData[200] = 200;  // w0 = 200/255 ~ 0.78
        weightData[201] = 55;   // w1 = 55/255 ~ 0.22

        var weights = new HdrWeights(weightData, numFrames: 2, channels: 1);

        byte[] dataA = [100];
        byte[] dataB = [100];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[1];

        _blend.Weighted(frameA, frameB, weights, output);

        // result = (100*200 + 100*55) >> 8 = 25500 >> 8 = 99
        output[0].Should().Be(99);
    }

    [Fact]
    public void Weighted_TwoFrames_WrongNumFrames_ShouldThrow()
    {
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        var frameA = new FrameImage(header, new byte[4]);
        var frameB = new FrameImage(header, new byte[4]);
        var output = new byte[4];
        var weights = new HdrWeights(3); // Wrong! Should be 2

        var action = () => _blend.Weighted(frameA, frameB, weights, output);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*2 frames*");
    }

    #endregion

    #region Weighted 3-Frame Tests - matches gsthdr_weighted.cpp blend_weighted_gray8_nf

    [Fact]
    public void Weighted_ThreeFrames_EqualWeights_ShouldBlendEqually()
    {
        var header = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] dataA = [0, 0, 0, 0];
        byte[] dataB = [127, 127, 127, 127];
        byte[] dataC = [255, 255, 255, 255];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var frameC = new FrameImage(header, dataC);
        var output = new byte[4];

        var weights = HdrWeights.CreateEqual(3);

        _blend.Weighted(frameA, frameB, frameC, weights, output);

        // lum = (0 + 127 + 255) / 3 = 127
        // For equal 3-frame weights: w0=85, w1=85, w2=85 (255/3=85)
        // result = (0*85 + 127*85 + 255*85) >> 8 = (0 + 10795 + 21675) >> 8 = 32470 >> 8 = 126
        output.Should().AllSatisfy(b => b.Should().BeInRange(125, 128));
    }

    [Fact]
    public void Weighted_ThreeFrames_VerifyLuminanceLookup()
    {
        // Test that luminance = average of all frames
        var header = new FrameHeader(1, 1, 1, PixelFormat.Gray8, 1);

        // Pixels: 60, 90, 120 -> average = 90
        // Weight index for frame 0 at lum 90: 90*3+0 = 270
        // Weight index for frame 1 at lum 90: 90*3+1 = 271
        // Weight index for frame 2 at lum 90: 90*3+2 = 272

        // Create weight data with equal weights by default
        var weightData = new byte[256 * 3];
        for (int lum = 0; lum < 256; lum++)
        {
            weightData[lum * 3 + 0] = 85;
            weightData[lum * 3 + 1] = 85;
            weightData[lum * 3 + 2] = 85;
        }

        // Set custom weights at luminance 90 indices
        weightData[270] = 255;  // w0 = 1.0 (all weight to frame 0)
        weightData[271] = 0;    // w1 = 0
        weightData[272] = 0;    // w2 = 0

        var weights = new HdrWeights(weightData, numFrames: 3, channels: 1);

        byte[] dataA = [60];
        byte[] dataB = [90];
        byte[] dataC = [120];
        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var frameC = new FrameImage(header, dataC);
        var output = new byte[1];

        _blend.Weighted(frameA, frameB, frameC, weights, output);

        // result = (60*255 + 90*0 + 120*0) >> 8 = 15300 >> 8 = 59
        output[0].Should().BeInRange(59, 61);
    }

    #endregion

    #region GrayToRgb Tests - matches gsthdr_average.cpp blend_gray8_to_rgb

    [Fact]
    public void GrayToRgb_ShouldCombineChannels()
    {
        var grayHeader = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        byte[] redData = [255, 0, 128, 64];
        byte[] greenData = [0, 255, 128, 64];
        byte[] blueData = [0, 0, 128, 64];
        var frameR = new FrameImage(grayHeader, redData);
        var frameG = new FrameImage(grayHeader, greenData);
        var frameB = new FrameImage(grayHeader, blueData);
        var output = new byte[12]; // 4 pixels * 3 bytes

        var result = _blend.GrayToRgb(frameR, frameG, frameB, output);

        result.Width.Should().Be(2);
        result.Height.Should().Be(2);
        result.Format.Should().Be(PixelFormat.Rgb24);
        result.Length.Should().Be(12);

        // Matches GStreamer: dst[x*3+0]=R, dst[x*3+1]=G, dst[x*3+2]=B
        // First pixel: R=255, G=0, B=0 (red)
        output[0].Should().Be(255);
        output[1].Should().Be(0);
        output[2].Should().Be(0);

        // Second pixel: R=0, G=255, B=0 (green)
        output[3].Should().Be(0);
        output[4].Should().Be(255);
        output[5].Should().Be(0);

        // Third pixel: R=128, G=128, B=128 (gray)
        output[6].Should().Be(128);
        output[7].Should().Be(128);
        output[8].Should().Be(128);

        // Fourth pixel: R=64, G=64, B=64 (dark gray)
        output[9].Should().Be(64);
        output[10].Should().Be(64);
        output[11].Should().Be(64);
    }

    [Fact]
    public void GrayToRgb_NonGray8Format_ShouldThrow()
    {
        var rgbHeader = new FrameHeader(2, 2, 6, PixelFormat.Rgb24, 12);
        var frameR = new FrameImage(rgbHeader, new byte[12]);
        var grayHeader = new FrameHeader(2, 2, 2, PixelFormat.Gray8, 4);
        var frameG = new FrameImage(grayHeader, new byte[4]);
        var frameB = new FrameImage(grayHeader, new byte[4]);
        var output = new byte[12];

        var action = () => _blend.GrayToRgb(frameR, frameG, frameB, output);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Gray8*");
    }

    [Fact]
    public void GrayToRgb_OutputBufferTooSmall_ShouldThrow()
    {
        var header = new FrameHeader(10, 10, 10, PixelFormat.Gray8, 100);
        var frameR = new FrameImage(header, new byte[100]);
        var frameG = new FrameImage(header, new byte[100]);
        var frameB = new FrameImage(header, new byte[100]);
        var output = new byte[100]; // Need 300 for RGB

        var action = () => _blend.GrayToRgb(frameR, frameG, frameB, output);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*buffer too small*");
    }

    #endregion

    #region Large Frame Tests

    [Fact]
    public void Average_LargeFrame_ShouldWorkCorrectly()
    {
        const int width = 1920;
        const int height = 1080;
        const int length = width * height;
        var header = new FrameHeader(width, height, width, PixelFormat.Gray8, length);

        var dataA = new byte[length];
        var dataB = new byte[length];
        Array.Fill(dataA, (byte)100);
        Array.Fill(dataB, (byte)200);

        var frameA = new FrameImage(header, dataA);
        var frameB = new FrameImage(header, dataB);
        var output = new byte[length];

        _blend.Average(frameA, frameB, output);

        // (100 + 200 + 1) >> 1 = 150
        output.Should().AllSatisfy(b => b.Should().Be(150));
    }

    #endregion
}
