using System;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// One entry in a Matroska file's index: a moment, a track, and where in the file the frame for that moment
/// is.
/// </summary>
/// <remarks>
/// The index is what makes seeking possible. Without it a reader has to walk the file from the beginning,
/// which is why the WebM guidelines ask for the whole index to sit before the first cluster - a player can
/// then index a file over a network with one read of the head.
/// </remarks>
public sealed class MatroskaCuePoint
{
    /// <summary>Creates a cue point.</summary>
    /// <param name="time">The moment this entry indexes.</param>
    /// <param name="trackId">The track the entry belongs to.</param>
    /// <param name="clusterOffset">The absolute offset of the cluster holding the frame.</param>
    /// <param name="relativeOffset">
    /// Where the block is inside the cluster's payload, or -1 when the file did not say.
    /// </param>
    /// <param name="duration">How long the indexed frame lasts, or <see cref="TimeSpan.Zero" /> when unstated.</param>
    public MatroskaCuePoint(TimeSpan time, int trackId, long clusterOffset, long relativeOffset, TimeSpan duration)
    {
        Time = time;
        TrackId = trackId;
        ClusterOffset = clusterOffset;
        RelativeOffset = relativeOffset;
        Duration = duration;
    }

    /// <summary>The moment this entry indexes.</summary>
    public TimeSpan Time { get; }

    /// <summary>The track the entry belongs to.</summary>
    public int TrackId { get; }

    /// <summary>
    /// The ABSOLUTE offset of the cluster holding the frame. The file stores it relative to the start of the
    /// Segment's payload; this property has that base already added, so it can be assigned to a source's
    /// position as it stands.
    /// </summary>
    public long ClusterOffset { get; }

    /// <summary>Where the block is inside the cluster's payload, or -1 when the file did not say.</summary>
    public long RelativeOffset { get; }

    /// <summary>How long the indexed frame lasts, or <see cref="TimeSpan.Zero" /> when the file did not say.</summary>
    public TimeSpan Duration { get; }

    /// <inheritdoc />
    public override string ToString() => $"cue at {Time} for track {TrackId}, cluster at {ClusterOffset}";
}
