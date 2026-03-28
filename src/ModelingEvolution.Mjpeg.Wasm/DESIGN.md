# ModelingEvolution.Mjpeg.Wasm — Design Document

**Goal:** Multi-threaded JPEG decoder for Blazor WebAssembly, targeting Full HD (1920x1080) at 30fps.

---

## Problem

The WASM Playback Player in rocket-welder2 decodes JPEG frames using `SKBitmap.Decode()` (~30ms per frame).
This limits playback to ~33fps theoretical max and blocks the single UI thread.

`SKBitmap.Decode` is slow because:
1. SkiaSharp wraps libjpeg-turbo but adds overhead: SKCodec header parsing, color space conversion, GC-tracked bitmap allocation
2. SkiaSharp is NOT thread-safe for concurrent decode (issues #209, #1628)
3. No way to reuse decoder state across frames (creates/destroys `jpeg_decompress_struct` per call)

## Solution

A dedicated WASM JPEG decoder that:
- Uses libjpeg-turbo directly via Emscripten-compiled native code
- Pools decoder instances (reuse `jpeg_decompress_struct` across frames)
- Decodes to BGRA directly (`JCS_EXT_BGRA` — libjpeg-turbo extension)
- Supports multi-threaded decode via `WasmEnableThreads`
- Produces output compatible with `SKBitmap.InstallPixels()` (zero-copy)

---

## Architecture

```
                    ModelingEvolution.Mjpeg.Wasm
                    ┌─────────────────────────────────────┐
                    │                                     │
  JPEG bytes ──────▶│  WasmJpegDecoderPool                │──────▶ BGRA pixel buffer
  (from chunk)      │  ├── Decoder A (Thread 1)           │       (pinned, reusable)
                    │  └── Decoder B (Thread 2)           │
                    │                                     │
                    │  Each decoder:                      │
                    │  ├── Native: I420Decoder instance    │
                    │  ├── Output: pinned byte[] (BGRA)   │
                    │  └── Reused across frames            │
                    └─────────────────────────────────────┘
                         │
                         ▼
                    SKBitmap.InstallPixels(info, ptr)
                    (zero-copy — bitmap wraps the buffer)
```

### Components

#### 1. Native: LibJpegWrap.a (Emscripten static library)

Extends the existing `LibJpegWrap.cpp` with a BGRA decode function:

```cpp
#include <setjmp.h>

// Custom error handler — recovers via longjmp instead of calling exit()
// CRITICAL for WASM: default jpeg_error_exit calls exit() which kills the entire app.
struct safe_error_mgr {
    struct jpeg_error_mgr pub;
    jmp_buf setjmp_buffer;
};

static void safe_error_exit(j_common_ptr cinfo) {
    safe_error_mgr* myerr = (safe_error_mgr*)cinfo->err;
    longjmp(myerr->setjmp_buffer, 1);
}

// BGRA Decoder — reuses jpeg_decompress_struct, safe error handling
class BgraDecoder {
public:
    struct jpeg_decompress_struct cinfo;
    safe_error_mgr jerr;
    int max_width;
    int max_height;

    BgraDecoder(int maxWidth, int maxHeight)
        : max_width(maxWidth), max_height(maxHeight)
    {
        cinfo.err = jpeg_std_error(&jerr.pub);
        jerr.pub.error_exit = safe_error_exit;  // override exit() with longjmp
        jpeg_create_decompress(&cinfo);
    }

    ~BgraDecoder() { jpeg_destroy_decompress(&cinfo); }

    ulong DecodeBGRA(const byte* jpegData, ulong jpegSize,
                     byte* output, ulong outputSize, DecodeInfo* info)
    {
        // setjmp returns 0 on first call, non-zero on longjmp from error_exit
        if (setjmp(jerr.setjmp_buffer)) {
            // Error recovery — abort decompress and return 0
            jpeg_abort_decompress(&cinfo);
            return 0;
        }

        // Setup memory source (reuse existing pattern)
        jpeg_memory_src(&cinfo, jpegData, jpegSize);

        if (jpeg_read_header(&cinfo, TRUE) != JPEG_HEADER_OK)
            return 0;

        cinfo.out_color_space = JCS_EXT_BGRA;  // Direct BGRA — libjpeg-turbo extension
        cinfo.raw_data_out = FALSE;

        jpeg_start_decompress(&cinfo);

        int width = cinfo.output_width;
        int height = cinfo.output_height;
        int rowBytes = width * 4;
        ulong totalSize = (ulong)rowBytes * height;

        info->width = width;
        info->height = height;
        info->components = 4;
        info->colorSpace = cinfo.out_color_space;

        if (totalSize > outputSize) {
            jpeg_abort_decompress(&cinfo);
            return 0;
        }

        // Read scanlines directly into caller's buffer (SKBitmap pixel memory)
        while (cinfo.output_scanline < cinfo.output_height) {
            byte* rowPtr = output + cinfo.output_scanline * rowBytes;
            jpeg_read_scanlines(&cinfo, &rowPtr, 1);
        }

        jpeg_finish_decompress(&cinfo);
        return totalSize;
    }
};

// Exported C functions
extern "C" {
    EXPORT BgraDecoder* CreateBgraDecoder(int maxWidth, int maxHeight) {
        return new BgraDecoder(maxWidth, maxHeight);
    }
    EXPORT void CloseBgraDecoder(BgraDecoder* decoder) {
        delete decoder;
    }
    EXPORT ulong DecoderDecodeBGRA(BgraDecoder* decoder,
        const byte* jpegData, ulong jpegSize,
        byte* output, ulong outputSize, DecodeInfo* info) {
        return decoder->DecodeBGRA(jpegData, jpegSize, output, outputSize, info);
    }
}
```

**Error handling:** Custom `safe_error_exit` prevents `exit()` on corrupted JPEG frames.
Without this, a single bad frame kills the entire WASM app.

**Implementation note:** The original design specified `setjmp`/`longjmp` for error recovery.
However, the .NET WASM runtime's linker uses `-mllvm -wasm-enable-sjlj` which is incompatible
with Emscripten's JS-based `setjmp`/`longjmp` emulation — linking fails with conflicting
sjlj implementations. The actual implementation uses **flag-based error recovery** instead:
`safe_error_exit` sets `has_error = true` and returns (instead of calling `longjmp`).
The `DecodeBGRA` method checks `has_error` after each libjpeg call and aborts if set.
This is equally safe and avoids the linker incompatibility.

**Why BGRA?** SkiaSharp's `SKColorType.Bgra8888` is the native pixel format for `SKBitmap` on most platforms.
Decoding directly to BGRA eliminates the YCbCr→BGRA conversion pass that `SKBitmap.Decode` does separately.

**Build:** Emscripten toolchain, static library output.

```bash
# Build libjpeg-turbo for WASM
emcmake cmake -S libjpeg-turbo -B build-wasm -DCMAKE_BUILD_TYPE=Release
emmake make -C build-wasm -j$(nproc)

# Build LibJpegWrap linking against WASM libjpeg-turbo
emcc -O2 -c LibJpegWrap.cpp -I build-wasm -o LibJpegWrap.o
emar rcs LibJpegWrap.a LibJpegWrap.o
```

#### 2. C# Managed: WasmJpegDecodePipeline

Fully async, push-based decoder using **TPL Dataflow `TransformBlock`** for internal
multi-threading with ordered output. No manual reorder buffer needed.

```csharp
using System.Threading.Tasks.Dataflow;

/// <summary>
/// Async JPEG decode pipeline with internal multi-threading.
/// Push input (JPEG bytes + target bitmap), receive decoded bitmaps in frame order.
/// Uses TPL Dataflow TransformBlock for parallel decode with ordered output.
/// </summary>
public sealed class WasmJpegDecodePipeline : IAsyncDisposable
{
    private TransformBlock<DecodeRequest, DecodeResult> _decodeBlock;
    private readonly ConcurrentQueue<nint> _decoderPool = new();  // native decoder pool
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _workerCount;
    private ulong _generation;  // incremented on seek/loop to discard stale results

    public WasmJpegDecodePipeline(int maxWidth, int maxHeight, int workerCount = 2)
    {
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _workerCount = workerCount;

        // Pre-create native decoder instances (one per worker thread)
        for (int i = 0; i < workerCount; i++)
            _decoderPool.Enqueue(WasmJpegNative.CreateBgraDecoder(maxWidth, maxHeight));

        _decodeBlock = new TransformBlock<DecodeRequest, DecodeResult>(
            request => Decode(request),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = workerCount,   // 2 competing threads
                BoundedCapacity = workerCount * 2,      // back-pressure at 4
                EnsureOrdered = true,                   // output in input order
                SingleProducerConstrained = true        // one producer optimization
            });
    }

    /// <summary>
    /// Push a decode request. Awaits if queue is full (back-pressure).
    /// The target bitmap must be pre-allocated (BGRA8888, correct dimensions).
    /// </summary>
    public ValueTask PushAsync(DecodeRequest request, CancellationToken ct = default)
    {
        // SendAsync returns false if block is completed
        return new ValueTask(_decodeBlock.SendAsync(request, ct));
    }

    /// <summary>
    /// Read next decoded result in frame order. Blocks until available.
    /// TPL Dataflow guarantees output order matches input order.
    /// </summary>
    public ValueTask<DecodeResult> ReadAsync(CancellationToken ct = default)
    {
        return new ValueTask<DecodeResult>(_decodeBlock.ReceiveAsync(ct));
    }

    /// <summary>
    /// Try read next decoded result without blocking.
    /// </summary>
    public bool TryRead(out DecodeResult result)
    {
        return _decodeBlock.TryReceive(out result);
    }

    /// <summary>
    /// Number of items waiting in output queue.
    /// </summary>
    public int OutputCount => _decodeBlock.OutputCount;

    /// <summary>
    /// Reset pipeline for seek/loop. Discards in-flight results from previous generation.
    /// Creates a fresh TransformBlock — the old one completes and is GC'd.
    /// </summary>
    public void Reset()
    {
        _decodeBlock.Complete();
        Interlocked.Increment(ref _generation);
        _decodeBlock = CreateBlock();
    }

    private TransformBlock<DecodeRequest, DecodeResult> CreateBlock()
    {
        var gen = Interlocked.Read(ref _generation);
        return new TransformBlock<DecodeRequest, DecodeResult>(
            request => Decode(request, gen),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = _workerCount,
                BoundedCapacity = _workerCount * 2,
                EnsureOrdered = true,
                SingleProducerConstrained = true
            });
    }

    private DecodeResult Decode(DecodeRequest request, ulong generation)
    {
        // Discard if from a stale generation (seek/loop happened)
        if (Interlocked.Read(ref _generation) != generation)
            return new DecodeResult(request.FrameId, request.Target, false);

        // Rent native decoder from pool
        if (!_decoderPool.TryDequeue(out var decoder))
            decoder = WasmJpegNative.CreateBgraDecoder(_maxWidth, _maxHeight);

        var success = false;
        try
        {
            var pixels = request.Target.GetPixels();

            // Assert row stride matches expected (no padding)
            Debug.Assert(request.Target.RowBytes == request.Target.Width * 4,
                $"SKBitmap row stride mismatch: {request.Target.RowBytes} != {request.Target.Width * 4}");

            var bufferSize = (ulong)(request.Target.RowBytes * request.Target.Height);
            var info = new DecodeInfo();

            unsafe
            {
                using var pin = request.JpegData.Pin();
                var written = WasmJpegNative.DecoderDecodeBGRA(
                    decoder, (nint)pin.Pointer, (ulong)request.JpegData.Length,
                    pixels, bufferSize, &info);
                success = written > 0;
            }

            if (success)
                request.Target.NotifyPixelsChanged();
        }
        catch
        {
            // Decode failed — bitmap stays blank
        }
        finally
        {
            _decoderPool.Enqueue(decoder);
        }

        return new DecodeResult(request.FrameId, request.Target, success);
    }

    public async ValueTask DisposeAsync()
    {
        _decodeBlock.Complete();
        await _decodeBlock.Completion;

        // Dispose all native decoders
        while (_decoderPool.TryDequeue(out var decoder))
            WasmJpegNative.CloseBgraDecoder(decoder);
    }
}

public readonly record struct DecodeRequest(
    ulong FrameId,
    ReadOnlyMemory<byte> JpegData,
    SKBitmap Target);

public readonly record struct DecodeResult(
    ulong FrameId,
    SKBitmap Bitmap,
    bool Success);
```

**Why TPL Dataflow `TransformBlock`:**

1. **`EnsureOrdered = true`** (default) — output order matches input order even with parallel workers.
   No manual `SortedDictionary` reorder buffer needed.

2. **`MaxDegreeOfParallelism = 2`** — 2 threads decode concurrently, managed by the block.
   Each invocation dispatched to thread pool.

3. **`BoundedCapacity = 4`** — back-pressure. `SendAsync` awaits when queue is full.
   Prevents unbounded memory growth from producer outpacing decoder.

4. **`SingleProducerConstrained = true`** — optimization for single-producer pattern.

5. **Built-in completion** — `Complete()` + `await Completion` for clean shutdown.

**No manual threading code.** No channels, no worker loops, no reorder logic.
`TransformBlock` handles all of it.

#### Buffer Ownership: SKBitmap IS the buffer

The caller allocates the SKBitmap, pushes it into the pipeline, and receives it back
in the output. The pipeline writes into the bitmap but never allocates or frees it.

```
Bitmap pool (RenderedWindow)
  │
  ▼ Rent bitmap
Caller (Vector Compositor stage in playback player)
  │
  ▼ pipeline.PushAsync(new DecodeRequest(frameId, jpegData, bitmap))
TransformBlock (MaxDegreeOfParallelism = 2, EnsureOrdered = true)
  │  Thread A ──┐
  │  Thread B ──┤  ← decode in parallel, write into bitmap.GetPixels()
  │             ▼
  │  TPL Dataflow reorder buffer (internal)
  │             │
  │             ▼ emit in input order
  ▼ pipeline.ReadAsync() → DecodeResult { FrameId, Bitmap, Success }
Caller receives bitmap with decoded pixels
  │
  ▼ compose overlays onto separate overlay bitmap
DecodedFrame { Jpeg = bitmap, Overlay = overlayBitmap }
  │
  ▼ flows through pipeline
RenderedWindow → Render Loop → OnPaint → DrawBitmap
  │
  ▼ after eviction
Bitmap pool (returned, erased, reused)
```

**No intermediate buffers.** The decoder writes into the SKBitmap's pixel memory.
The bitmap flows through the entire pipeline and returns to the pool.
Native decoders are pooled per-thread — rent/return from `ConcurrentBag`.

---

## Integration with Playback Player

The playback player wires the decode pipeline between the receiver and vector compositor:

```csharp
// Startup
_decodePipeline = new WasmJpegDecodePipeline(1920, 1080, workerCount: 2);

// JPEG Decode feeder — pushes RawFrames into the pipeline
async Task RunJpegDecodeFeeder(CancellationToken ct)
{
    await foreach (var frameId in _framesToDecode.Reader.ReadAllAsync(ct))
    {
        if (!_receiver.TryGetRaw(frameId, out var raw)) continue;

        var bitmap = _bitmapPool.Rent(1920, 1080);
        await _decodePipeline.PushAsync(
            new DecodeRequest(frameId, raw.JpegData, bitmap), ct);
    }
}

// Vector Compositor — reads decoded bitmaps in order, composes overlays
async Task RunVectorCompositor(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var result = await _decodePipeline.ReadAsync(ct);
        if (!result.Success) { _bitmapPool.Return(result.Bitmap); continue; }

        // Overlays must be composed in frame order (keypoints use delta encoding)
        if (_receiver.TryGetRaw(result.FrameId, out var raw))
        {
            var overlay = _bitmapPool.RentOverlay(result.Bitmap.Width, result.Bitmap.Height);
            RenderOverlays(overlay, raw);
            _renderedWindow.Add(result.FrameId, new DecodedFrame(result.Bitmap, overlay));
        }
    }
}
```

**The caller never touches threads.** Push work, read ordered results.
`TransformBlock` handles parallelism, ordering, back-pressure, and thread dispatch internally.

---

## Project Structure

```
mjpeg/src/
├── LibJpegWrap/
│   ├── LibJpegWrap.cpp            # Existing + new DecoderDecodeBGRA function
│   ├── CMakeLists.txt             # Existing native build (win/linux)
│   └── CMakeLists.wasm.txt        # NEW: Emscripten build → LibJpegWrap.a
│
├── ModelingEvolution.Mjpeg/       # Existing (win/linux native package)
│
├── ModelingEvolution.Mjpeg.Wasm/  # NEW: WASM-specific package
│   ├── DESIGN.md                  # This file
│   ├── ModelingEvolution.Mjpeg.Wasm.csproj
│   ├── WasmJpegDecoder.cs         # Single decoder instance
│   ├── WasmJpegDecoderPool.cs     # Thread-safe pool
│   ├── WasmJpegNative.cs          # P/Invoke declarations (subset of JpegTurboNative.cs)
│   └── native/
│       └── LibJpegWrap.a          # Pre-built Emscripten static library
│
├── ModelingEvolution.Mjpeg.Wasm.TestApp/  # NEW: Blazor WASM test app
│   ├── ModelingEvolution.Mjpeg.Wasm.TestApp.csproj
│   ├── Program.cs
│   └── Pages/
│       └── DecodeBenchmark.razor  # Decode benchmark page
│
└── ModelingEvolution.Mjpeg.Wasm.Tests/    # NEW: Unit tests
    ├── WasmJpegDecoderTests.cs
    └── WasmJpegDecoderPoolTests.cs
```

### Csproj: ModelingEvolution.Mjpeg.Wasm

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>browser-wasm</RuntimeIdentifier>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <NativeFileReference Include="native/LibJpegWrap.o" />
    <NativeFileReference Include="native/libjpeg.a" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SkiaSharp" Version="3.119.1" />
  </ItemGroup>
</Project>
```

### Csproj: TestApp

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <WasmEnableThreads>true</WasmEnableThreads>
    <WasmEnableSIMD>true</WasmEnableSIMD>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ModelingEvolution.Mjpeg.Wasm\ModelingEvolution.Mjpeg.Wasm.csproj" />
    <PackageReference Include="SkiaSharp.Views.Blazor" Version="3.119.1" />
    <PackageReference Include="SkiaSharp.NativeAssets.WebAssembly" Version="3.119.1" />
  </ItemGroup>
</Project>
```

---

## Acceptance Criteria

All criteria must pass before the library can be published and used in rocket-welder2.

### AC-1: Decode Performance (Release WASM build)

| # | Criterion | Target | How to verify |
|---|-----------|--------|---------------|
| 1 | Single frame 1920x1080 decode (warmed) | <33ms | Benchmark scenario #1 |
| 2 | Sustained 1-thread 30 frames | <1000ms total | Benchmark scenario #2 |
| 3 | Parallel 2-thread 30 frames | <700ms total | Benchmark scenario #3 |
| 4 | Pool rent/return 100x (incl. decode) | no leak, 100/100 success | Benchmark scenario #4 |
| 5 | Decode + write to SKBitmap | <35ms | Benchmark scenario #5 |
| 6 | Single-thread fallback (no MT) | <33ms/frame | Benchmark scenario #6 |

### AC-2: Correctness

| # | Criterion | Target | How to verify |
|---|-----------|--------|---------------|
| 7 | Pixel correctness vs SKBitmap.Decode | 100% match (±2 tolerance) | Benchmark scenario #7 |
| 8 | Frame ordering with 2 parallel threads | Strict 1..30 order | Benchmark scenario #8 |
| 9 | No memory copies (zero-copy pipeline) | No .ToArray(), no Buffer.Copy in hot path | Code review |

### AC-3: SKCanvasView Visual Rendering

| # | Criterion | Target | How to verify |
|---|-----------|--------|---------------|
| 10 | SKCanvasView renders decoded frame | Image visible, correct colors | Visual check in browser |
| 11 | No "Cannot call synchronous C# methods" error | 0 console errors | Browser console check |
| 12 | Works with WasmEnableThreads=true | No crash, canvas renders | Run TestApp with MT enabled |

### AC-4: TestApp Runs as Proper Blazor App

| # | Criterion | Target | How to verify |
|---|-----------|--------|---------------|
| 13 | TestApp has ASP.NET Core host with COOP/COEP headers | SharedArrayBuffer available | `crossOriginIsolated === true` in console |
| 14 | `dotnet run` starts the app (debug mode) | App loads at localhost | Developer workflow |
| 15 | `dotnet publish -c Release` + serve works | App loads with AOT | Production workflow |
| 16 | All 8 benchmark scenarios run without errors | 0 unhandled exceptions | Full benchmark run |

### AC-5: Package Integration

| # | Criterion | Target | How to verify |
|---|-----------|--------|---------------|
| 17 | NuGet package published | Available on nuget.modelingevolution.com | `dotnet restore` succeeds |
| 18 | Consumers reference via PackageReference | No ProjectReference required | rocket-welder2 Client.csproj |
| 19 | NativeFileReference documented for consumers | README explains app-side .o/.a requirement | Package README |

---

## Test App

### Architecture

The TestApp is an **ASP.NET Core hosted Blazor WASM** app (not standalone) to support:
- COOP/COEP headers (required for `SharedArrayBuffer` / `WasmEnableThreads`)
- Proper static asset serving (compressed `.br`/`.gz` files)
- `dotnet run` for development, `dotnet publish -c Release` for production testing

```
ModelingEvolution.Mjpeg.Wasm.TestApp/        # ASP.NET Core host + Blazor WASM client
├── Program.cs                               # Host with COOP/COEP middleware
├── Pages/
│   └── DecodeBenchmark.razor                # All benchmark scenarios + visual verification
└── wwwroot/
    └── sample.jpg                           # Bundled Full HD test image (optional)
```

### DecodeBenchmark.razor

8 benchmark scenarios + visual verification via SKCanvasView:

| # | Scenario | Threads | Target | What it proves |
|---|----------|---------|--------|----------------|
| 1 | Single 1920x1080 decode | 1 | <33ms | Raw decode speed |
| 2 | Sequential 30 frames | 1 | <1000ms total | Sustained throughput |
| 3 | Parallel 30 frames | 2 | <700ms total | MT speedup |
| 4 | Pool rent/return 100x | 2 | no leak | Pool correctness |
| 5 | Decode + InstallPixels | 1 | <35ms | Full pipeline to SKBitmap |
| 6 | Single-thread fallback | 1 | <33ms/frame | Works without MT |
| 7 | Pixel correctness | 1 | 100% match | BGRA output correct |
| 8 | Frame ordering | 2 | 1..30 strict | TransformBlock ordering |

### Visual verification

The test app renders the last decoded frame to an `SKCanvasView` so you can visually confirm:
- Colors are correct (BGRA byte order)
- No artifacts from YCbCr→BGRA conversion
- Image matches the source JPEG
- **No console errors** (especially no "Cannot call synchronous C# methods")

---

## Build Steps

### 1. Build libjpeg-turbo for WASM

```bash
cd mjpeg/src/LibJpegWrap

# Clone libjpeg-turbo at PINNED version (reproducible builds)
git clone --branch 3.1.0 --depth 1 \
  https://github.com/libjpeg-turbo/libjpeg-turbo.git

# Build with Emscripten — MUST include -pthread for MT compatibility
mkdir build-wasm && cd build-wasm
emcmake cmake ../libjpeg-turbo \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_FLAGS="-pthread" \
  -DENABLE_SHARED=OFF \
  -DWITH_TURBOJPEG=OFF \
  -DWITH_SIMD=OFF
emmake make -j$(nproc)
cd ..
```

### 2. Build LibJpegWrap.o

```bash
# Compile LibJpegWrap.cpp with Emscripten — MUST include -pthread
emcc -O2 -pthread -c LibJpegWrap.cpp \
  -I build-wasm \
  -I libjpeg-turbo \
  -o LibJpegWrap.o
```

**NOTE:** Do NOT combine `.o` and `.a` into a single archive with `emar` — Emscripten
cannot nest `.a` inside `.a`. Instead, reference both files separately via `NativeFileReference`.

### 3. Copy native files to project

```bash
mkdir -p ../ModelingEvolution.Mjpeg.Wasm/native/
cp LibJpegWrap.o ../ModelingEvolution.Mjpeg.Wasm/native/
cp build-wasm/libjpeg.a ../ModelingEvolution.Mjpeg.Wasm/native/
```

### 4. Csproj references both native files

```xml
<ItemGroup>
  <NativeFileReference Include="native/LibJpegWrap.o" />
  <NativeFileReference Include="native/libjpeg.a" />
</ItemGroup>
```

### 5. Build and test

```bash
cd mjpeg/src
dotnet build ModelingEvolution.Mjpeg.Wasm.TestApp/
dotnet run --project ModelingEvolution.Mjpeg.Wasm.TestApp/
# Open browser to the benchmark page
```

### Emscripten version pinning

The `.o` and `.a` files MUST be compiled with the same Emscripten version bundled in
the .NET SDK. To find the version:

```bash
# Check .NET SDK's bundled Emscripten
ls $(dotnet --list-sdks | tail -1 | cut -d' ' -f2 | tr -d '[]')/packs/Microsoft.NET.Runtime.WebAssembly.Sdk/
```

Pin your Emscripten SDK to match. Mismatched versions produce linker errors.

---

## Performance Targets

**Benchmark first, then optimize.** The TestApp benchmarks decode time in Release WASM build.
Targets below are estimates — actual numbers come from benchmarking.

| Metric | Estimated | Must-have | How to measure |
|--------|-----------|-----------|----------------|
| Single frame decode (1920x1080) | 15-25ms | <33ms (30fps) | TestApp single-decode scenario |
| Sustained throughput (1 thread) | 40-66fps | ≥30fps | TestApp sequential-30 scenario |
| Sustained throughput (2 threads) | 60-120fps | ≥45fps | TestApp parallel-30 scenario |
| Memory per decoder | ~1KB | <1MB | Native struct only (no output buffer) |
| Pool size | 2 | 2 | Matches player's 2 decode threads |
| DecodeInto overhead (excl. decode) | <0.1ms | <1ms | TestApp decode-into-SKBitmap scenario |

**Note:** libjpeg-turbo without SIMD in WASM may be 2-3x slower than native SIMD.
Estimated single-frame: 15-25ms. If >25ms, 2 threads still achieve 30fps effective.

### 30fps Full HD budget

```
Per frame at 30fps = 33.3ms budget
  JPEG decode:      ~15-25ms (1 thread) or ~10-15ms (2 threads sharing load)
  Vector compose:    ~3ms
  Render:            ~2ms
  Overhead:          ~5ms
  Headroom:          ~0-8ms (tight — benchmarking critical)
```

### Benchmark scenarios (TestApp — Release WASM build)

| # | Scenario | What it measures | Accept if |
|---|----------|-----------------|-----------|
| 1 | Single 1920x1080 decode | Raw decode latency | <33ms |
| 2 | Sequential 30 frames (1 thread) | Sustained single-thread throughput | <1000ms (30fps) |
| 3 | Parallel 30 frames (2 threads) | MT speedup factor | <700ms (>1.4x speedup) |
| 4 | Pool rent/return 1000x | Pool overhead | <1ms total |
| 5 | DecodeInto SKBitmap | Full pipeline including NotifyPixelsChanged | <35ms |
| 6 | Fallback: WasmEnableThreads=false | Single-thread still works | <33ms per frame |

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| `JCS_EXT_BGRA` not available | Can't decode BGRA directly | Build libjpeg-turbo from source (guarantees extension). Fallback: `JCS_RGB` + manual conversion (+~3ms) |
| Emscripten version mismatch | Link errors | Pin to .NET SDK's bundled Emscripten version (see build steps) |
| WASM without SIMD too slow (>25ms) | Can't sustain 30fps single-thread | **Benchmark early.** 2 threads provide headroom. Worst case: accept 20fps |
| Native crash on corrupted JPEG | Kills entire WASM app | Custom `safe_error_exit` with `setjmp`/`longjmp` (see native code above) |
| .NET WASM threading is experimental | May regress between .NET previews | Fallback: `TransformBlock` with `MaxDegreeOfParallelism=1` still works single-threaded |
| `SKBitmap.RowBytes` has padding | Decode writes to wrong offsets | `Debug.Assert(RowBytes == Width * 4)` at decode time |
| Thread safety of `jpeg_decompress_struct` | Crashes | Each decoder instance independent (own struct, own buffers, pooled via `ConcurrentQueue`) |
| Seek/loop with in-flight decodes | Stale frames rendered | Generation counter — `Reset()` increments generation, stale results discarded |

## Fallback: WasmEnableThreads Unavailable

When `WasmEnableThreads` is not enabled (older browsers, missing COOP/COEP headers):
- `TransformBlock` with `MaxDegreeOfParallelism=2` silently falls back to sequential execution
- P/Invoke still works (single-threaded WASM can call native functions)
- Performance: single-thread decode speed, no parallelism
- **Must verify in TestApp benchmark scenario #6**

## Resolved Questions

1. **JCS_EXT_BGRA** — Resolved: build libjpeg-turbo 3.1.0 from source. Extension guaranteed.
2. **Emscripten version** — Resolved: pin to .NET SDK's bundled version (see build steps).
3. **GCHandle across threads** — Resolved: shared linear memory (SharedArrayBuffer). Not used for output — SKBitmap.GetPixels() used instead.
4. **Error handling** — Resolved: custom `safe_error_exit` with `setjmp`/`longjmp`. See native code.
5. **Seek/loop** — Resolved: `Reset()` method with generation counter. Old TransformBlock completes, new one created.
