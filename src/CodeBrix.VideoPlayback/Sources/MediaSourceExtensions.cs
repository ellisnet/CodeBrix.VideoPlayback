using System;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// The read helpers every container reader needs: read exactly this many bytes, or say why you could not.
/// </summary>
public static class MediaSourceExtensions
{
    /// <summary>Reads until the buffer is full or the source ends.</summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>How many bytes were actually read - less than the buffer length only at the end of the source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public static int ReadAtLeast(this IMediaSource source, Span<byte> buffer)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer.Slice(total));
            if (read <= 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>Reads until the buffer is full, and refuses to accept anything less.</summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="what">What is being read, for the message if it fails - for example "the EBML header".</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The source ended first.</exception>
    public static void ReadExactly(this IMediaSource source, Span<byte> buffer, string what)
    {
        int read = source.ReadAtLeast(buffer);
        if (read == buffer.Length) return;

        throw new VideoPlaybackException(
            $"'{source.Name}' ended after {read} of the {buffer.Length} bytes needed for {what}. The file is "
            + "truncated or is not the format it claims to be.");
    }

    /// <summary>Reads a whole buffer's worth at an absolute offset, and refuses to accept anything less.</summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="offset">The absolute offset to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="what">What is being read, for the message if it fails.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The source ended first.</exception>
    public static void ReadExactlyAt(this IMediaSource source, long offset, Span<byte> buffer, string what)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.ReadAt(offset + total, buffer.Slice(total));
            if (read <= 0) break;
            total += read;
        }

        if (total == buffer.Length) return;

        throw new VideoPlaybackException(
            $"'{source.Name}' could only supply {total} of the {buffer.Length} bytes needed for {what} at offset "
            + $"{offset}. The file is truncated or is not the format it claims to be.");
    }

    /// <summary>Skips forwards, seeking when the source can and reading and discarding when it cannot.</summary>
    /// <param name="source">The source to advance.</param>
    /// <param name="count">How many bytes to skip. Zero or less does nothing.</param>
    /// <returns>True when the whole distance was skipped; false when the source ended first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public static bool Skip(this IMediaSource source, long count)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (count <= 0) return true;

        if (source.CanSeek)
        {
            long target = source.Position + count;
            if (source.IsLengthKnown && target > source.Length) return false;
            source.Position = target;
            return true;
        }

        Span<byte> scratch = stackalloc byte[4096];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, scratch.Length);
            int read = source.Read(scratch.Slice(0, want));
            if (read <= 0) return false;
            remaining -= read;
        }

        return true;
    }
}
