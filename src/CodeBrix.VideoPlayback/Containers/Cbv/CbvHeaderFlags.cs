using System;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The flags in a bespoke file's fixed header.
/// </summary>
[Flags]
public enum CbvHeaderFlags
{
    /// <summary>Nothing is claimed.</summary>
    None = 0,

    /// <summary>
    /// The file carries an index covering every chunk. A file without one can only be played from the
    /// beginning; this library always writes one.
    /// </summary>
    HasIndex = 1,

    /// <summary>
    /// The chunks are stored in ascending presentation order across all tracks, which is what lets a reader
    /// play the file forwards without seeking about.
    /// </summary>
    ChunksInPresentationOrder = 2,

    /// <summary>
    /// The file is SILENT BY INTENT: it carries no audio track because its author said it has no sound, not
    /// because the sound was left out. A version-1 feature (<see cref="CbvFormat.MasterVersion" />): a file with
    /// this flag is stamped version 1 and carries no audio track. A file with no audio track and no flag is
    /// silent by absence; consumers treat the two alike.
    /// </summary>
    Silent = 4,
}
