using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// Turns whatever an application has - a path, an address, a stream - into an <see cref="IMediaSource" />.
/// </summary>
public static class MediaSources
{
    /// <summary>Opens a source for a file path or an HTTP(S) address.</summary>
    /// <param name="pathOrUrl">
    /// A file-system path, a <c>file://</c> address, or an <c>http://</c> or <c>https://</c> address.
    /// </param>
    /// <param name="mode">How a local file should be read. Ignored for an address.</param>
    /// <returns>A source ready to be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="pathOrUrl" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">A local path was given and there is no file there.</exception>
    public static IMediaSource Open(string pathOrUrl, FileSourceMode mode = FileSourceMode.Streaming)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            throw new ArgumentException("A file path or an address is required.", nameof(pathOrUrl));
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) return HttpMediaSource.Create(uri);
            if (uri.Scheme == Uri.UriSchemeFile) return OpenFile(uri.LocalPath, mode);
        }

        return OpenFile(pathOrUrl, mode);
    }

    /// <summary>Opens a source for a local file.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="mode">How the file should be read.</param>
    /// <returns>A source ready to be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public static IMediaSource OpenFile(string path, FileSourceMode mode = FileSourceMode.Streaming)
    {
        switch (mode)
        {
            case FileSourceMode.MemoryMapped:
                return new MemoryMappedMediaSource(path);
            case FileSourceMode.Preloaded:
                return PreloadedClip.FromFile(path).OpenSource();
            default:
                return new FileMediaSource(path);
        }
    }
}
