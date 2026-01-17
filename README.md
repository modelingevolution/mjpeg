# ModelingEvolution.Mjpeg

A .NET library for MJPEG stream processing, JPEG encoding/decoding, and HDR frame blending algorithms.

## Installation

```bash
dotnet add package ModelingEvolution.Mjpeg
```

## Features

- **JPEG Boundary Detection** - State machine-based detection of JPEG frame boundaries (SOI/EOI markers)
- **Dimension Extraction** - Extract width/height from JPEG SOF0/SOF2 markers
- **Bidirectional Scanning** - Forward and reverse MJPEG decoders for flexible stream processing
- **Frame Validation** - Validate JPEG frame integrity
- **JPEG Encode/Decode** - High-performance JPEG encoding and decoding with pooled memory support
- **HDR Blending** - Average and weighted HDR frame blending algorithms (2-3 frames)

---

## Core Types

### PixelFormat

Supported pixel formats for raw frame data. Values 0-11 are aligned with libjpeg-turbo's `TJPF` enum for zero-cost interop. Values 128+ are extended formats requiring conversion.

```csharp
/// <summary>
/// Pixel format enumeration aligned with libjpeg-turbo TJPF for direct interop.
/// OpenCV equivalent: BGR24 = CV_8UC3 (default), RGB24 = CV_8UC3 after COLOR_BGR2RGB.
/// </summary>
public enum PixelFormat : byte
{
    // === libjpeg-turbo compatible formats (TJPF values) ===
    Rgb24   = 0,   // TJPF_RGB  - 3 bytes/pixel: R, G, B order
    Bgr24   = 1,   // TJPF_BGR  - 3 bytes/pixel: B, G, R order (OpenCV default)
    Rgbx32  = 2,   // TJPF_RGBX - 4 bytes/pixel: R, G, B, X (X ignored)
    Bgrx32  = 3,   // TJPF_BGRX - 4 bytes/pixel: B, G, R, X (X ignored)
    Xbgr32  = 4,   // TJPF_XBGR - 4 bytes/pixel: X, B, G, R
    Xrgb32  = 5,   // TJPF_XRGB - 4 bytes/pixel: X, R, G, B
    Gray8   = 6,   // TJPF_GRAY - 1 byte/pixel: luminance (OpenCV CV_8UC1)
    Rgba32  = 7,   // TJPF_RGBA - 4 bytes/pixel: R, G, B, A
    Bgra32  = 8,   // TJPF_BGRA - 4 bytes/pixel: B, G, R, A (OpenCV CV_8UC4)
    Abgr32  = 9,   // TJPF_ABGR - 4 bytes/pixel: A, B, G, R
    Argb32  = 10,  // TJPF_ARGB - 4 bytes/pixel: A, R, G, B
    Cmyk32  = 11,  // TJPF_CMYK - 4 bytes/pixel: C, M, Y, K

    // === Extended YUV formats (require conversion for JPEG) ===
    Yuy2    = 128, // YUY2/YUYV packed: 4 bytes per 2 pixels (Y0 U Y1 V)
    Uyvy    = 129, // UYVY packed: 4 bytes per 2 pixels (U Y0 V Y1)
    Nv12    = 130, // NV12 planar: Y plane + interleaved UV plane (12 bits/pixel)
    Nv21    = 131, // NV21 planar: Y plane + interleaved VU plane (12 bits/pixel)
    I420    = 132, // I420/YV12 planar: Y + U + V planes (12 bits/pixel)
}
```

**OpenCV Compatibility:**
| PixelFormat | OpenCV Type | Notes |
|-------------|-------------|-------|
| `Bgr24` | `CV_8UC3` | OpenCV's default color format |
| `Rgb24` | `CV_8UC3` | After `COLOR_BGR2RGB` conversion |
| `Gray8` | `CV_8UC1` | Single-channel grayscale |
| `Bgra32` | `CV_8UC4` | 4-channel with alpha |

### FrameHeader

Describes the layout of raw frame data. JSON serializable for configuration and metadata persistence.

```csharp
/// <summary>
/// Frame metadata. JSON serializable, unit-tested.
/// </summary>
[JsonSerializable]
public readonly record struct FrameHeader(
    int Width,
    int Height,
    int Stride,
    PixelFormat Format,
    int Length);  // Total byte length of frame data
```

