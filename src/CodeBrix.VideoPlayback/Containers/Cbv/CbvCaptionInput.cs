using System;
using CodeBrix.VideoPlayback.Captions;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// One caption file on its way into a bespoke container, with the things the file itself cannot say: what
/// language it is in, what to call it, and what it is for.
/// </summary>
public sealed class CbvCaptionInput
{
    /// <summary>Creates a caption input.</summary>
    /// <param name="path">The path of a <c>.vtt</c> or <c>.srt</c> file.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">A name to show in a menu, or null.</param>
    /// <param name="flags">What the track is for.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    public CbvCaptionInput(
        string path,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A caption file path is required.", nameof(path));

        Path = path;
        Language = language ?? string.Empty;
        Name = name ?? string.Empty;
        Flags = flags;
    }

    /// <summary>The path of the caption file.</summary>
    public string Path { get; }

    /// <summary>The BCP 47 language tag for the track.</summary>
    public string Language { get; }

    /// <summary>A name to show in a menu, or an empty string.</summary>
    public string Name { get; }

    /// <summary>What the track is for.</summary>
    public CaptionTrackFlags Flags { get; }
}
