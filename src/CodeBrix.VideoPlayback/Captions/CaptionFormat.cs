namespace CodeBrix.VideoPlayback.Captions;

/// <summary>
/// The text format a caption track's cues were written in.
/// </summary>
/// <remarks>
/// The format matters because it says what <see cref="CaptionCue.Settings" /> means and how much of the
/// original styling survived. This library carries cue TEXT faithfully in every case; drawing it - and any
/// positioning or styling the format allows - is a presenter's job.
/// </remarks>
public enum CaptionFormat
{
    /// <summary>The format is not known.</summary>
    Unknown = 0,

    /// <summary>
    /// WebVTT. <see cref="CaptionCue.Settings" /> carries the cue settings list untouched, and
    /// <see cref="CaptionCue.Identifier" /> the cue identifier if the cue had one.
    /// </summary>
    WebVtt = 1,

    /// <summary>
    /// SubRip - plain UTF-8 text, Matroska's <c>S_TEXT/UTF8</c>. There are no cue settings, so
    /// <see cref="CaptionCue.Settings" /> is empty.
    /// </summary>
    SubRip = 2,

    /// <summary>
    /// Advanced SubStation. The dialogue TEXT is extracted; the styling, positioning and effects fields are
    /// dropped, so a cue reads as plain text.
    /// </summary>
    Ass = 3,
}