**Properties:**
- `Width` - Frame width in pixels
- `Height` - Frame height in pixels
- `Stride` - Bytes per row (may include padding for alignment)
- `Format` - Pixel format of the frame data
- `Length` - Total bytes in the frame data buffer

**Example:**
```csharp
// 1920x1080 RGB24 with no padding
var header = new FrameHeader(
    Width: 1920,
    Height: 1080,
    Stride: 1920 * 3,  // 5760 bytes per row
    Format: PixelFormat.Rgb24,
    Length: 1920 * 1080 * 3);  // 6,220,800 bytes
```

### FrameImage

Container for raw frame data with multiple memory ownership models:

```csharp
public readonly struct FrameImage : IDisposable
{
    public FrameHeader Header { get; }

    // Memory backing (mutually exclusive - only one is set)
    public ReadOnlyMemory<byte> Data { get; }           // Borrowed memory
    public IMemoryOwner<byte>? Owner { get; }           // Pooled memory (caller transfers ownership)
    public unsafe byte* Pointer { get; }                // Unmanaged pointer

    public bool OwnershipTransferred { get; }           // True if Owner was provided
}
```

**Construction patterns:**

```csharp
// Pattern 1: Borrowed memory (no ownership transfer)
var frame = new FrameImage(header, existingBuffer.AsMemory());

// Pattern 2: Pooled memory (ownership transferred to FrameImage)
var owner = MemoryPool<byte>.Shared.Rent(header.Length);
var frame = new FrameImage(header, owner);  // FrameImage now owns the memory

// Pattern 3: Unmanaged pointer (caller manages lifetime)
unsafe
{
    byte* ptr = (byte*)NativeMemory.Alloc((nuint)header.Length);
    var frame = new FrameImage(header, ptr);
    // ... use frame ...
    NativeMemory.Free(ptr);
}
```

---

## JPEG Encode/Decode API

### Interface

```csharp
public interface IJpegCodec : IDisposable
{
    /// <summary>
    /// Decodes JPEG data to raw pixel data.
    /// </summary>
    FrameHeader Decode(ReadOnlyMemory<byte> jpegData, Memory<byte> outputBuffer);

    /// <summary>
    /// Decodes JPEG data, allocating output buffer.
    /// </summary>
    FrameImage Decode(ReadOnlyMemory<byte> jpegData);

    /// <summary>
    /// Encodes raw frame to JPEG. Returns bytes written.
    /// </summary>
    int Encode(in FrameImage frame, Memory<byte> outputBuffer);

    /// <summary>
    /// Encodes raw frame to JPEG, allocating output buffer.
    /// </summary>
    FrameImage Encode(in FrameImage frame);

    /// <summary>
    /// Default JPEG quality (1-100).
    /// </summary>
    int Quality { get; set; }

    /// <summary>
    /// DCT algorithm: Integer (slower, accurate) or Float (faster).
    /// </summary>
    DctMethod DctMethod { get; set; }
}

public enum DctMethod : byte
{
    Integer = 0,  // JDCT_ISLOW - accurate
    Float = 1     // JDCT_FASTEST - faster
}
```

### Implementation

```csharp
public sealed class JpegCodec : IJpegCodec
{
    private readonly nint _encoderPtr;
    private readonly nint _decoderPtr;

    public JpegCodec(int maxWidth = 1920, int maxHeight = 1080, int quality = 85) { }

    public int Quality { get; set; }
    public DctMethod DctMethod { get; set; }

    // ... implementation
}
```

### Usage

```csharp
// Via DI
public class VideoProcessor(IJpegCodec codec)
{
    public void ProcessFrame(ReadOnlyMemory<byte> jpegData)
    {
        byte[] buffer = new byte[1920 * 1080 * 3];
        var header = codec.Decode(jpegData, buffer);
        // ... process raw pixels
    }
}

// Direct instantiation
using var codec = new JpegCodec(quality: 90);
codec.DctMethod = DctMethod.Float;  // Faster encoding

var header = new FrameHeader(1920, 1080, 1920 * 3, PixelFormat.Rgb24, 1920 * 1080 * 3);
var frame = new FrameImage(header, rawPixelData);

byte[] jpegBuffer = new byte[header.Length];
int jpegLength = codec.Encode(frame, jpegBuffer);
```

