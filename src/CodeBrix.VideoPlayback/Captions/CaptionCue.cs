using System;

namespace CodeBrix.VideoPlayback.Captions;

/// <summary>
/// One caption: when it appears, when it goes away, what it says, and any placement instructions the format
/// carried.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Settings" /> is passed through EXACTLY as the file wrote it, with no attempt to interpret it.
/// A WebVTT cue settings list such as <c>line:90% align:center</c> reaches a presenter unchanged, so a
/// presenter that understands WebVTT positioning can honour it and one that does not can ignore it without
/// anything being lost on the way.
/// </para>
/// </remarks>
public sealed class CaptionCue
{
    /// <summary>Creates a cue.</summary>
    /// <param name="start">When the cue appears, relative to the start of the media.</param>
    /// <param name="end">When the cue goes away.</param>
    /// <param name="text">The cue's text, in UTF-8 as the file carried it, decoded to a string.</param>
    /// <param name="settings">The format's raw placement instructions, or an empty string when there are none.</param>
    /// <param name="identifier">The cue's identifier, or an empty string when it had none.</param>
    public CaptionCue(TimeSpan start, TimeSpan end, string text, string settings = "", string identifier = "")
    {
        Start = start;
        End = end;
        Text = text ?? string.Empty;
        Settings = settings ?? string.Empty;
        Identifier = identifier ?? string.Empty;
    }

    /// <summary>When the cue appears, relative to the start of the media.</summary>
    public TimeSpan Start { get; }

    /// <summary>When the cue goes away.</summary>
    public TimeSpan End { get; }

    /// <summary>How long the cue is on screen.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>The cue's text. Never null.</summary>
    public string Text { get; }

    /// <summary>The format's raw placement instructions, passed through untouched. Never null.</summary>
    public string Settings { get; }

    /// <summary>The cue's identifier, or an empty string when it had none. Never null.</summary>
    public string Identifier { get; }

    /// <summary>Reports whether the cue should be on screen at a given moment.</summary>
    /// <param name="position">A position in the media.</param>
    /// <returns>True when the position is at or after <see cref="Start" /> and before <see cref="End" />.</returns>
    public bool IsActiveAt(TimeSpan position) => position >= Start && position < End;

    /// <inheritdoc />
    public override string ToString() => $"[{Start} - {End}] {Text}";
}
