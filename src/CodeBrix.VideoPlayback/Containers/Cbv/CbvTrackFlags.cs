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
}
