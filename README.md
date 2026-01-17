# ModelingEvolution.Mjpeg

A .NET library for MJPEG stream processing with JPEG boundary detection, dimension extraction, and frame indexing utilities.

## Installation

```bash
dotnet add package ModelingEvolution.Mjpeg
```

## Features

- **JPEG Boundary Detection** - State machine-based detection of JPEG frame boundaries (SOI/EOI markers)
- **Dimension Extraction** - Extract width/height from JPEG SOF0/SOF2 markers
- **Bidirectional Scanning** - Forward and reverse MJPEG decoders for flexible stream processing
- **Frame Validation** - Validate JPEG frame integrity

## Usage

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
using ModelingEvolution.Mjpeg;

byte[] jpegData = /* your JPEG frame */;
var (width, height) = JpegDimensionExtractor.Extract(jpegData);

Console.WriteLine($"Image size: {width}x{height}");
```

### Validating JPEG Frame

```csharp
using ModelingEvolution.Mjpeg;

Memory<byte> frame = /* your frame data */;
bool isValid = MjpegDecoder.IsJpeg(frame);
```

### Reverse Scanning (for seeking from end)

```csharp
using ModelingEvolution.Mjpeg;

var decoder = new ReverseMjpegDecoder();

// Scan bytes in reverse order
for (int i = mjpegBytes.Length - 1; i >= 0; i--)
{
    var marker = decoder.Decode(mjpegBytes[i]);
    // ...
}
```

## JPEG Markers Reference

| Marker | Bytes | Description |
|--------|-------|-------------|
| SOI | `0xFF 0xD8` | Start Of Image |
| EOI | `0xFF 0xD9` | End Of Image |
| SOF0 | `0xFF 0xC0` | Start Of Frame (Baseline) |
| SOF2 | `0xFF 0xC2` | Start Of Frame (Progressive) |

## License

MIT License - see [LICENSE](LICENSE) for details.
