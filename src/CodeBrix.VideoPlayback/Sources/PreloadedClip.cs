using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A whole media file held in one pooled buffer, ready to be played instantly and repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// This is what an application uses for the clips it ships with itself - a splash animation, a transition, a
/// short loop. Load it once at start-up; after that every play starts with no file-system work at all, and
/// several plays can run at once because each one gets its own cursor over the same bytes.
/// </para>
/// <para>
/// The buffer comes from the shared array pool, so a clip that is loaded and released repeatedly does not
/// churn the large-object heap. Dispose the clip when the application is finished with it; sources still open
/// over it keep working until they are disposed too, but the bytes are only guaranteed while the clip lives,
/// so dispose the sources first.
/// </para>
/// </remarks>
public sealed class PreloadedClip : IDisposable
{
    private byte[] buffer;
    private int length;
    private bool disposed;

    private PreloadedClip(byte[] buffer, int length, string name)
    {
        this.buffer = buffer;
        this.length = length;
        Name = name;
    }

    /// <summary>A short description of where the bytes came from, used in error messages.</summary>
    public string Name { get; }

    /// <summary>How many bytes the clip holds.</summary>
    public int Length => length;

    /// <summary>Reads a whole file into memory.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>A clip holding the file's bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    /// <exception cref="VideoPlaybackException">The file is larger than 2 GB, which will not fit in one buffer.</exception>
    public static PreloadedClip FromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no media file at '{path}'.", path);

        long fileLength = new FileInfo(path).Length;
        if (fileLength > int.MaxValue)
        {
            throw new VideoPlaybackException(
                $"'{path}' is {fileLength} bytes, which is too large to preload into one buffer. Play it with a "
                + $"{nameof(FileMediaSource)} or a {nameof(MemoryMappedMediaSource)} instead.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)fileLength);
        try
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int total = 0;
            while (total < (int)fileLength)
            {
                int read = stream.Read(rented, total, (int)fileLength - total);
                if (read <= 0) break;
                total += read;
            }

            return new PreloadedClip(rented, total, System.IO.Path.GetFileName(path));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <summary>Reads a whole file into memory without blocking the calling thread.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task producing a clip holding the file's bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public static async Task<PreloadedClip> FromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no media file at '{path}'.", path);

        long fileLength = new FileInfo(path).Length;
        if (fileLength > int.MaxValue)
        {
            throw new VideoPlaybackException(
                $"'{path}' is {fileLength} bytes, which is too large to preload into one buffer. Play it with a "
                + $"{nameof(FileMediaSource)} or a {nameof(MemoryMappedMediaSource)} instead.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)fileLength);
        try
        {
            await using FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);

            int total = 0;
            while (total < (int)fileLength)
            {
                int read = await stream
                    .ReadAsync(rented.AsMemory(total, (int)fileLength - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }

            return new PreloadedClip(rented, total, System.IO.Path.GetFileName(path));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <summary>Takes a copy of bytes that are already in memory.</summary>
    /// <param name="data">The bytes of the whole media file.</param>
    /// <param name="name">A short description used in error messages.</param>
    /// <returns>A clip holding a pooled copy of the bytes.</returns>
    public static PreloadedClip FromBytes(ReadOnlySpan<byte> data, string name = "preloaded clip")
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 1));
        data.CopyTo(rented);
        return new PreloadedClip(rented, data.Length, name);
    }

    /// <summary>Opens a source over the clip. Each source has its own cursor, so several may be open at once.</summary>
    /// <returns>A source that reads the clip's bytes.</returns>
    /// <exception cref="ObjectDisposedException">The clip has been disposed.</exception>
    public IMediaSource OpenSource()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PreloadedClip));
        return new PreloadedMediaSource(this);
    }

    /// <summary>The clip's bytes.</summary>
    /// <exception cref="ObjectDisposedException">The clip has been disposed.</exception>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            if (disposed) throw new ObjectDisposedException(nameof(PreloadedClip));
            return buffer.AsMemory(0, length);
        }
    }

    /// <summary>Gives the pooled buffer back.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        byte[] toReturn = buffer;
        buffer = null;
        length = 0;
        if (toReturn != null) ArrayPool<byte>.Shared.Return(toReturn);
    }
}
