using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Tests for HdrWeights matching GStreamer gsthdr_weighted.cpp behavior.
/// </summary>
public class HdrWeightsTests
{
    [Fact]
    public void HdrWeights_2Frame_ShouldHave512Elements()
    {
        var weights = new HdrWeights(2);

        weights.Size.Should().Be(512); // 256 luminance * 2 frames
        weights.NumFrames.Should().Be(2);
        weights.Channels.Should().Be(1);
    }

    [Fact]
    public void HdrWeights_3Frame_ShouldHave768Elements()
    {
        var weights = new HdrWeights(3);

        weights.Size.Should().Be(768); // 256 luminance * 3 frames
        weights.NumFrames.Should().Be(3);
    }

    [Fact]
    public void HdrWeights_Default_ShouldHaveEqualWeights()
    {
        // Matches GStreamer init_default_weights_double_buffer()
        var weights = new HdrWeights(2);

        // For 2 frames: each should get 127 and 128 (255/2 = 127, remainder = 128)
        for (int lum = 0; lum < 256; lum++)
        {
            byte w0 = weights.GetWeight(lum, 0);
            byte w1 = weights.GetWeight(lum, 1);

            // Weights should sum to 255
            (w0 + w1).Should().Be(255, $"weights at luminance {lum} should sum to 255");

            // First frame gets 255/2 = 127, second gets remainder = 128
            w0.Should().Be(127);
            w1.Should().Be(128);
        }
    }

    [Fact]
    public void HdrWeights_3Frame_Default_ShouldSumTo255()
    {
        var weights = new HdrWeights(3);

        for (int lum = 0; lum < 256; lum++)
        {
            int sum = weights.GetWeight(lum, 0) + weights.GetWeight(lum, 1) + weights.GetWeight(lum, 2);
            sum.Should().Be(255, $"weights at luminance {lum} should sum to 255");
        }
    }

    [Fact]
    public void HdrWeights_Linear2Frame_ShouldIncreaseW1()
    {
        var weights = HdrWeights.CreateLinear2Frame();

        // At luminance 0: w0=255, w1=0 (dark pixels prefer frame 0)
        weights.GetWeight(0, 0).Should().Be(255);
        weights.GetWeight(0, 1).Should().Be(0);

        // At luminance 255: w0=0, w1=255 (bright pixels prefer frame 1)
        weights.GetWeight(255, 0).Should().Be(0);
        weights.GetWeight(255, 1).Should().Be(255);

        // At luminance 128: approximately equal
        weights.GetWeight(128, 0).Should().Be(127);
        weights.GetWeight(128, 1).Should().Be(128);
    }

    [Fact]
    public void HdrWeights_InverseLinear2Frame_ShouldDecreaseW1()
    {
        var weights = HdrWeights.CreateInverseLinear2Frame();

        // At luminance 0: w0=0, w1=255 (dark pixels prefer frame 1)
        weights.GetWeight(0, 0).Should().Be(0);
        weights.GetWeight(0, 1).Should().Be(255);

        // At luminance 255: w0=255, w1=0 (bright pixels prefer frame 0)
        weights.GetWeight(255, 0).Should().Be(255);
        weights.GetWeight(255, 1).Should().Be(0);
    }

    [Fact]
    public void HdrWeights_AllLuminances_ShouldSumTo255()
    {
        var weights = HdrWeights.CreateLinear2Frame();

        for (int lum = 0; lum < 256; lum++)
        {
            int sum = weights.GetWeight(lum, 0) + weights.GetWeight(lum, 1);
            sum.Should().Be(255, $"weights at luminance {lum} should sum to 255");
        }
    }

    [Fact]
    public void HdrWeights_WeightIndex_MatchesGStreamerLayout()
    {
        // GStreamer layout: weights[luminance * num_frames + frame_idx]
        var weights = new HdrWeights(2);

        // Luminance 0, frame 0 -> index 0
        weights.GetWeightIndex(0, 0).Should().Be(0);

        // Luminance 0, frame 1 -> index 1
        weights.GetWeightIndex(0, 1).Should().Be(1);

        // Luminance 1, frame 0 -> index 2
        weights.GetWeightIndex(1, 0).Should().Be(2);

        // Luminance 1, frame 1 -> index 3
        weights.GetWeightIndex(1, 1).Should().Be(3);

        // Luminance 255, frame 0 -> index 510
        weights.GetWeightIndex(255, 0).Should().Be(510);

        // Luminance 255, frame 1 -> index 511
        weights.GetWeightIndex(255, 1).Should().Be(511);
    }

    [Fact]
    public void HdrWeights_2Frame_WeightBaseIndex_MatchesGStreamer()
    {
        // GStreamer 2-frame uses: weight_base = (pix0 + pix1) & ~0x1
        // This gives even indices 0,2,4...510

        var weights = HdrWeights.CreateLinear2Frame();

        // Simulate GStreamer weight lookup for pix0=100, pix1=100
        int pix0 = 100, pix1 = 100;
        int weightBase = (pix0 + pix1) & ~0x1; // = 200

        byte w0 = weights.Weights[weightBase];
        byte w1 = weights.Weights[weightBase + 1];

        // luminance 100 in linear: w0 = 255-100=155, w1 = 100
        w0.Should().Be(155);
        w1.Should().Be(100);
        (w0 + w1).Should().Be(255);
    }

    [Fact]
    public void HdrWeights_Constructor_ShouldThrowOnInvalidFrameCount()
    {
        var action1 = () => new HdrWeights(1);
        action1.Should().Throw<ArgumentOutOfRangeException>();

        var action11 = () => new HdrWeights(11);
        action11.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HdrWeights_Constructor_ShouldThrowOnInvalidChannels()
    {
        var action = () => new HdrWeights(2, channels: 2);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HdrWeights_ShouldSerializeToJson()
    {
        var weights = HdrWeights.CreateLinear2Frame();

        var json = JsonSerializer.Serialize(weights, HdrWeightsJsonContext.Default.HdrWeights);

        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"Weights\"");
        json.Should().Contain("\"NumFrames\":2");
        json.Should().Contain("\"Channels\":1");
    }

    [Fact]
    public void HdrWeights_ShouldDeserializeFromJson()
    {
        var original = HdrWeights.CreateLinear2Frame();
        var json = JsonSerializer.Serialize(original, HdrWeightsJsonContext.Default.HdrWeights);

        var deserialized = JsonSerializer.Deserialize(json, HdrWeightsJsonContext.Default.HdrWeights);

        deserialized.Should().NotBeNull();
        deserialized!.NumFrames.Should().Be(2);
        deserialized.Channels.Should().Be(1);
        deserialized.Size.Should().Be(512);

        // Verify weights match
        for (int i = 0; i < 512; i++)
        {
            deserialized.Weights[i].Should().Be(original.Weights[i]);
        }
    }

    [Fact]
    public void HdrWeights_RgbChannels_ShouldHaveCorrectSize()
    {
        var weights = new HdrWeights(2, channels: 3);

        weights.Size.Should().Be(3 * 256 * 2); // 1536
        weights.Channels.Should().Be(3);
    }
}
