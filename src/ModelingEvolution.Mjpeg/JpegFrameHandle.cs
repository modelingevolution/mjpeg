using System.Buffers;
using System.Runtime.CompilerServices;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Ref-counted handle to a JPEG frame stored in an ArrayPool buffer.
/// Buffer layout: [0..8) ulong refCount | [8..8+Length) JPEG data.
/// Zero-allocation in steady state — buffers are rented/returned via ArrayPool.
/// </summary>
/// <remarks>
/// Ownership rules:
/// - <see cref="Rent"/> creates a handle with refCount = 1. The caller owns it.
/// - <see cref="TryAddRef"/> atomically increments refCount if > 0. Returns false if already disposed.
/// - <see cref="Dispose"/> decrements refCount. Returns buffer to pool when it reaches 0.
/// - Raw struct copy does NOT increment refCount (no copy ctor in C#). Use <see cref="TryAddRef"/>.
/// </remarks>
public readonly record struct JpegFrameHandle : IDisposable
{
    private const int HeaderSize = sizeof(ulong);

    private readonly byte[]? _buffer;

    /// <summary>
    /// Length of the JPEG data in bytes (excluding the refCount header).
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// The raw JPEG data as a read-only memory region.
    /// </summary>
    public ReadOnlyMemory<byte> Data => _buffer != null
        ? _buffer.AsMemory(HeaderSize, Length)
        : ReadOnlyMemory<byte>.Empty;

    private JpegFrameHandle(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    /// <summary>
    /// Returns true if this handle has a valid buffer.
    /// </summary>
    public bool IsValid => _buffer != null;

    /// <summary>
    /// Rents a buffer from ArrayPool and sets refCount to 1.
    /// Use <see cref="DataSpan"/> to write JPEG data after construction.
    /// </summary>
    /// <param name="jpegSize">Size of the JPEG data in bytes.</param>
    /// <returns>A new handle with refCount = 1.</returns>
    public static JpegFrameHandle Rent(int jpegSize)
    {
        var buf = ArrayPool<byte>.Shared.Rent(HeaderSize + jpegSize);
        Unsafe.WriteUnaligned(ref buf[0], (ulong)1);
        return new JpegFrameHandle(buf, jpegSize);
    }

    /// <summary>
    /// Writable span for the JPEG data region. Use during initial fill only.
    /// </summary>
    internal Span<byte> DataSpan => _buffer != null
        ? _buffer.AsSpan(HeaderSize, Length)
        : Span<byte>.Empty;

    /// <summary>
    /// Attempts to atomically increment the reference count.
    /// Returns false if the buffer has already been returned to the pool (refCount was 0).
    /// Uses a CAS loop to prevent the dangerous 0→1 resurrection.
    /// </summary>
    public bool TryAddRef()
    {
        if (_buffer == null) return false;

        ref ulong rc = ref Unsafe.As<byte, ulong>(ref _buffer[0]);
        while (true)
        {
            ulong current = Volatile.Read(ref rc);
            if (current == 0) return false;
            if (Interlocked.CompareExchange(ref rc, current + 1, current) == current)
                return true;
        }
    }

    /// <summary>
    /// Decrements the reference count. Returns the buffer to ArrayPool when it reaches 0.
    /// </summary>
    public void Dispose()
    {
        if (_buffer == null) return;

        ref ulong rc = ref Unsafe.As<byte, ulong>(ref _buffer[0]);
        if (Interlocked.Decrement(ref rc) == 0)
            ArrayPool<byte>.Shared.Return(_buffer);
    }
}
