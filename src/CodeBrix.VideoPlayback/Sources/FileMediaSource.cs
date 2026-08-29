using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source that streams a file from disk, reading ahead as playback needs it and seeking with the file
/// system.
/// </summary>
/// <remarks>
/// This is the right choice for a long file: nothing is loaded until it is needed, and a seek costs one
/// file-system seek. For a short clip that will be played over and over, <see cref="PreloadedMediaSource" />
/// avoids the file system entirely after the first load.
/// </remarks>
public sealed class FileMediaSource : IMediaSource
{
    private readonly FileStream stream;
    private bool disposed;

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The path of the file.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public FileMediaSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no media file at '{path}'.", path);

        stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);

        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    /// <summary>The full path of the file being read.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public bool IsLengthKnown => true;

    /// <inheritdoc />
    public long Length => stream.Length;

    /// <inheritdoc />
    public long Position
    {
        get
        {
            ThrowIfDisposed();
            return stream.Position;
        }

        set
        {
            ThrowIfDisposed();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The position cannot be negative.");
            stream.Position = value;
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        return stream.Read(buffer);
    }

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (offset >= stream.Length) return 0;
        return RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(FileMediaSource));
    }
}