---

## HDR Blending API

HDR (High Dynamic Range) blending combines multiple frames captured at different exposure levels to produce a single frame with enhanced dynamic range.

### HDR Configuration Types

Aligned with GStreamer `gsthdr` plugin for interoperability:

```csharp
/// <summary>
/// HDR blending algorithm to use.
/// </summary>
public enum HdrBlendMode : byte
{
    Average,    // Equal weight for all frames
    Weighted,   // Per-luminance weighted blending using curves
    GrayToRgb,  // Map 3 Gray8 frames to RGB channels
}

/// <summary>
/// Configuration for weighted HDR blending.
/// Weights are indexed by luminance (0-255) and determine blend factor.
/// JSON serializable for configuration persistence.
/// </summary>
public readonly record struct HdrWeightCurve(float[] Weights)  // 256 floats, values 0.0-1.0
{
    public static HdrWeightCurve Linear { get; }     // 0→0, 255→1
    public static HdrWeightCurve Equal { get; }      // All 0.5
}

/// <summary>
/// RGB-aware weight configuration with separate curves per channel.
/// </summary>
public readonly record struct HdrRgbWeightCurves(
    HdrWeightCurve Red,
    HdrWeightCurve Green,
    HdrWeightCurve Blue);
```

### Interface

```csharp
public interface IHdrBlend
{
    /// <summary>
    /// Blends 2 frames using simple averaging.
    /// </summary>
    FrameHeader Average(in FrameImage frameA, in FrameImage frameB, Memory<byte> output);

    /// <summary>
    /// Blends 3 frames using simple averaging.
    /// </summary>
    FrameHeader Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC,
        Memory<byte> output);

    /// <summary>
    /// Blends 2 frames, allocating output buffer.
    /// </summary>
    FrameImage Average(in FrameImage frameA, in FrameImage frameB);

    /// <summary>
    /// Blends 3 frames, allocating output buffer.
    /// </summary>
    FrameImage Average(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC);

    /// <summary>
    /// Blends 2 frames using luminance-weighted curve.
    /// </summary>
    FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB,
        in HdrWeightCurve curve, Memory<byte> output);

    /// <summary>
    /// Blends 3 frames using luminance-weighted curve.
    /// </summary>
    FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB, in FrameImage frameC,
        in HdrWeightCurve curve, Memory<byte> output);

    /// <summary>
    /// Blends 2 frames using RGB-aware weighted curves.
    /// </summary>
    FrameHeader Weighted(in FrameImage frameA, in FrameImage frameB,
        in HdrRgbWeightCurves curves, Memory<byte> output);

    /// <summary>
    /// Combines 3 Gray8 frames into RGB24 (R=frameA, G=frameB, B=frameC).
    /// </summary>
    FrameHeader GrayToRgb(in FrameImage frameR, in FrameImage frameG, in FrameImage frameB,
        Memory<byte> output);
}
```

### Implementation

```csharp
public sealed class HdrBlend : IHdrBlend
{
    // Uses SIMD-optimized blending internally
}
```

### Usage

```csharp
// Via DI
public class HdrProcessor(IHdrBlend hdr, IJpegCodec codec)
{
    public FrameImage ProcessHdr(ReadOnlyMemory<byte> jpeg1, ReadOnlyMemory<byte> jpeg2)
    {
        using var frame1 = codec.Decode(jpeg1);
        using var frame2 = codec.Decode(jpeg2);
        return hdr.Average(frame1, frame2);
    }
}

// Weight curve semantics:
// - Index 0-255 = luminance level
// - Value 0.0 = use only frame A
// - Value 1.0 = use only frame B
// - Value 0.5 = equal blend

// Create custom weight curve
float[] weights = new float[256];
for (int i = 0; i < 256; i++)
    weights[i] = i / 255f;  // Dark→frameA, Bright→frameB

var curve = new HdrWeightCurve(weights);
byte[] output = new byte[frame1.Header.Length];
var header = hdr.Weighted(frame1, frame2, curve, output);
```

