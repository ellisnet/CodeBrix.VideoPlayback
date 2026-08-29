using System;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// One row of a bespoke file's index: which track a chunk belongs to, where it is, how big it is, and when
/// it is for.
/// </summary>
/// <remarks>
/// The index sits BEFORE the chunks, so opening a file - even over a network - reads the header and the whole
/// index in one go, and every seek after that is arithmetic rather than searching.
/// </remarks>
public readonly struct CbvIndexEntry
{
    /// <summary>Creates an index entry.</summary>
    /// <param name="trackId">The track the chunk belongs to.</param>
    /// <param name="flags">The chunk's flags.</param>
    /// <param name="size">The size of the chunk's payload in bytes.</param>
    /// <param name="offset">The absolute file offset of the chunk's HEADER.</param>
    /// <param name="timestampTicks">When the chunk is for, in the file's timescale.</param>
    public CbvIndexEntry(byte trackId, CbvChunkFlags flags, uint size, ulong offset, long timestampTicks)
    {
        TrackId = trackId;
        Flags = flags;
        Size = size;
        Offset = offset;
        TimestampTicks = timestampTicks;
    }

    /// <summary>The track the chunk belongs to.</summary>
    public byte TrackId { get; }

    /// <summary>The chunk's flags.</summary>
    public CbvChunkFlags Flags { get; }

    /// <summary>The size of the chunk's payload in bytes, not counting the chunk header.</summary>
    public uint Size { get; }

    /// <summary>The absolute file offset of the chunk's header.</summary>
    public ulong Offset { get; }

    /// <summary>When the chunk is for, counted in the file's timescale.</summary>
    public long TimestampTicks { get; }

    /// <summary>True when decoding may start at this chunk.</summary>
    public bool IsKeyFrame => (Flags & CbvChunkFlags.KeyFrame) != 0;

    /// <inheritdoc />
    public override string ToString() =>
        $"track {TrackId} at {TimestampTicks} ticks: {Size} bytes at offset {Offset}"
        + (IsKeyFrame ? " (key)" : string.Empty);
}
