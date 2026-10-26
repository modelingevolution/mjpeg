using FluentAssertions;
using Xunit;

namespace ModelingEvolution.Mjpeg.Tests;

/// <summary>
/// Rgb24 decode/encode through <see cref="JpegCodec"/>.
/// The reference pair in TestData was produced by libjpeg-turbo's own tools (2.1.2):
/// <c>cjpeg -quality 90</c> of a 48x32 synthetic image (gradients, a red/blue checker, a pattern),
/// and <c>djpeg -ppm</c> of that JPEG, stripped of its PPM header (rgb24-reference.rgb).
/// </summary>
public class JpegCodecRgb24Tests
{
    private const int RefWidth = 48;
    private const int RefHeight = 32;

    private static byte[] ReferenceJpeg => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "rgb24-reference.jpg"));
    private static byte[] ReferenceRgb => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "rgb24-reference.rgb"));

    [Fact]
    public void DecodeRgb24_ReferenceJpeg_MatchesDjpegOutputExactly()
    {
        using var codec = new JpegCodec();

        using var frame = codec.DecodeRgb24(ReferenceJpeg);

        frame.Header.Should().Be(new FrameHeader(RefWidth, RefHeight, RefWidth * 3, PixelFormat.Rgb24, RefWidth * RefHeight * 3));
        frame.Data.ToArray().Should().Equal(ReferenceRgb);
    }

    [Fact]
    public void DecodeRgb24_ToBuffer_FillsBufferAndReturnsHeader()
    {
        using var codec = new JpegCodec();
        var buffer = new byte[RefWidth * RefHeight * 3 + 100];

        var header = codec.DecodeRgb24(ReferenceJpeg, buffer);

        header.Should().Be(new FrameHeader(RefWidth, RefHeight, RefWidth * 3, PixelFormat.Rgb24, RefWidth * RefHeight * 3));
        buffer.AsSpan(0, header.Length).ToArray().Should().Equal(ReferenceRgb);
    }

    [Fact]
    public void DecodeRgb24_OutputBufferTooSmall_Throws()
    {
        using var codec = new JpegCodec();
        var buffer = new byte[RefWidth * RefHeight * 3 - 1];

        var act = () => codec.DecodeRgb24(ReferenceJpeg, buffer);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DecodeRgb24_CorruptJpeg_ThrowsAndCodecStaysUsable()
    {
        using var codec = new JpegCodec();

        // Garbage after SOI: libjpeg raises a fatal error; the native side recovers via longjmp.
        var act = () => codec.DecodeRgb24(new byte[] { 0xFF, 0xD8, 0x00, 0x01, 0x02 }, new byte[RefWidth * RefHeight * 3]);
        act.Should().Throw<InvalidOperationException>();

        using var frame = codec.DecodeRgb24(ReferenceJpeg);
        frame.Data.ToArray().Should().Equal(ReferenceRgb);
    }

    [Theory]
    [InlineData(JpegSubsampling.Ratio420, 90, 30.0)]
    [InlineData(JpegSubsampling.Ratio444, 92, 40.0)]
    public void EncodeRgb24_ThenDecode_RoundTripsAbovePsnrBound(JpegSubsampling subsampling, int quality, double minPsnr)
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage(checker: false);
        using var source = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);

        using var jpeg = codec.EncodeRgb24(source, quality, subsampling);
        using var decoded = codec.DecodeRgb24(jpeg.Data);

        jpeg.Data.Span[0].Should().Be(0xFF);
        jpeg.Data.Span[1].Should().Be(0xD8);
        decoded.Header.Width.Should().Be(w);
        decoded.Header.Height.Should().Be(h);
        var psnr = Psnr(rgb, decoded.Data.Span);
        Console.WriteLine($"PSNR {subsampling} q{quality}: {psnr:F2} dB");
        psnr.Should().BeGreaterThan(minPsnr);
    }

    [Fact]
    public void EncodeRgb24_Ratio444_KeepsSharpChromaEdgesBetterThanRatio420()
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        using var source = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);

        using var jpeg420 = codec.EncodeRgb24(source, 92, JpegSubsampling.Ratio420);
        using var jpeg444 = codec.EncodeRgb24(source, 92, JpegSubsampling.Ratio444);
        using var decoded420 = codec.DecodeRgb24(jpeg420.Data);
        using var decoded444 = codec.DecodeRgb24(jpeg444.Data);

        Psnr(rgb, decoded444.Data.Span).Should().BeGreaterThan(Psnr(rgb, decoded420.Data.Span));
    }

    [Fact]
    public void EncodeRgb24_PaddedStride_IgnoresPaddingBytes()
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        int stride = w * 3 + 16;
        var padded = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            rgb.AsSpan(y * w * 3, w * 3).CopyTo(padded.AsSpan(y * stride));
            padded.AsSpan(y * stride + w * 3, 16).Fill(0xAB);
        }
        using var tight = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);
        using var loose = new FrameImage(new FrameHeader(w, h, stride, PixelFormat.Rgb24, padded.Length), padded);

        using var a = codec.EncodeRgb24(tight, 90);
        using var b = codec.EncodeRgb24(loose, 90);

        b.Data.ToArray().Should().Equal(a.Data.ToArray());
    }

    [Fact]
    public void EncodeRgb24_ToBuffer_ReturnsBytesWritten()
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        using var source = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);
        var output = new byte[rgb.Length * 2];

        int written = codec.EncodeRgb24(source, 90, output);

        using var viaPool = codec.EncodeRgb24(source, 90);
        output.AsSpan(0, written).ToArray().Should().Equal(viaPool.Data.ToArray());
    }

    [Fact]
    public void EncodeRgb24_OutputBufferTooSmall_Throws()
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        using var source = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);

        var act = () => codec.EncodeRgb24(source, 90, new byte[64]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EncodeRgb24_NonRgb24Frame_ThrowsNotSupported()
    {
        using var codec = new JpegCodec();
        var gray = new byte[16 * 16];
        using var frame = new FrameImage(new FrameHeader(16, 16, 16, PixelFormat.Gray8, gray.Length), gray);

        var act = () => codec.EncodeRgb24(frame, 90);

        act.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void EncodeRgb24_QualityOutOfRange_Throws(int quality)
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        using var source = new FrameImage(new FrameHeader(w, h, w * 3, PixelFormat.Rgb24, rgb.Length), rgb);

        var act = () => codec.EncodeRgb24(source, quality);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Gray8AndI420Decode_OfReferenceJpeg_MatchOutputOfPreviousRelease()
    {
        // gray8-reference.gray / i420-reference.yuv: Decode / DecodeI420 of rgb24-reference.jpg on master add0195 (v1.4.1 natives).
        using var codec = new JpegCodec();

        using var gray = codec.Decode(ReferenceJpeg);
        using var i420 = codec.DecodeI420(ReferenceJpeg);

        gray.Header.Should().Be(new FrameHeader(RefWidth, RefHeight, RefWidth, PixelFormat.Gray8, RefWidth * RefHeight));
        gray.Data.ToArray().Should().Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "gray8-reference.gray")));
        i420.Header.Format.Should().Be(PixelFormat.I420);
        i420.Data.ToArray().Should().Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "i420-reference.yuv")));
    }

    [Fact]
    public void DecodeRgb24_Pooled_BadSofPrecision_ThrowsInsteadOfExitingProcess()
    {
        using var codec = new JpegCodec();
        var jpeg = ReferenceJpeg;
        int sof = IndexOfMarker(jpeg, 0xC0);
        jpeg[sof + 4] = 12; // SOF0 sample precision: baseline libjpeg-turbo supports 8 only -> fatal error in jpeg_read_header

        var act = () => codec.DecodeRgb24(jpeg);

        act.Should().Throw<InvalidOperationException>();
        using var frame = codec.DecodeRgb24(ReferenceJpeg);
        frame.Data.ToArray().Should().Equal(ReferenceRgb);
    }

    [Fact]
    public void EncodeRgb24_DataShorterThanHeader_ThrowsBeforeNativeCall()
    {
        using var codec = new JpegCodec();
        var (w, h, rgb) = SyntheticImage();
        // Header claims a padded stride the data does not have.
        using var frame = new FrameImage(new FrameHeader(w, h, w * 3 + 16, PixelFormat.Rgb24, rgb.Length), rgb);

        var pooled = () => codec.EncodeRgb24(frame, 90);
        var buffered = () => codec.EncodeRgb24(frame, 90, new byte[rgb.Length * 2]);

        pooled.Should().Throw<ArgumentException>();
        buffered.Should().Throw<ArgumentException>();
    }

    private static int IndexOfMarker(byte[] jpeg, byte marker)
    {
        for (int i = 0; i < jpeg.Length - 1; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
                return i;
        throw new InvalidOperationException($"Marker FF{marker:X2} not found.");
    }

    // 96x64: smooth gradients (left), a saturated red/blue checker with 4-px cells (middle, or a third gradient
    // when checker is false), a smooth pattern (right). The checker is the worst case for 4:2:0 chroma.
    private static (int Width, int Height, byte[] Rgb) SyntheticImage(bool checker = true)
    {
        const int w = 96, h = 64;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 3;
            (byte r, byte g, byte b) = x switch
            {
                < 32 => ((byte)(x * 8), (byte)(y * 4), (byte)128),
                < 64 when !checker => ((byte)(255 - x * 2), (byte)(x * 3), (byte)(y * 3)),
                < 64 => ((x / 4 + y / 4) % 2 == 0) ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255),
                _ => ((byte)(x * 2), (byte)(255 - y * 3), (byte)((x + y) * 2))
            };
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }
        return (w, h, rgb);
    }

    private static double Psnr(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            double d = expected[i] - actual[i];
            sum += d * d;
        }
        double mse = sum / expected.Length;
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
