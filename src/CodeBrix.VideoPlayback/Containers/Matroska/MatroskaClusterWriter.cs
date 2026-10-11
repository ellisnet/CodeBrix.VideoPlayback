using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Containers.Ebml;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>Where one finished cluster sits in the first pass's temporary file.</summary>
internal readonly struct MatroskaClusterRecord
{
    internal MatroskaClusterRecord(long offset, long length, long timestampMs)
    {
        Offset = offset;
        Length = length;
        TimestampMs = timestampMs;
    }

    /// <summary>The cluster's offset from the first cluster.</summary>
    internal long Offset { get; }

    /// <summary>The whole cluster's length, header included.</summary>
    internal long Length { get; }

    /// <summary>The cluster's timestamp, in milliseconds.</summary>
    internal long TimestampMs { get; }
}

/// <summary>One <c>CueTrackPositions</c> on its way into the index.</summary>
internal readonly struct MatroskaCueEntry
{
    internal MatroskaCueEntry(long timeMs, int track, int clusterIndex, long relativePosition, long durationMs)
    {
        TimeMs = timeMs;
        Track = track;
        ClusterIndex = clusterIndex;
        RelativePosition = relativePosition;
        DurationMs = durationMs;
    }

    /// <summary>The indexed block's timestamp, in milliseconds.</summary>
    internal long TimeMs { get; }

    /// <summary>The indexed block's track number.</summary>
    internal int Track { get; }

    /// <summary>Which cluster the block is in.</summary>
    internal int ClusterIndex { get; }

    /// <summary>The block's offset from the first byte of its cluster's payload.</summary>
    internal long RelativePosition { get; }

    /// <summary>The block's duration in milliseconds, or -1 when it states none.</summary>
    internal long DurationMs { get; }
}

/// <summary>
/// The first pass: lays blocks into clusters and writes the clusters, back to back, to a temporary file,
/// remembering where every cluster and every indexed block landed.
/// </summary>
/// <remarks>
/// A cluster's bytes depend only on its own blocks - every position inside it is relative - so a cluster written
/// here is copied into the finished file unchanged; only the index and the seek head, which state absolute
/// positions, are worked out in the second pass.
/// </remarks>
internal sealed class MatroskaClusterWriter
{
    private readonly Stream output;
    private readonly EbmlWriter content = new EbmlWriter(64 * 1024);
    private readonly EbmlWriter scratch = new EbmlWriter(64);
    private readonly MatroskaMuxTrack cuedTrack;
    private readonly List<MatroskaClusterRecord> clusters = new List<MatroskaClusterRecord>();
    private readonly List<MatroskaCueEntry> cueEntries = new List<MatroskaCueEntry>();

    private bool open;
    private bool clusterHasVideo;
    private long clusterTimestampMs;
    private long written;

    /// <summary>Creates the first-pass writer.</summary>
    /// <param name="output">The temporary file.</param>
    /// <param name="cuedTrack">
    /// The track that is indexed: the video, every key frame of which gets an entry, or - in a file with no video -
    /// the audio, whose first block in every cluster gets one.
    /// </param>
    internal MatroskaClusterWriter(Stream output, MatroskaMuxTrack cuedTrack)
    {
        this.output = output;
        this.cuedTrack = cuedTrack;
    }

    /// <summary>Every cluster written, in order.</summary>
    internal IReadOnlyList<MatroskaClusterRecord> Clusters => clusters;

    /// <summary>Every index entry, in the order the blocks were written.</summary>
    internal IReadOnlyList<MatroskaCueEntry> CueEntries => cueEntries;

    /// <summary>How many bytes the clusters occupy in total.</summary>
    internal long ClusterBytes => written;

