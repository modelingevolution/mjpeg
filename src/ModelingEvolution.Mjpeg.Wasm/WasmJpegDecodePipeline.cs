using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using SkiaSharp;

namespace ModelingEvolution.Mjpeg.Wasm;

/// <summary>
/// Async JPEG decode pipeline with internal multi-threading.
/// Push input (JPEG bytes + target bitmap), receive decoded bitmaps in frame order.
/// Uses TPL Dataflow TransformBlock for parallel decode with ordered output.
/// </summary>
public sealed class WasmJpegDecodePipeline : IAsyncDisposable
{
    private TransformBlock<DecodeRequest, DecodeResult> _decodeBlock;
    private readonly ConcurrentQueue<nint> _decoderPool = new();
    private readonly INativeDecoder _native;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _workerCount;
    private ulong _generation;

    public WasmJpegDecodePipeline(int maxWidth, int maxHeight, int workerCount = 2)
        : this(WasmNativeDecoder.Instance, maxWidth, maxHeight, workerCount) { }

    internal WasmJpegDecodePipeline(INativeDecoder native, int maxWidth, int maxHeight, int workerCount = 2)
    {
        _native = native;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _workerCount = workerCount;

        for (int i = 0; i < workerCount; i++)
            _decoderPool.Enqueue(_native.CreateDecoder(maxWidth, maxHeight));

        _decodeBlock = CreateBlock();
    }

    /// <summary>
    /// Push a decode request. Awaits if queue is full (back-pressure).
    /// </summary>
    public ValueTask PushAsync(DecodeRequest request, CancellationToken ct = default)
    {
        return new ValueTask(_decodeBlock.SendAsync(request, ct));
    }

    /// <summary>
    /// Read next decoded result in frame order. Blocks until available.
    /// </summary>
    public ValueTask<DecodeResult> ReadAsync(CancellationToken ct = default)
    {
        return new ValueTask<DecodeResult>(_decodeBlock.ReceiveAsync(ct));
    }

    /// <summary>
    /// Try to push a decode request without blocking.
    /// Returns false immediately if the pipeline is full (BoundedCapacity reached).
    /// </summary>
    public bool Post(DecodeRequest request)
    {
        return _decodeBlock.Post(request);
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

    private readonly ConcurrentDictionary<int, bool> _loggedDecodeThreads = new();

    private DecodeResult Decode(DecodeRequest request, ulong generation)
    {
        var tid = Thread.CurrentThread.ManagedThreadId;
        if (_loggedDecodeThreads.TryAdd(tid, true))
            Console.WriteLine($"[DECODER] Thread {tid} - decode worker");

        if (Interlocked.Read(ref _generation) != generation)
            return new DecodeResult(request.FrameId, request.Target, false);

        if (!_decoderPool.TryDequeue(out var decoder))
            decoder = _native.CreateDecoder(_maxWidth, _maxHeight);

        var success = false;
        var sw = Stopwatch.StartNew();
        try
        {
            var pixels = request.Target.GetPixels();

            Debug.Assert(request.Target.RowBytes == request.Target.Width * 4,
                $"SKBitmap row stride mismatch: {request.Target.RowBytes} != {request.Target.Width * 4}");

            var bufferSize = (uint)(request.Target.RowBytes * request.Target.Height);
            var info = new WasmJpegNative.DecodeInfo();

            unsafe
            {
                using var pin = request.JpegData.Pin();
                var written = _native.Decode(
                    decoder, (nint)pin.Pointer, (uint)request.JpegData.Length,
                    pixels, bufferSize, &info);
                success = written > 0;
            }

            if (success)
                request.Target.NotifyPixelsChanged();
        }
        catch
        {
            // Decode failed -- bitmap stays blank
        }
        finally
        {
            _decoderPool.Enqueue(decoder);
        }

        sw.Stop();
        return new DecodeResult(request.FrameId, request.Target, success, sw.ElapsedTicks);
    }

    public async ValueTask DisposeAsync()
    {
        _decodeBlock.Complete();
        await _decodeBlock.Completion;

        while (_decoderPool.TryDequeue(out var decoder))
            _native.CloseDecoder(decoder);
    }
}

public readonly record struct DecodeRequest(
    ulong FrameId,
    ReadOnlyMemory<byte> JpegData,
    SKBitmap Target);

public readonly record struct DecodeResult(
    ulong FrameId,
    SKBitmap Bitmap,
    bool Success,
    long DecodeTicks = 0);
