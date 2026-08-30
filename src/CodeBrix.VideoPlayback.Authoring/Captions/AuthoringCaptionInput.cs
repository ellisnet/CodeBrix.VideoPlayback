using System;
using System.IO;
using CodeBrix.VideoPlayback.Captions;

namespace CodeBrix.VideoPlayback.Authoring.Captions;

/// <summary>
/// One caption file on its way into an authored file, with the three things the file itself cannot say:
/// what language it is in, what to call it in a menu, and what it is for.
/// </summary>
/// <remarks>
/// <para>
/// WebVTT (<c>.vtt</c>) and SubRip (<c>.srt</c>) files are both read by the bespoke flavour. The
/// WebM-profile flavour takes WebVTT ONLY, because its caption tracks are copied into the container
/// unaltered - which is the only way to keep a cue's identifier and its positioning settings - and a
/// Matroska-family container has no home for a SubRip stream in a WebM document.
/// </para>
/// <para>Instances are immutable.</para>
/// </remarks>
public sealed class AuthoringCaptionInput
{
    /// <summary>Creates a caption input.</summary>
    /// <param name="path">The path of a <c>.vtt</c> or <c>.srt</c> file.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">A name to show in a menu, or null.</param>
    /// <param name="flags">What the track is for.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    public AuthoringCaptionInput(
        string path,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A caption file path is required.", nameof(path));
        }

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

    /// <summary>True when the file's extension says it is WebVTT.</summary>
    public bool IsWebVtt =>
        string.Equals(System.IO.Path.GetExtension(Path), ".vtt", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() =>
        System.IO.Path.GetFileName(Path)
        + " [" + (Language.Length == 0 ? "no language" : Language) + "]"
        + (Name.Length == 0 ? string.Empty : " \"" + Name + "\"")
        + (Flags == CaptionTrackFlags.None ? string.Empty : " " + Flags);
}
