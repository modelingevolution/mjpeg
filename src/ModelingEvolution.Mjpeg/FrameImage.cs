using System.Buffers;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Container for raw frame data with multiple memory ownership models.
/// Implements IDisposable to properly release owned memory.
/// </summary>
public readonly struct FrameImage : IDisposable
{
    private readonly IMemoryOwner<byte>? _owner;
    private readonly Memory<byte> _data;

    /// <summary>
    /// Frame metadata describing dimensions and format.
    /// </summary>
    public FrameHeader Header { get; }

    /// <summary>
    /// The frame pixel data.
    /// </summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>
    /// Returns true if this FrameImage owns its memory and will dispose it.
    /// </summary>
    public bool OwnsMemory => _owner != null;

    /// <summary>
    /// Creates a FrameImage with borrowed memory (no ownership transfer).
    /// The caller is responsible for the lifetime of the data.
    /// </summary>
    public FrameImage(FrameHeader header, Memory<byte> data)
    {
        Header = header;
        _data = data;
        _owner = null;
    }

    /// <summary>
    /// Creates a FrameImage with borrowed memory from a byte array.
    /// </summary>
    public FrameImage(FrameHeader header, byte[] data)
        : this(header, data.AsMemory())
    {
    }

    /// <summary>
    /// Creates a FrameImage that takes ownership of pooled memory.
    /// The memory will be disposed when this FrameImage is disposed.
    /// </summary>
    public FrameImage(FrameHeader header, IMemoryOwner<byte> owner)
    {
        Header = header;
        _owner = owner;
        _data = owner.Memory.Slice(0, header.Length);
    }

    /// <summary>
    /// Creates a new FrameImage by allocating from a memory pool.
    /// </summary>
    public static FrameImage Allocate(FrameHeader header, MemoryPool<byte>? pool = null)
    {
        pool ??= MemoryPool<byte>.Shared;
        var owner = pool.Rent(header.Length);
        return new FrameImage(header, owner);
    }

    /// <summary>
    /// Creates a new FrameImage by allocating a new byte array.
    /// </summary>
    public static FrameImage AllocateArray(FrameHeader header)
    {
        var data = new byte[header.Length];
        return new FrameImage(header, data);
    }

    /// <summary>
    /// Gets writable access to the frame data.
    /// Only valid if this FrameImage was created with writable memory.
    /// </summary>
    public Memory<byte> GetWritableData()
    {
        return _data;
    }

    /// <summary>
    /// Disposes the owned memory if this FrameImage owns it.
    /// </summary>
    public void Dispose()
    {
        _owner?.Dispose();
    }
}
