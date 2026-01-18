using System.Buffers;
using System.Text.Json.Serialization;

namespace ModelingEvolution.Mjpeg.Cli;

/// <summary>
/// Wraps an ArrayPool rental with the actual data length.
/// </summary>
internal sealed class PooledArrayOwner : IMemoryOwner<byte>
{
    private byte[]? _buffer;
    private readonly int _length;

    public PooledArrayOwner(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public Memory<byte> Memory => _buffer != null
        ? _buffer.AsMemory(0, _length)
        : throw new ObjectDisposedException(nameof(PooledArrayOwner));

    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}

/// <summary>
/// Recording metadata for index.json deserialization.
/// </summary>
internal record RecordingMetadata
{
    public string? Caps { get; init; }
    public SortedList<ulong, FrameIndex> Index { get; init; } = new();

    /// <summary>
    /// Parses format from Caps string. Returns null if not found.
    /// </summary>
    public string? GetFormat()
    {
        if (string.IsNullOrEmpty(Caps)) return null;

        const string formatPrefix = "format=(string)";
        var idx = Caps.IndexOf(formatPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + formatPrefix.Length;
        var end = Caps.IndexOfAny([',', ' ', ';'], start);
        return end < 0 ? Caps[start..] : Caps[start..end];
    }
}

/// <summary>
/// Frame index entry.
/// </summary>
internal record FrameIndex
{
    [JsonPropertyName("s")]
    public ulong Start { get; init; }

    [JsonPropertyName("sz")]
    public ulong Size { get; init; }

    [JsonPropertyName("t")]
    public ulong RelativeTimestampMicroseconds { get; init; }
}

/// <summary>
/// Reads frames from a recording data file with thread-safe access.
/// </summary>
internal sealed class RecordingFrameReader : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly SortedList<ulong, FrameIndex> _index;
    private readonly IList<ulong> _frameKeys;
    private readonly object _lock = new();

    public RecordingFrameReader(string dataPath, SortedList<ulong, FrameIndex> index)
    {
        _fileStream = File.OpenRead(dataPath);
        _index = index;
        _frameKeys = index.Keys;
    }

    public RecordingFrameReader(string dataPath, RecordingMetadata metadata)
        : this(dataPath, metadata.Index)
    {
    }

    public Task<IMemoryOwner<byte>> ReadFrameAsync(ulong frameIndex)
    {
        if (frameIndex >= (ulong)_frameKeys.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        var frameKey = _frameKeys[(int)frameIndex];
        var frame = _index[frameKey];

        var buffer = ArrayPool<byte>.Shared.Rent((int)frame.Size);

        lock (_lock)
        {
            _fileStream.Seek((long)frame.Start, SeekOrigin.Begin);
            _fileStream.ReadExactly(buffer, 0, (int)frame.Size);
        }

        return Task.FromResult<IMemoryOwner<byte>>(new PooledArrayOwner(buffer, (int)frame.Size));
    }

    public void Dispose()
    {
        _fileStream.Dispose();
    }
}
