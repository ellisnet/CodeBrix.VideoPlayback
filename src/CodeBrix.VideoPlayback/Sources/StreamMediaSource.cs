using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source over any <see cref="Stream" /> - an embedded resource, a decrypted stream, a network stream
/// somebody else opened.
/// </summary>
/// <remarks>
/// The stream's own <see cref="Stream.CanSeek" /> decides whether the container reader gets random access. A
/// forward-only stream still plays: the readers fall back to sequential demultiplexing and simply cannot
/// seek.
/// </remarks>
public sealed class StreamMediaSource : IMediaSource
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool disposed;
    private long positionWhenNotSeekable;

    /// <summary>Wraps a stream.</summary>
    /// <param name="stream">The stream to read. It must be readable.</param>
    /// <param name="name">A short description used in error messages, or null to use a generic one.</param>
    /// <param name="leaveOpen">True to leave the stream open when this source is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot be read.</exception>
    public StreamMediaSource(Stream stream, string name = null, bool leaveOpen = false)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("The stream must be readable.", nameof(stream));

        this.stream = stream;
        this.leaveOpen = leaveOpen;
        Name = string.IsNullOrEmpty(name) ? $"stream ({stream.GetType().Name})" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool CanSeek => !disposed && stream.CanSeek;

    /// <inheritdoc />
    public bool IsLengthKnown => CanSeek;

    /// <inheritdoc />
    public long Length => CanSeek ? stream.Length : -1L;

    /// <inheritdoc />
    public long Position
    {
        get
        {
            ThrowIfDisposed();
            return stream.CanSeek ? stream.Position : positionWhenNotSeekable;
        }

        set
        {
            ThrowIfDisposed();
            if (!stream.CanSeek)
            {
                throw new NotSupportedException($"'{Name}' cannot seek: the underlying stream is forward-only.");
            }

            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The position cannot be negative.");
            stream.Position = value;
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int read = stream.Read(buffer);
        if (!stream.CanSeek && read > 0) positionWhenNotSeekable += read;
        return read;
    }

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (!stream.CanSeek)
        {
            throw new NotSupportedException($"'{Name}' cannot read at an offset: the underlying stream is forward-only.");
        }

        long saved = stream.Position;
        try
        {
            if (offset >= stream.Length) return 0;
            stream.Position = offset;
            return stream.Read(buffer);
        }
        finally
        {
            stream.Position = saved;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveOpen) stream.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamMediaSource));
    }
}
