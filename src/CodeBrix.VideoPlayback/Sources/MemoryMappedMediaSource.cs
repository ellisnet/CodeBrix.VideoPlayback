using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source that maps a file into the address space instead of reading it, so seeking is free and the
/// operating system decides what stays in memory.
/// </summary>
/// <remarks>
/// This is the cheapest way to play a file that is seeked around a lot: a jump costs nothing, and pages the
/// player never touches are never read from disk. It costs address space rather than memory, so it suits
/// 64-bit processes and large files.
/// </remarks>
public sealed class MemoryMappedMediaSource : IMediaSource
{
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly long length;
    private long position;
    private bool disposed;

    /// <summary>Maps a file for reading.</summary>
    /// <param name="path">The path of the file.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    /// <exception cref="VideoPlaybackException">The file is empty, which cannot be mapped.</exception>
    public MemoryMappedMediaSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no media file at '{path}'.", path);

        length = new FileInfo(path).Length;
        if (length <= 0)
        {
            throw new VideoPlaybackException($"'{path}' is empty, so there is nothing to map or to play.");
        }

        Path = path;
        Name = System.IO.Path.GetFileName(path);
        map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
    }

    /// <summary>The full path of the mapped file.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public bool IsLengthKnown => true;

    /// <inheritdoc />
    public long Length => length;

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
    public unsafe int ReadAt(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (offset >= length || buffer.Length == 0) return 0;

        int count = (int)Math.Min(buffer.Length, length - offset);
        byte* pointer = null;
        try
        {
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            new ReadOnlySpan<byte>(pointer + view.PointerOffset + offset, count).CopyTo(buffer);
        }
        finally
        {
            if (pointer != null) view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        return count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        view.Dispose();
        map.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(MemoryMappedMediaSource));
    }
}
