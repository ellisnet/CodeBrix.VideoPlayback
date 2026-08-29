using System;

namespace CodeBrix.VideoPlayback.Captions;

/// <summary>
/// What a container says a caption track is FOR.
/// </summary>
/// <remarks>
/// These are the three flags Matroska carries and the bespoke container stores, and they are what an
/// application needs to build a sensible subtitle menu: which track to select by default, which to show even
/// when subtitles are switched off, and which is written for a viewer who cannot hear the audio.
/// </remarks>
[Flags]
public enum CaptionTrackFlags
{
    /// <summary>Nothing special is claimed about the track.</summary>
    None = 0,

    /// <summary>The track the author expects to be selected when nothing else is chosen.</summary>
    Default = 1,

    /// <summary>
    /// The track carries only the parts that must be read whatever the viewer chose - signs, and dialogue in
    /// a language the audio does not cover. A player shows these even with subtitles off.
    /// </summary>
    Forced = 2,

    /// <summary>
    /// The track is written for viewers who are deaf or hard of hearing: it describes sounds and names
    /// speakers as well as transcribing dialogue.
    /// </summary>
    HearingImpaired = 4,
}
