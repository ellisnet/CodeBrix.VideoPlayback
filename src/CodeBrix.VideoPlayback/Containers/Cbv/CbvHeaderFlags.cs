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
}
