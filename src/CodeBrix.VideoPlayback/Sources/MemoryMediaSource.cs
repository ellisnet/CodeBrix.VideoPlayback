using System;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source over bytes that are already in memory.
/// </summary>
/// <remarks>
/// Used directly for media embedded in an assembly or produced in memory, and used underneath
/// <see cref="PreloadedMediaSource" />, which is the same thing with a pooled buffer behind it.
/// </remarks>
public sealed class MemoryMediaSource : IMediaSource
{
    private readonly ReadOnlyMemory<byte> data;
    private long position;
    private bool disposed;

    /// <summary>Wraps a block of memory.</summary>
    /// <param name="data">The bytes of the whole media file.</param>
    /// <param name="name">A short description used in error messages, or null to use a generic one.</param>
    public MemoryMediaSource(ReadOnlyMemory<byte> data, string name = null)
    {
        this.data = data;
        Name = string.IsNullOrEmpty(name) ? $"memory ({data.Length} bytes)" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public bool IsLengthKnown => true;

    /// <inheritdoc />
    public long Length => data.Length;

    /// <inheritdoc />
    public long Position
    {
        get => position;
        set
        {
            ThrowIfDisposed();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The position cannot be negative.");
            position = value;
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        int read = ReadAt(position, buffer);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (offset >= data.Length || buffer.Length == 0) return 0;

        int count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.Span.Slice((int)offset, count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc />
    public void Dispose() => disposed = true;

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(MemoryMediaSource));
    }
}
