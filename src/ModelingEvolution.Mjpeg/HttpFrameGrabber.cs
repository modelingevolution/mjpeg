using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Connects to an HTTP multipart/x-mixed-replace endpoint via raw TCP,
/// parses frame boundaries using PipeReader,
/// and keeps only the latest frame as a ref-counted <see cref="JpegFrameHandle"/>.
/// </summary>
public sealed class HttpFrameGrabber : IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _path;
    private readonly ILogger? _logger;

    private TcpClient? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _parseTask;
    private JpegFrameHandle _latest;
    private readonly Lock _frameLock = new();
    private volatile bool _hasFrame;
    private volatile int _frameCount;

    /// <summary>
    /// Creates a new HttpFrameGrabber from explicit host, port, and path.
    /// </summary>
    public HttpFrameGrabber(string host, int port, string path = "/", ILogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new HttpFrameGrabber from a URI (e.g. http://192.168.1.10:8080/stream).
    /// </summary>
    public HttpFrameGrabber(Uri streamUri, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        _host = streamUri.Host;
        _port = streamUri.Port > 0 ? streamUri.Port : 80;
        _path = streamUri.PathAndQuery;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if at least one frame has been grabbed.
    /// </summary>
    public bool HasFrame => _hasFrame;

    /// <summary>
    /// Total number of frames grabbed since start.
    /// </summary>
    public int FrameCount => _frameCount;

    /// <summary>
    /// Connects via TCP, sends HTTP GET, starts background parse loop.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_tcp != null)
            throw new InvalidOperationException("Already started.");

        _tcp = new TcpClient { NoDelay = true };
        _tcp.ReceiveBufferSize = 256 * 1024;
        await _tcp.ConnectAsync(_host, _port, ct);

        var stream = _tcp.GetStream();

        // Send HTTP GET request
        var request = $"GET {_path} HTTP/1.1\r\nHost: {_host}:{_port}\r\nConnection: keep-alive\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, ct);

        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: 256 * 1024,
            minimumReadSize: 64 * 1024));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _parseTask = Task.Run(() => ParseLoop(reader, _cts.Token), _cts.Token);
    }

    /// <summary>
    /// Returns a ref-counted handle to the latest raw JPEG bytes.
    /// Caller must dispose the handle when done.
    /// Returns null if no frame is available or the frame was just recycled.
    /// </summary>
    public JpegFrameHandle? TryAcquireLatest()
    {
        lock (_frameLock)
        {
            var frame = _latest;
            if (!frame.IsValid) return null;
            return frame.TryAddRef() ? frame : null;
        }
    }

    #region State Machine

    private enum State
    {
        HttpHeaders,
        Boundary,
        FrameHeaders,
        FrameBody,
        FrameTrail,
    }

    private static ReadOnlySpan<byte> CrLfCrLf => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;
    private static ReadOnlySpan<byte> BoundaryEquals => "boundary="u8;
    private static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length:"u8;
    private static ReadOnlySpan<byte> HttpPrefix => "HTTP"u8;
    private static ReadOnlySpan<byte> DashDash => "--"u8;

    private async Task ParseLoop(PipeReader reader, CancellationToken ct)
    {
        var state = State.HttpHeaders;
        var boundary = ReadOnlyMemory<byte>.Empty;
        var contentLength = -1;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                while (TryAdvance(ref buffer, ref state, ref boundary, ref contentLength))
                {
                    // State machine advances until it needs more data
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    _logger?.LogInformation("MJPEG stream ended (server closed connection).");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MJPEG parse loop error.");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private bool TryAdvance(
        ref ReadOnlySequence<byte> buffer,
        ref State state,
        ref ReadOnlyMemory<byte> boundary,
        ref int contentLength)
    {
        switch (state)
        {
            case State.HttpHeaders:
                return TryParseHttpHeaders(ref buffer, ref boundary, ref state);

            case State.Boundary:
                return TryParseBoundary(ref buffer, ref state);

            case State.FrameHeaders:
                return TryParseFrameHeaders(ref buffer, ref contentLength, ref state);

            case State.FrameBody:
                return TryReadFrameBody(ref buffer, contentLength, ref state);

            case State.FrameTrail:
                return TryParseFrameTrail(ref buffer, ref state);

            default:
                return false;
        }
    }

    private bool TryParseHttpHeaders(
        ref ReadOnlySequence<byte> buffer,
        ref ReadOnlyMemory<byte> boundary,
        ref State state)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> headers, CrLfCrLf))
            return false;

        // Extract boundary from Content-Type header
        if (boundary.IsEmpty)
        {
            boundary = ExtractBoundary(headers);
            if (!boundary.IsEmpty)
                _logger?.LogDebug("Boundary: {Boundary}", Encoding.ASCII.GetString(boundary.Span));
        }

        buffer = buffer.Slice(reader.Position);
        state = State.Boundary;
        return true;
    }

    private static ReadOnlyMemory<byte> ExtractBoundary(ReadOnlySequence<byte> headers)
    {
        // Scan headers for "boundary=" and extract the value
        var reader = new SequenceReader<byte>(headers);
        Span<byte> scratch = stackalloc byte[(int)Math.Min(headers.Length, 4096)];
        headers.CopyTo(scratch);
        var headersSpan = scratch.Slice(0, (int)headers.Length);

        var idx = headersSpan.IndexOf(BoundaryEquals);
        if (idx < 0) return ReadOnlyMemory<byte>.Empty;

        var start = idx + BoundaryEquals.Length;
        var remaining = headersSpan.Slice(start);

        // Find end: \r, \n, space, or semicolon
        var end = remaining.Length;
        for (int i = 0; i < remaining.Length; i++)
        {
            var b = remaining[i];
            if (b == '\r' || b == '\n' || b == ' ' || b == ';')
            {
                end = i;
                break;
            }
        }

        // Copy boundary to owned memory (small, done once)
        return remaining.Slice(0, end).ToArray();
    }

    private bool TryParseBoundary(ref ReadOnlySequence<byte> buffer, ref State state)
    {
        var reader = new SequenceReader<byte>(buffer);

        // Find end of line
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, CrLf))
            return false;

        buffer = buffer.Slice(reader.Position);
        state = State.FrameHeaders;
        return true;
    }

    private bool TryParseFrameHeaders(
        ref ReadOnlySequence<byte> buffer,
        ref int contentLength,
        ref State state)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> headers, CrLfCrLf))
            return false;

        contentLength = ExtractContentLength(headers);

        buffer = buffer.Slice(reader.Position);
        state = State.FrameBody;
        return true;
    }

    private static int ExtractContentLength(ReadOnlySequence<byte> headers)
    {
        Span<byte> scratch = stackalloc byte[(int)Math.Min(headers.Length, 1024)];
        headers.CopyTo(scratch);
        var headersSpan = scratch.Slice(0, (int)headers.Length);

        var idx = headersSpan.IndexOf(ContentLengthHeader);
        if (idx < 0) return -1;

        var start = idx + ContentLengthHeader.Length;
        var remaining = headersSpan.Slice(start);

        // Skip leading whitespace
        int offset = 0;
        while (offset < remaining.Length && remaining[offset] == ' ')
            offset++;

        // Parse digits
        int value = 0;
        while (offset < remaining.Length)
        {
            var b = remaining[offset];
            if (b < '0' || b > '9') break;
            value = value * 10 + (b - '0');
            offset++;
        }

        return value > 0 ? value : -1;
    }

    private bool TryReadFrameBody(ref ReadOnlySequence<byte> buffer, int contentLength, ref State state)
    {
        if (contentLength > 0)
        {
            // Fast path: known content length
            if (buffer.Length < contentLength)
                return false;

            var jpegSequence = buffer.Slice(0, contentLength);
            PublishFrame(jpegSequence, contentLength);

            buffer = buffer.Slice(contentLength);
            state = State.FrameTrail;
            return true;
        }
        else
        {
            // Slow path: scan for JPEG end marker (FFD9)
            // Then look for boundary after it
            var reader = new SequenceReader<byte>(buffer);
            long frameEnd = -1;

            while (reader.TryRead(out byte b))
            {
                if (b == 0xFF && reader.TryPeek(out byte next) && next == 0xD9)
                {
                    reader.Advance(1); // consume D9
                    frameEnd = reader.Consumed;
                    break;
                }
            }

            if (frameEnd < 0)
                return false;

            var jpegSequence = buffer.Slice(0, frameEnd);
            PublishFrame(jpegSequence, (int)frameEnd);

            buffer = buffer.Slice(frameEnd);
            state = State.FrameTrail;
            return true;
        }
    }

    private bool TryParseFrameTrail(ref ReadOnlySequence<byte> buffer, ref State state)
    {
        // Skip optional \r\n after body
        if (buffer.Length < 2)
            return false;

        var reader = new SequenceReader<byte>(buffer);

        // Consume \r\n if present
        if (reader.TryPeek(0, out byte b0) && reader.TryPeek(1, out byte b1)
            && b0 == '\r' && b1 == '\n')
        {
            reader.Advance(2);
        }

        // Peek ahead to determine next state
        if (buffer.Length - reader.Consumed < 4)
        {
            // Need more data to decide — consume what we've skipped so far
            if (reader.Consumed > 0)
                buffer = buffer.Slice(reader.Position);
            return false;
        }

        // Check if next is HTTP response or boundary
        var remaining = buffer.Slice(reader.Position);
        var peekReader = new SequenceReader<byte>(remaining);

        Span<byte> peek = stackalloc byte[4];
        if (peekReader.TryCopyTo(peek))
        {
            if (peek.SequenceEqual(HttpPrefix))
                state = State.HttpHeaders;
            else
                state = State.Boundary;
        }
        else
        {
            state = State.Boundary;
        }

        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private void PublishFrame(ReadOnlySequence<byte> jpegSequence, int length)
    {
        var handle = JpegFrameHandle.Rent(length);
        jpegSequence.CopyTo(handle.DataSpan);

        JpegFrameHandle old;
        lock (_frameLock)
        {
            old = _latest;
            _latest = handle;
        }

        _hasFrame = true;
        Interlocked.Increment(ref _frameCount);

        if (old.IsValid)
            old.Dispose();
    }

    #endregion

    /// <summary>
    /// Stops the background parse loop and closes the TCP connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_parseTask != null)
            {
                try { await _parseTask; }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }

        _tcp?.Dispose();
        _tcp = null;

        lock (_frameLock)
        {
            if (_latest.IsValid)
                _latest.Dispose();
            _latest = default;
        }
    }

    /// <summary>
    /// Synchronous dispose for convenience.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        if (_parseTask != null)
        {
            try { _parseTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;

        _tcp?.Dispose();
        _tcp = null;

        lock (_frameLock)
        {
            if (_latest.IsValid)
                _latest.Dispose();
            _latest = default;
        }
    }
}
