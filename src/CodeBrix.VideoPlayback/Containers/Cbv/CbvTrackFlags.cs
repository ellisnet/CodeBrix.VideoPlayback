using System;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The flags on a track entry in a bespoke file's header.
/// </summary>
[Flags]
public enum CbvTrackFlags
{
    /// <summary>Nothing is claimed.</summary>
    None = 0,

    /// <summary>The track a player should select when nothing else is chosen.</summary>
    Default = 1,

    /// <summary>The track carries content that must be shown whatever the viewer selected.</summary>
    Forced = 2,

    /// <summary>The track is written for viewers who are deaf or hard of hearing.</summary>
    HearingImpaired = 4,

    /// <summary>The track should be ignored unless an application asks for it by name.</summary>
    Disabled = 8,

    /// <summary>
    /// A video track that is NOT a picture: a monochrome stream of the alpha channel of the file's picture
    /// track, luma only, full range, in lock step with it - the same dimensions, bit depth, frame count,
    /// timestamps and key-frame positions. Unless <see cref="PremultipliedAlpha" /> is also set, the picture
    /// track's colours are STRAIGHT (not multiplied by this alpha). A file with this flag on any track is
    /// version 1 or later.
    /// </summary>
    AlphaPlane = 16,

    /// <summary>
    /// On an <see cref="AlphaPlane" /> track: the picture track's colours have already been multiplied by
    /// this alpha. Reserved so the format can record the fact; the authoring library never sets it, and the
    /// player treats the colours as straight unless it is set.
    /// </summary>
    PremultipliedAlpha = 32,
}
