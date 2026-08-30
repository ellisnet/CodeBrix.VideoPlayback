using System;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// Opens a media file with whichever container reader its first four bytes call for.
/// </summary>
/// <remarks>
/// <para>
/// Two containers are read here: the bespoke <c>.cbv</c>, whose files begin with <c>CBVF</c>, and
/// Matroska/WebM, whose files begin with EBML's <c>1A 45 DF A3</c>. The extension is never consulted - a
/// WebM-profile <c>.cbv</c> file IS a WebM file and is opened by the Matroska reader, which is the whole
/// point of the two flavours sharing one extension.
/// </para>
/// <para>
/// This is the same sniff the playback session performs when it opens a file, exposed so that a tool, a
/// verifier or an authoring step can read a container's structure without starting a session.
/// </para>
/// </remarks>
public static class MediaContainers
{
    /// <summary>Opens a file or URL and returns the reader its header calls for.</summary>
    /// <param name="pathOrUrl">A file path, a <c>file://</c> URL or an <c>http(s)://</c> URL.</param>
    /// <returns>The reader, which OWNS the source it opened and closes it when disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="pathOrUrl" /> is null or blank.</exception>
    /// <exception cref="VideoPlaybackException">The header is neither container's.</exception>
    public static IMediaContainerReader Open(string pathOrUrl)
    {
        IMediaSource source = MediaSources.Open(pathOrUrl);

        try
        {
            return Open(source, false);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Reads a source's first bytes and returns the reader they call for.</summary>
    /// <param name="source">The source to read.</param>
    /// <param name="leaveSourceOpen">
    /// True to leave the source open when the reader is disposed; false to hand the reader ownership of it.
    /// </param>
    /// <returns>The reader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The source is shorter than a container header, or begins with something that is neither
    /// <c>CBVF</c> nor EBML's signature.
    /// </exception>
    public static IMediaContainerReader Open(IMediaSource source, bool leaveSourceOpen = false)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        Span<byte> magic = stackalloc byte[4];
        int read = source.CanSeek ? source.ReadAt(0, magic) : source.ReadAtLeast(magic);

        if (read < 4)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' is only {read} bytes long, so it carries no container header at all.");
        }

        if (source.CanSeek) source.Position = 0;

        if (CbvReader.IsCbv(magic)) return new CbvReader(source, leaveSourceOpen);

        if (MatroskaReader.IsMatroska(magic)) return new MatroskaReader(source, leaveSourceOpen);

        throw new VideoPlaybackException(
            $"'{source.Name}' begins with {magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}, which is "
            + "neither the bespoke container's 'CBVF' nor Matroska's 1A 45 DF A3. This library plays WebM, "
            + "Matroska and .cbv files.");
    }
}
