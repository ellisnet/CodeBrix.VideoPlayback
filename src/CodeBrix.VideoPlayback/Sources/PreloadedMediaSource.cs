using System;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source that reads a <see cref="PreloadedClip" /> - a whole media file already sitting in a pooled
/// buffer.
/// </summary>
/// <remarks>
/// Obtained from <see cref="PreloadedClip.OpenSource" />. Disposing it releases nothing: the bytes belong to
/// the clip, and the clip may be played again immediately.
/// </remarks>
public sealed class PreloadedMediaSource : IMediaSource
{
    private readonly PreloadedClip clip;
    private long position;
    private bool disposed;

    internal PreloadedMediaSource(PreloadedClip clip)
    {
        this.clip = clip;
    }

    /// <inheritdoc />
    public string Name => clip.Name;

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public bool IsLengthKnown => true;

    /// <inheritdoc />
    public long Length => clip.Length;

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

        ReadOnlySpan<byte> data = clip.Data.Span;
        if (offset >= data.Length || buffer.Length == 0) return 0;

        int count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.Slice((int)offset, count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc />
    public void Dispose() => disposed = true;

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PreloadedMediaSource));
    }
}