    /// <summary>Adds a block, starting a new cluster first when the cluster rule says so.</summary>
    /// <param name="block">The block.</param>
    internal void Add(MatroskaPendingBlock block)
    {
        bool isVideo = block.Track.Kind == MediaTrackKind.Video;

        // A key frame starts a cluster unless the cluster has no picture in it yet - the audio that precedes
        // the very first frame stays in the same cluster as that frame, so a seek to the start keeps it.
        bool startCluster = !open
            || (isVideo && block.IsKeyFrame && clusterHasVideo)
            || block.TimestampMs - clusterTimestampMs > short.MaxValue
            || block.TimestampMs - clusterTimestampMs >= MatroskaMuxer.MaximumClusterMilliseconds
            || content.Length >= MatroskaMuxer.MaximumClusterBytes;

        if (startCluster)
        {
            Flush();
            content.Clear();
            clusterTimestampMs = block.TimestampMs;
            content.WriteUnsigned(MatroskaIds.ClusterTimestamp, (ulong)clusterTimestampMs);
            open = true;
            clusterHasVideo = false;
        }

        if (isVideo) clusterHasVideo = true;

        long relativePosition = content.Length;
        WriteBlock(block, block.TimestampMs - clusterTimestampMs);
        block.Track.BlockCount++;

        int clusterIndex = clusters.Count;
        bool cued = ReferenceEquals(block.Track, cuedTrack)
            && block.IsKeyFrame
            && (isVideo || startCluster);

        if (cued)
        {
            cueEntries.Add(new MatroskaCueEntry(block.TimestampMs, block.Track.Number, clusterIndex, relativePosition, -1));
        }
        else if (block.Track.Kind == MediaTrackKind.Caption)
        {
            cueEntries.Add(new MatroskaCueEntry(
                block.TimestampMs,
                block.Track.Number,
                clusterIndex,
                relativePosition,
                block.DurationMs));
        }
    }

    /// <summary>Writes out the cluster in progress, if there is one.</summary>
    internal void Flush()
    {
        if (!open) return;

        scratch.Clear();
        scratch.WriteHeader(MatroskaIds.Cluster, content.Length);

        long length = scratch.Length + content.Length;
        clusters.Add(new MatroskaClusterRecord(written, length, clusterTimestampMs));

        scratch.CopyTo(output);
        content.CopyTo(output);
        written += length;
        open = false;
    }

    private void WriteBlock(MatroskaPendingBlock block, long relativeMs)
    {
        int trackLength = EbmlWriter.MeasureSize(block.Track.Number);
        long payloadLength = trackLength + 3L + block.Data.Length;

        if (!block.NeedsBlockGroup)
        {
            content.WriteHeader(MatroskaIds.SimpleBlock, payloadLength);
            WriteBlockPayload(block, relativeMs, block.IsKeyFrame ? (byte)0x80 : (byte)0x00);
            return;
        }

        scratch.Clear();
        if (block.DurationMs >= 0) scratch.WriteUnsigned(MatroskaIds.BlockDuration, (ulong)block.DurationMs);
        if (block.DiscardPaddingNs != 0) scratch.WriteSigned(MatroskaIds.DiscardPadding, block.DiscardPaddingNs);

        long groupLength = EbmlWriter.MeasureElement(MatroskaIds.Block, payloadLength) + scratch.Length;
        content.WriteHeader(MatroskaIds.BlockGroup, groupLength);
        content.WriteHeader(MatroskaIds.Block, payloadLength);

        // A Block has no key-frame bit; a BlockGroup without a ReferenceBlock is a key frame by definition.
        WriteBlockPayload(block, relativeMs, 0x00);
        content.WriteRaw(scratch.WrittenSpan);
    }

    private void WriteBlockPayload(MatroskaPendingBlock block, long relativeMs, byte flags)
    {
        content.WriteSize(block.Track.Number);
        content.WriteByte((byte)((relativeMs >> 8) & 0xFF));
        content.WriteByte((byte)(relativeMs & 0xFF));
        content.WriteByte(flags);
        content.WriteRaw(block.Data);
    }
}