---

## Dependency Injection

### Service Registration

```csharp
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers IJpegCodec and IHdrBlend as singletons.
    /// </summary>
    public static IServiceCollection AddMjpeg(this IServiceCollection services)
    {
        services.AddSingleton<IJpegCodec, JpegCodec>();
        services.AddSingleton<IHdrBlend, HdrBlend>();
        return services;
    }

    /// <summary>
    /// Registers with custom JPEG codec options.
    /// </summary>
    public static IServiceCollection AddMjpeg(this IServiceCollection services,
        Action<JpegCodecOptions> configure)
    {
        var options = new JpegCodecOptions();
        configure(options);
        services.AddSingleton<IJpegCodec>(_ => new JpegCodec(options));
        services.AddSingleton<IHdrBlend, HdrBlend>();
        return services;
    }
}

public class JpegCodecOptions
{
    public int MaxWidth { get; set; } = 1920;
    public int MaxHeight { get; set; } = 1080;
    public int Quality { get; set; } = 85;
    public DctMethod DctMethod { get; set; } = DctMethod.Integer;
}
```

### Usage

```csharp
// Program.cs
builder.Services.AddMjpeg();

// Or with options
builder.Services.AddMjpeg(options =>
{
    options.MaxWidth = 3840;
    options.MaxHeight = 2160;
    options.Quality = 90;
    options.DctMethod = DctMethod.Float;
});
```

---

## MJPEG Stream Processing

### Detecting JPEG Frames in MJPEG Stream

```csharp
using ModelingEvolution.Mjpeg;

var decoder = new MjpegDecoder();

foreach (var b in mjpegBytes)
{
    var marker = decoder.Decode(b);

    switch (marker)
    {
        case JpegMarker.Start:
            // Frame started at current position - 1
            break;
        case JpegMarker.End:
            // Frame ended at current position
            break;
    }
}
```

### Extracting JPEG Dimensions

```csharp
byte[] jpegData = /* your JPEG frame */;
var (width, height) = JpegDimensionExtractor.Extract(jpegData);

Console.WriteLine($"Image size: {width}x{height}");
```

### Validating JPEG Frame

```csharp
Memory<byte> frame = /* your frame data */;
bool isValid = MjpegDecoder.IsJpeg(frame);
```

### Reverse Scanning (for seeking from end)

```csharp
var decoder = new ReverseMjpegDecoder();

// Scan bytes in reverse order
for (int i = mjpegBytes.Length - 1; i >= 0; i--)
{
    var marker = decoder.Decode(mjpegBytes[i]);
    // ...
}
```

---

## JPEG Markers Reference

| Marker | Bytes | Description |
|--------|-------|-------------|
| SOI | `0xFF 0xD8` | Start Of Image |
| EOI | `0xFF 0xD9` | End Of Image |
| SOF0 | `0xFF 0xC0` | Start Of Frame (Baseline) |
| SOF2 | `0xFF 0xC2` | Start Of Frame (Progressive) |

---

## GStreamer Interoperability

The HDR types are designed to align with the `gsthdr` GStreamer plugin:

| .NET Type | GStreamer Property |
|-----------|-------------------|
| `HdrBlendMode.Average` | `blend-mode=average` |
| `HdrBlendMode.Weighted` | `blend-mode=weighted` |
| `HdrBlendMode.GrayToRgb` | `blend-mode=gray-to-rgb` |
| `HdrProcessingMode.Continuous` | `processing-mode=continuous` |
| `HdrProcessingMode.Burst` | `processing-mode=burst` |
| `HdrWeightCurve.Weights` | `weights` property (comma-separated floats) |

### Converting Weights for GStreamer

```csharp
// Export weights to GStreamer format
string ToGstWeights(HdrWeightCurve curve)
{
    return string.Join(",", curve.Weights.Span.ToArray().Select(w => w.ToString("F4")));
}

// Import weights from GStreamer format
HdrWeightCurve FromGstWeights(string gstWeights)
{
    var weights = gstWeights.Split(',')
        .Select(float.Parse)
        .ToArray();
    return new HdrWeightCurve(weights);
}
```

---

## Performance Considerations

