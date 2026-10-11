using System;
using CodeBrix.VideoPlayback.Captions;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// One caption file on its way into a Matroska or WebM file, with the things the file itself cannot say: what
/// language it is in, what to call it, and what it is for.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.vtt</c> file is carried cue for cue, with each cue's identifier and settings list. A <c>.srt</c> file
/// is carried as WebVTT too - the streamable profile allows WebVTT captions only - with its cue text unchanged
/// and no identifier or settings, which is all a SubRip cue has to offer.
/// </para>
/// <para>
/// The flags map onto the track as follows: <see cref="CaptionTrackFlags.Default" /> sets <c>FlagDefault</c>,
/// <see cref="CaptionTrackFlags.Forced" /> sets <c>FlagForced</c>, and
/// <see cref="CaptionTrackFlags.HearingImpaired" /> makes the track's codec identifier
/// <c>D_WEBVTT/CAPTIONS</c> instead of <c>D_WEBVTT/SUBTITLES</c> - which is how the WebM element list says
/// "captions for the deaf and hard of hearing" - AND sets <c>FlagHearingImpaired</c>, so a reader that looks
/// for the flag rather than the codec identifier sees it too.
/// </para>
/// </remarks>
public sealed class MatroskaCaptionInput
{
    /// <summary>Creates a caption input.</summary>
    /// <param name="path">The path of a <c>.vtt</c> or <c>.srt</c> file.</param>
    /// <param name="language">The BCP 47 language tag for the track, or null for none.</param>
    /// <param name="name">A name to show in a menu, or null.</param>
    /// <param name="flags">What the track is for.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    public MatroskaCaptionInput(
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

    /// <summary>The BCP 47 language tag for the track, or an empty string.</summary>
    public string Language { get; }

    /// <summary>A name to show in a menu, or an empty string.</summary>
    public string Name { get; }

    /// <summary>What the track is for.</summary>
    public CaptionTrackFlags Flags { get; }
}
