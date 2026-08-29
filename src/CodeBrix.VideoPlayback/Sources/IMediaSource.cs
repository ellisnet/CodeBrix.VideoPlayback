using System;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// Where a container reader gets its bytes from - a file, an HTTP response, a memory-mapped view, a block of
/// memory, or any seekable stream.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately small, and deliberately honest about what a source cannot do. A progressive
/// HTTP download from a server with no range support can be read forwards and nothing else; a memory-mapped
/// file can be read at any offset for free. A container reader asks
/// <see cref="CanSeek" /> and <see cref="IsLengthKnown" /> and adapts, rather than discovering the limitation
/// half way through a file.
/// </para>
/// <para>
/// Two ways to read. <see cref="Read" /> advances <see cref="Position" /> and is what a forward-only
/// demultiplexer uses. <see cref="ReadAt" /> reads at an absolute offset without disturbing
/// <see cref="Position" />, which is how an index or a cues element at the end of a file is fetched; it
/// requires <see cref="CanSeek" />.
/// </para>
/// <para>
/// A source is used from ONE thread at a time - the demultiplexing thread. Implementations do not have to be
/// thread-safe.
/// </para>
/// </remarks>
public interface IMediaSource : IDisposable
{
    /// <summary>A short description of where the bytes come from, used in error messages. Never null.</summary>
    string Name { get; }

    /// <summary>True when <see cref="Position" /> may be set and <see cref="ReadAt" /> may be called.</summary>
    bool CanSeek { get; }

    /// <summary>True when <see cref="Length" /> is a real number rather than "not known".</summary>
    bool IsLengthKnown { get; }

    /// <summary>The total number of bytes, or -1 when the source cannot say.</summary>
    long Length { get; }

    /// <summary>The offset the next <see cref="Read" /> will start at.</summary>
    /// <exception cref="NotSupportedException">Set on a source whose <see cref="CanSeek" /> is false.</exception>
    long Position { get; set; }

    /// <summary>Reads forwards from <see cref="Position" />, advancing it by however much was read.</summary>
    /// <param name="buffer">Where to put the bytes.</param>
    /// <returns>
    /// How many bytes were read: fewer than asked for is legal, and zero means the end of the source.
    /// </returns>
    int Read(Span<byte> buffer);

    /// <summary>Reads at an absolute offset without disturbing <see cref="Position" />.</summary>
    /// <param name="offset">The absolute offset to read from.</param>
    /// <param name="buffer">Where to put the bytes.</param>
    /// <returns>How many bytes were read; zero at or past the end of the source.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanSeek" /> is false.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset" /> is negative.</exception>
    int ReadAt(long offset, Span<byte> buffer);
}