1. **Memory Pooling**: Use `MemoryPool<byte>.Shared` or custom pools to reduce GC pressure
2. **Span-based APIs**: Prefer `Span<byte>` overloads for zero-allocation processing
3. **SIMD Optimization**: HDR blending uses hardware-accelerated SIMD where available
4. **Pre-allocated Buffers**: For streaming scenarios, reuse output buffers

```csharp
// High-performance streaming example
var pool = MemoryPool<byte>.Shared;
Span<byte> outputBuffer = new byte[1920 * 1080 * 3];

while (await stream.ReadFrameAsync())
{
    using var frameA = JpegCodec.Decode(jpegDataA, pool);
    using var frameB = JpegCodec.Decode(jpegDataB, pool);

    HdrBlend.Average(frameA, frameB, outputBuffer);

    int jpegLen = JpegCodec.Encode(
        new FrameImage(frameA.Header, outputBuffer),
        jpegOutputBuffer);

    await SendAsync(jpegOutputBuffer.Slice(0, jpegLen));
}
```

---

## Implementation Details

### JPEG Codec - LibJpegWrap

The library extends the existing `ModelingEvolution.VideoStreaming.LibJpegTurbo` package which provides a custom native wrapper around libjpeg:

**Existing capabilities (from video-streaming):**
- YUV420 (I420) encoding via `LibJpegWrap` native library
- Cross-platform: Windows x64, Linux x64, Linux arm64
- DCT mode selection (Integer/Float) and runtime quality adjustment
- Zero-allocation encoding path

**Extensions needed for mjpeg:**
- JPEG **decoder** (add to LibJpegWrap.cpp)
- RGB/BGR format encoding (extend native wrapper)
- Span-based API with pooled memory support

```cpp
// Existing LibJpegWrap.cpp exports (YUV420 encode only):
extern "C" {
    EXPORT YuvEncoder* Create(int width, int height, int quality, ulong size);
    EXPORT ulong Encode(YuvEncoder* encoder, byte* data, byte* dstBuffer, ulong dstBufferSize);
    EXPORT void SetQuality(YuvEncoder* encoder, int quality);
    EXPORT void SetMode(YuvEncoder* encoder, int mode);  // 0=JDCT_ISLOW, 1=JDCT_FASTEST
    EXPORT void Close(YuvEncoder* encoder);
}

// Extensions needed for mjpeg:
extern "C" {
    // Decoder
    EXPORT JpegDecoder* CreateDecoder();
    EXPORT int Decode(JpegDecoder* decoder, byte* jpegData, ulong jpegSize,
                      byte* output, int* width, int* height, int pixelFormat);
    EXPORT void CloseDecoder(JpegDecoder* decoder);

    // RGB/BGR encoder (in addition to existing YUV encoder)
    EXPORT RgbEncoder* CreateRgbEncoder(int width, int height, int quality,
                                         int pixelFormat, ulong bufSize);
    EXPORT ulong EncodeRgb(RgbEncoder* encoder, byte* data, byte* dst, ulong dstSize);
}
```

### C# Wrapper Architecture

```csharp
// Extend existing JpegEncoder with decode and RGB support
public class JpegCodec : IDisposable
{
    // Reuse existing LibJpegWrap for YUV420 encode
    private readonly JpegEncoder? _yuvEncoder;

    // New: RGB/BGR encoder instance
    private readonly IntPtr _rgbEncoderPtr;

    // New: Decoder instance
    private readonly IntPtr _decoderPtr;

    public FrameHeader Decode(ReadOnlySpan<byte> jpeg, Span<byte> output)
    {
        fixed (byte* src = jpeg)
        fixed (byte* dst = output)
        {
            int width, height;
            Decode(_decoderPtr, src, (ulong)jpeg.Length, dst, &width, &height, (int)PixelFormat.Rgb24);
            return new FrameHeader(width, height, width * 3, PixelFormat.Rgb24, width * height * 3);
        }
    }

    public int Encode(in FrameImage frame, Span<byte> output, int quality = 85)
    {
        return frame.Header.Format switch
        {
            PixelFormat.I420 => EncodeYuv420(frame, output),      // Existing path
            PixelFormat.Rgb24 or PixelFormat.Bgr24 => EncodeRgb(frame, output),  // New path
            >= PixelFormat.Yuy2 => EncodeWithConversion(frame, output),  // Convert first
            _ => throw new NotSupportedException($"Format {frame.Header.Format}")
        };
    }
}
```

