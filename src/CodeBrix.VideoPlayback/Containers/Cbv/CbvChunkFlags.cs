using System;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The flags on one chunk, and on the index entry that points at it.
/// </summary>
[Flags]
public enum CbvChunkFlags
{
    /// <summary>Nothing is claimed.</summary>
    None = 0,

    /// <summary>Decoding may start at this chunk without having seen any earlier one.</summary>
    KeyFrame = 1,
}