### YUV Format Conversion

Extended formats (Yuy2, Uyvy, Nv12) convert to I420 for JPEG encoding:

```csharp
// SIMD-optimized YUV conversion before JPEG encode
internal static class YuvConverter
{
    public static void Yuy2ToI420(ReadOnlySpan<byte> yuy2, Span<byte> i420, int width, int height)
    {
        // YUYV packed (4 bytes/2 pixels) -> I420 planar
        // Y plane: every other byte from YUYV
        // U plane: every 4th byte starting at offset 1, subsampled 2x2
        // V plane: every 4th byte starting at offset 3, subsampled 2x2
    }

    public static void Nv12ToI420(ReadOnlySpan<byte> nv12, Span<byte> i420, int width, int height)
    {
        // NV12: Y plane + interleaved UV -> I420: Y + U + V separate planes
    }
}
```

### HDR Blending - SIMD Implementation

HDR blending operations use `System.Numerics.Vector<T>` for hardware acceleration:

```csharp
// Vectorized average blending (simplified)
internal static void BlendAverage(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> output)
{
    int vectorSize = Vector<byte>.Count;
    int i = 0;

    // SIMD path: process 16-32 bytes at a time
    for (; i <= a.Length - vectorSize; i += vectorSize)
    {
        var va = new Vector<byte>(a.Slice(i));
        var vb = new Vector<byte>(b.Slice(i));
        // Average with rounding: (a + b + 1) >> 1
        var avg = Vector.Divide(Vector.Add(Vector.Add(va, vb), Vector<byte>.One),
                                new Vector<byte>(2));
        avg.CopyTo(output.Slice(i));
    }

    // Scalar tail
    for (; i < a.Length; i++)
        output[i] = (byte)((a[i] + b[i] + 1) >> 1);
}
```

### Native Library Structure

The JPEG codec is self-contained within the mjpeg repository:

```
mjpeg/
├── src/
│   ├── ModelingEvolution.Mjpeg/           # Main C# library
│   │   ├── JpegCodec.cs                   # Public API (to be added)
│   │   ├── JpegEncoder.cs                 # P/Invoke wrapper (to be added)
│   │   └── libs/                          # Native binaries
│   │       ├── win/LibJpegWrap.dll
│   │       ├── linux-x64/LibJpegWrap.so
│   │       └── linux-arm64/LibJpegWrap.so
│   └── LibJpegWrap/                       # Native C++ source
│       ├── LibJpegWrap.cpp                # libjpeg wrapper
│       ├── CMakeLists.txt
│       └── vcpkg.json                     # libjpeg-turbo dependency
```

### Building Native Library

```bash
# Prerequisites: vcpkg with libjpeg-turbo
vcpkg install libjpeg-turbo:x64-linux
vcpkg install libjpeg-turbo:arm64-linux
vcpkg install libjpeg-turbo:x64-windows

# Build
cd src/LibJpegWrap
cmake -B build -DCMAKE_TOOLCHAIN_FILE=$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake
cmake --build build --config Release
```

### Dependencies

No external NuGet dependencies for JPEG codec - native binaries are bundled.

| Component | Purpose | Platforms |
|-----------|---------|-----------|
| LibJpegWrap (bundled) | JPEG encode/decode via libjpeg-turbo | Windows x64, Linux x64/arm64 |

The native library uses libjpeg-turbo (ABI-compatible with standard libjpeg) for SIMD-accelerated compression.

---

## License

MIT License - see [LICENSE](LICENSE) for details.

---

## References

- [libjpeg](https://ijg.org/) - Independent JPEG Group reference implementation
- [libjpeg-turbo](https://libjpeg-turbo.org/) - SIMD-accelerated drop-in replacement (ABI compatible)
- [OpenCV Color Space Conversions](https://docs.opencv.org/3.4/d8/d01/group__imgproc__color__conversions.html) - ColorConversionCodes reference
- [video-streaming submodule](https://github.com/user/video-streaming) - Source for LibJpegWrap native library
