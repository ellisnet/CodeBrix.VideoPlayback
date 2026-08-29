using System;
using System.Collections.Generic;
using System.Text;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// Reads Matroska and WebM files: their tracks, their index, their chapters, their captions, and the frames
/// in their clusters.
/// </summary>
/// <remarks>
/// <para>
/// Written from RFC 9559, RFC 8794 and the WebM container guidelines, plus the published mappings that say
/// how AV1, Opus and Vorbis sit inside Matroska. No code was taken from any existing demultiplexer.
/// </para>
/// <para>
/// <b>What it accepts.</b> <c>V_AV1</c> and <c>V_UNCOMPRESSED</c> video, <c>A_OPUS</c> and <c>A_VORBIS</c>
/// audio, and text captions in WebVTT, SubRip or Advanced SubStation form. A video or audio track in any other
/// codec is refused with a message naming the codec, because a file whose picture or sound cannot be decoded
/// is not playable and saying so at once is kinder than failing later. A SUBTITLE track this library cannot
/// read - a bitmap format, say - is skipped with a line in <see cref="Notices" /> instead, because the film
/// still plays without it.
/// </para>
/// <para>
/// <b>What it refuses outright.</b> A track with <c>ContentEncodings</c>: its frames have been compressed or
/// had their headers stripped, and handing them to a decoder unaltered would produce nonsense.
/// </para>
/// <para>
/// <b>Reading order.</b> The reader hands out packets in the order the file stores them, interleaved across
/// tracks, which is the order a player wants. Caption blocks are turned into cues as they go by, so a caption
/// track's <see cref="CaptionTrack.Cues" /> fills up during playback and
/// <see cref="CaptionTrack.AreCuesComplete" /> only becomes true once the end of the file has been reached.
/// </para>
/// <para>Used from one thread, like the source underneath it.</para>
/// </remarks>
public sealed class MatroskaReader : IMediaContainerReader
{
    private const long DefaultTimestampScaleNs = 1_000_000;
    private const int TrackTypeVideo = 1;
    private const int TrackTypeAudio = 2;
    private const int TrackTypeSubtitle = 17;
    private const int MaxSupportedDocTypeReadVersion = 4;

    private static readonly TimeSpan FallbackCueDuration = TimeSpan.FromSeconds(5);

    private readonly IMediaSource source;
    private readonly EbmlReader ebml;
    private readonly List<MediaTrackInfo> tracks = new List<MediaTrackInfo>();
    private readonly Dictionary<int, long> lastTimestampByTrack = new Dictionary<int, long>();
    private readonly List<CaptionTrack> captionTracks = new List<CaptionTrack>();
    private readonly List<Chapter> chapters = new List<Chapter>();
    private readonly List<string> notices = new List<string>();
    private readonly List<MatroskaCuePoint> cues = new List<MatroskaCuePoint>();
    private readonly Dictionary<int, TrackState> trackStates = new Dictionary<int, TrackState>();

    private long timestampScaleNs = DefaultTimestampScaleNs;
    private double durationInScaleUnits;
    private long segmentDataOffset = -1;
    private long segmentDataEnd = -1;
    private long firstClusterOffset = -1;
    private long cuesOffset = -1;
    private bool clusterHadUnknownSize;
    private EbmlElementHeader pendingCluster;
    private bool hasPendingCluster;

    private long clusterEnd = -1;
    private long clusterTimestampUnits;
    private long nextTopLevelOffset = -1;
    private bool reachedEnd;
    private bool capturedAllCaptions;

    private byte[] blockBuffer = Array.Empty<byte>();
    private byte[] additionBuffer = Array.Empty<byte>();
    private int[] frameOffsets = new int[16];
    private int[] frameLengths = new int[16];
    private int frameCount;
    private int frameIndex;
    private TrackState blockTrack;
    private long blockTimestampUnits;
    private TimeSpan blockDuration;
    private TimeSpan blockDiscardPadding;
    private bool blockIsKeyFrame;
    private int blockAdditionLength;
    private bool disposed;

    /// <summary>Opens a Matroska or WebM file.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this reader is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The file is not Matroska or WebM, is malformed, or carries a codec this library does not read.
    /// </exception>
    public MatroskaReader(IMediaSource source, bool leaveSourceOpen = false)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        this.source = source;
        ebml = new EbmlReader(source, leaveSourceOpen: true);
        LeaveSourceOpen = leaveSourceOpen;

        try
        {
            ReadEbmlHeader();
            ReadSegment();
        }
        catch
        {
            ebml.Dispose();
            if (!leaveSourceOpen) source.Dispose();
            throw;
        }
    }

    /// <summary>True when the source is left open by <see cref="Dispose" />.</summary>
    public bool LeaveSourceOpen { get; }

    /// <summary>Reports whether a run of bytes begins with the EBML signature every Matroska file starts with.</summary>
    /// <param name="firstBytes">The first bytes of a file; four are enough.</param>
    /// <returns>True when the bytes begin <c>1A 45 DF A3</c>.</returns>
    public static bool IsMatroska(ReadOnlySpan<byte> firstBytes) =>
        firstBytes.Length >= 4
        && firstBytes[0] == 0x1A && firstBytes[1] == 0x45 && firstBytes[2] == 0xDF && firstBytes[3] == 0xA3;

    /// <inheritdoc />
    public string FormatName => "Matroska/WebM";

    /// <summary>The document type the file declares: "matroska" or "webm". Never null.</summary>
    public string DocType { get; private set; } = string.Empty;

    /// <summary>The document-type version the writer used.</summary>
    public int DocTypeVersion { get; private set; }

    /// <summary>The minimum document-type version a reader needs.</summary>
    public int DocTypeReadVersion { get; private set; }

    /// <summary>The media's title, or an empty string when the file has none. Never null.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The library that laid the file out, or an empty string. Never null.</summary>
    public string MuxingApp { get; private set; } = string.Empty;

    /// <summary>The application that produced the file, or an empty string. Never null.</summary>
    public string WritingApp { get; private set; } = string.Empty;

    /// <summary>
    /// How many nanoseconds one unit of the file's timestamps represents. One million - a millisecond - is
    /// usual, but an audio-only file often uses one sample period instead, so this must be read rather than
    /// assumed.
    /// </summary>
    public long TimestampScale => timestampScaleNs;

    /// <summary>The absolute offset of the first byte of the Segment's payload, which every stored position counts from.</summary>
    public long SegmentDataOffset => segmentDataOffset;

    /// <summary>The absolute offset just past the Segment's payload.</summary>
    public long SegmentDataEnd => segmentDataEnd;

    /// <summary>The absolute offset of the first cluster, or -1 when the file has none.</summary>
    public long FirstClusterOffset => firstClusterOffset;

    /// <summary>The file's index, ordered by time. Empty when the file carries no <c>Cues</c> element.</summary>
    public IReadOnlyList<MatroskaCuePoint> Cues => cues;

    /// <summary>True when the file carries an index.</summary>
    public bool HasIndex => cues.Count > 0;

    /// <summary>
    /// True when the whole index sits before the first cluster, which is what lets a player index a file over
    /// a network without reading to the end. The WebM guidelines ask for this.
    /// </summary>
    public bool CuesPrecedeFirstCluster { get; private set; }

    /// <summary>How many clusters the reader has entered so far.</summary>
    public int ClustersRead { get; private set; }

    /// <summary>
    /// True when any element in the file declared an unknown size, which the streamable WebM profile forbids.
    /// </summary>
    /// <remarks>
    /// A writer that does not know an element's length until it has finished writing it - a live recorder -
    /// leaves the size field as all ones and lets the element end where the next one begins. That is legal
    /// Matroska and this reader handles it, but a file meant to be served over a network and indexed from its
    /// head should not contain any. The value reflects everything read so far, so it is settled for the header
    /// and metadata as soon as the file is opened, and can only become true later if a cluster turns out to be
    /// unsized as well.
    /// </remarks>
    public bool HasUnknownSizeElements => ebml.UnknownSizeElementCount > 0;

    /// <summary>How many elements so far declared an unknown size. See <see cref="HasUnknownSizeElements" />.</summary>
    public int UnknownSizeElementCount => ebml.UnknownSizeElementCount;

    /// <summary>
    /// True when the <c>Info</c> element actually carried a <c>Duration</c>, as opposed to the media simply
    /// lasting no time at all.
    /// </summary>
    /// <remarks>
    /// <see cref="Duration" /> reads <see cref="TimeSpan.Zero" /> in both cases, so a caller that has to tell
    /// "the file does not say" from "the file says zero" - a profile check, for instance - asks this instead.
    /// </remarks>
    public bool HasDeclaredDuration => durationInScaleUnits > 0;

    /// <summary>
    /// True to check the <c>CRC-32</c> on each cluster as it is read. Defaults to FALSE.
    /// </summary>
    /// <remarks>
    /// Checking a cluster means reading it a second time, which doubles the work of playing a file, so a
    /// player leaves this off and a diagnostics tool turns it on. The checksums on the small metadata
    /// elements - the track list, the index, the chapters - are always checked, because those are read once
    /// and are what the rest of the file is interpreted through.
    /// </remarks>
    public bool VerifyClusterChecksums { get; set; }

    /// <inheritdoc />
    public TimeSpan Duration =>
        durationInScaleUnits <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)Math.Round(durationInScaleUnits * timestampScaleNs / 100.0));

    /// <inheritdoc />
    /// <remarks>
    /// True whenever the source can seek. With an index a seek is a single jump; WITHOUT one - see
    /// <see cref="HasIndex" /> - the reader walks the file's clusters reading only their headers, which is
    /// still far cheaper than decoding but is not free on a long recording.
    /// </remarks>
    public bool CanSeek => source.CanSeek && firstClusterOffset >= 0;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrackInfo> Tracks => tracks;

    /// <inheritdoc />
    public IReadOnlyList<CaptionTrack> CaptionTracks => captionTracks;

    /// <inheritdoc />
    public IReadOnlyList<Chapter> Chapters => chapters;

    /// <inheritdoc />
    public IReadOnlyList<string> Notices => notices;

    /// <inheritdoc />
    public bool TryReadPacket(out MediaPacket packet)
    {
        ThrowIfDisposed();

        while (true)
        {
            if (frameIndex < frameCount)
            {
                packet = BuildPacket();
                frameIndex++;
                lastTimestampByTrack[packet.TrackId] = packet.Timestamp.Ticks;
                return true;
            }

            if (!AdvanceToNextBlock())
            {
                MarkCaptionsComplete();
                packet = default;
                return false;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A BOUND, NOT AN EARLY ANSWER: this reader reports a track exhausted only once it has read every
    /// cluster in the segment, at which point the answer is exact for every track at once. A track the file
    /// does not declare reads as exhausted immediately, because nothing will ever arrive for it.
    /// </para>
    /// <para>
    /// <b>Why it cannot do better, and why guessing would be wrong.</b> Nothing in Matroska records where a
    /// track stops. Cues are the obvious candidate and they do not answer this question: a cue point marks a
    /// KEY FRAME, so the last cue for a track is followed by however many non-key frames the encoder chose,
    /// and in practice a WebM file's cues index the video track alone and say nothing whatever about the
    /// audio. Cues CAN refute exhaustion - a cue for a track beyond the read position proves the track
    /// continues - but refuting is not proving, and a reader that treated "past the last cue and quiet since"
    /// as an ending would truncate ordinary files. So this reader waits until it is certain.
    /// </para>
    /// <para>
    /// The consequence is that a Matroska file whose audio ends before its picture keeps the session waiting
    /// for the audio's end until the whole file has been demultiplexed. That is why the session's
    /// demultiplexer is built so that it can always REACH the end of a file - see the parking design in
    /// MAINTAINER-README.txt - rather than relying on this answer arriving early.
    /// </para>
    /// </remarks>
    public bool IsTrackExhausted(int trackId)
    {
        ThrowIfDisposed();

        if (reachedEnd) return true;

        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == trackId) return false;
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Null until every cluster has been read, for the reason given on
    /// <see cref="IsTrackExhausted" />; afterwards it is the timestamp of the last packet this reader
    /// actually produced for the track since it was last positioned, which is exact for a file played from
    /// the beginning.
    /// </remarks>
    public TimeSpan? GetTrackEndTimestamp(int trackId)
    {
        ThrowIfDisposed();

        if (!reachedEnd) return null;
        if (!lastTimestampByTrack.TryGetValue(trackId, out long ticks)) return null;
        return TimeSpan.FromTicks(ticks);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The reader lands on the START OF A CLUSTER, not on an individual frame, and returns that cluster's own
    /// timestamp - so the value that comes back is genuinely where the reader now is, and the caller decodes
    /// and discards from there to reach the moment it wanted.
    /// </para>
    /// <para>
    /// Landing on the cluster rather than on the indexed frame is deliberate. An index entry points at ONE
    /// track's frame; jumping straight to it would step over the other tracks' frames earlier in the same
    /// cluster, and a player that lost the audio either side of every seek would be a poor one. Two entries
    /// pointing INTO the same cluster at different offsets are common - the audio-only files in this library's
    /// own test corpus do exactly that - so a reader that reported the index time while starting at the cluster
    /// would be reporting a position it had not reached.
    /// </para>
    /// </remarks>
    public TimeSpan Seek(TimeSpan position, int keyFrameTrackId)
    {
        ThrowIfDisposed();

        if (!source.CanSeek)
        {
            throw new NotSupportedException(
                $"'{source.Name}' cannot seek: the source only reads forwards.");
        }

        if (firstClusterOffset < 0)
        {
            throw new NotSupportedException(
                $"'{source.Name}' has no clusters, so there is nothing to seek within.");
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        ResetBlockState();
        reachedEnd = false;
        lastTimestampByTrack.Clear();

        if (cues.Count > 0)
        {
            MatroskaCuePoint target = FindCue(position, keyFrameTrackId);
            if (target != null) return EnterClusterAt(target.ClusterOffset);
        }

        return SeekByScanning(position);
    }

    /// <summary>Releases the reader and, unless it was told otherwise, the source underneath it.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ebml.Dispose();
        if (!LeaveSourceOpen) source.Dispose();
    }

    private MediaPacket BuildPacket()
    {
        int offset = frameOffsets[frameIndex];
        int length = frameLengths[frameIndex];

        TimeSpan timestamp = FrameTimestamp(frameIndex);
        TimeSpan duration = FrameDuration();
        TimeSpan discard = frameIndex == frameCount - 1 ? blockDiscardPadding : TimeSpan.Zero;

        if (blockTrack.Captions != null)
        {
            AddCaptionCue(blockTrack, blockBuffer.AsSpan(offset, length), timestamp, duration);
        }

        return new MediaPacket(
            blockTrack.Info.Id,
            blockBuffer.AsMemory(offset, length),
            timestamp,
            duration,
            blockIsKeyFrame,
            discard);
    }

    private TimeSpan FrameTimestamp(int index)
    {
        long baseTicks = UnitsToTicks(blockTimestampUnits);
        if (index == 0 || frameCount <= 1) return TimeSpan.FromTicks(baseTicks);

        long stepTicks = FrameStepTicks();
        return TimeSpan.FromTicks(baseTicks + (stepTicks * index));
    }

    private TimeSpan FrameDuration()
    {
        if (frameCount <= 1) return blockDuration;

        long step = FrameStepTicks();
        return step > 0 ? TimeSpan.FromTicks(step) : TimeSpan.Zero;
    }

    private long FrameStepTicks()
    {
        if (blockTrack != null && blockTrack.DefaultDurationTicks > 0) return blockTrack.DefaultDurationTicks;
        if (blockDuration > TimeSpan.Zero && frameCount > 0) return blockDuration.Ticks / frameCount;
        return 0;
    }

    private long UnitsToTicks(long units) => (long)Math.Round(units * (double)timestampScaleNs / 100.0);

    private void ResetBlockState()
    {
        frameCount = 0;
        frameIndex = 0;
        blockTrack = null;
        clusterEnd = -1;
        clusterTimestampUnits = 0;
        blockAdditionLength = 0;
        nextTopLevelOffset = -1;
    }

    private void MarkCaptionsComplete()
    {
        if (capturedAllCaptions) return;
        capturedAllCaptions = true;
        foreach (CaptionTrack track in captionTracks) track.AreCuesComplete = true;
    }

    // ---------------------------------------------------------------- header and segment

    private void ReadEbmlHeader()
    {
        // The first element IS the signature, so it is read as an element rather than sniffed and rewound;
        // that way a forward-only source - a progressive download - can be opened too.
        long bound = ebml.SourceEnd;
        EbmlElementHeader header;
        bool readHeader;
        try
        {
            readHeader = ebml.TryReadElementHeader(bound, out header);
        }
        catch (VideoPlaybackException)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' does not begin with the EBML signature 1A 45 DF A3, so it is not a Matroska or "
                + "WebM file.");
        }

        if (!readHeader || header.Id != EbmlIds.EbmlHeader)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' does not begin with the EBML signature 1A 45 DF A3, so it is not a Matroska or "
                + "WebM file.");
        }

        long end = header.EndOffset;
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case EbmlIds.DocType:
                    DocType = ebml.ReadString(child);
                    break;
                case EbmlIds.DocTypeVersion:
                    DocTypeVersion = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case EbmlIds.DocTypeReadVersion:
                    DocTypeReadVersion = (int)ebml.ReadUnsignedInteger(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }

        bool known = string.Equals(DocType, "matroska", StringComparison.OrdinalIgnoreCase)
            || string.Equals(DocType, "webm", StringComparison.OrdinalIgnoreCase);

        if (!known)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' is an EBML file whose DocType is '{DocType}'. This library reads 'matroska' and "
                + "'webm' documents.");
        }

        if (DocTypeReadVersion > MaxSupportedDocTypeReadVersion)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares DocType '{DocType}' read version {DocTypeReadVersion}, and this library "
                + $"reads up to version {MaxSupportedDocTypeReadVersion}.");
        }

        SeekTo(end);
    }

    private void ReadSegment()
    {
        long bound = ebml.SourceEnd;

        EbmlElementHeader segment = default;
        bool found = false;
        while (ebml.TryReadElementHeader(bound, out EbmlElementHeader header))
        {
            if (header.Id == MatroskaIds.Segment)
            {
                segment = header;
                found = true;
                break;
            }

            ebml.SkipElement(header);
        }

        if (!found)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has an EBML header but no Segment element, so it carries no media.");
        }

        segmentDataOffset = segment.DataOffset;
        segmentDataEnd = segment.IsUnknownSize ? bound : Math.Min(segment.EndOffset, bound);
        if (segmentDataEnd < segmentDataOffset) segmentDataEnd = bound;

        bool haveInfo = false;
        bool haveTracks = false;

        if (source.CanSeek)
        {
            HashSet<long> seekTargets = ReadSeekHeads();
            foreach (long offset in seekTargets)
            {
                if (offset < segmentDataOffset || offset >= segmentDataEnd) continue;
                SeekTo(offset);
                if (!ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element)) continue;

                if (ReadTopLevelElement(element, ref haveInfo, ref haveTracks)) continue;
                ebml.SkipElement(element);
            }
        }

        if (!haveInfo || !haveTracks)
        {
            ScanTopLevelElements(ref haveInfo, ref haveTracks);
        }

        if (firstClusterOffset < 0) FindFirstCluster();

        if (!haveTracks)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has no Tracks element, so there is nothing in it to play.");
        }

        cues.Sort((a, b) => a.Time.CompareTo(b.Time));
        CuesPrecedeFirstCluster = cues.Count > 0 && cuesOffset >= 0 && firstClusterOffset >= 0
            && cuesOffset < firstClusterOffset;

        if (firstClusterOffset >= 0 && !hasPendingCluster) SeekTo(firstClusterOffset);
        nextTopLevelOffset = firstClusterOffset;
    }

    private HashSet<long> ReadSeekHeads()
    {
        HashSet<long> targets = new HashSet<long>();
        Queue<long> pending = new Queue<long>();
        pending.Enqueue(segmentDataOffset);

        int guard = 0;
        while (pending.Count > 0 && guard++ < 8)
        {
            long at = pending.Dequeue();
            if (at < segmentDataOffset || at >= segmentDataEnd) continue;

            SeekTo(at);
            if (!ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader header)) continue;
            if (header.Id != MatroskaIds.SeekHead)
            {
                if (at != segmentDataOffset) continue;

                // The first top-level element is not a SeekHead; there is no index of elements to follow.
                continue;
            }

            if (header.IsUnknownSize) continue;

            long end = header.EndOffset;
            while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
            {
                if (child.Id != MatroskaIds.Seek)
                {
                    ebml.SkipElement(child);
                    continue;
                }

                uint seekId = 0;
                long seekPosition = -1;
                long seekEnd = child.EndOffset;
                while (ebml.TryReadElementHeader(seekEnd, out EbmlElementHeader entry))
                {
                    switch (entry.Id)
                    {
                        case MatroskaIds.SeekId:
                        {
                            ReadOnlyMemory<byte> raw = ebml.ReadBinaryShared(entry);
                            uint value = 0;
                            ReadOnlySpan<byte> bytes = raw.Span;
                            for (int i = 0; i < bytes.Length && i < 4; i++) value = (value << 8) | bytes[i];
                            seekId = value;
                            break;
                        }

                        case MatroskaIds.SeekPosition:
                            seekPosition = (long)ebml.ReadUnsignedInteger(entry);
                            break;
                        default:
                            ebml.SkipElement(entry);
                            break;
                    }
                }

                if (seekPosition < 0) continue;
                long absolute = segmentDataOffset + seekPosition;

                if (seekId == MatroskaIds.SeekHead)
                {
                    pending.Enqueue(absolute);
                    continue;
                }

                if (seekId == MatroskaIds.Info || seekId == MatroskaIds.Tracks || seekId == MatroskaIds.Cues
                    || seekId == MatroskaIds.Chapters || seekId == MatroskaIds.Cluster)
                {
                    targets.Add(absolute);
                }
            }
        }

        return targets;
    }

    private void ScanTopLevelElements(ref bool haveInfo, ref bool haveTracks)
    {
        SeekTo(segmentDataOffset);

        while (ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element))
        {
            if (element.Id == MatroskaIds.Cluster)
            {
                if (firstClusterOffset < 0) firstClusterOffset = element.Offset;

                if (!source.CanSeek)
                {
                    // Stepping over a cluster on a forward-only source would throw the frames away. Hold on
                    // to the header instead and let playback start from it.
                    pendingCluster = element;
                    hasPendingCluster = true;
                    return;
                }

                if (element.IsUnknownSize) return;
                ebml.SkipElement(element);
                continue;
            }

            if (ReadTopLevelElement(element, ref haveInfo, ref haveTracks)) continue;

            if (element.IsUnknownSize) return;
            ebml.SkipElement(element);
        }
    }

    private void FindFirstCluster()
    {
        SeekTo(segmentDataOffset);

        while (ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element))
        {
            if (element.Id == MatroskaIds.Cluster)
            {
                firstClusterOffset = element.Offset;
                return;
            }

            if (element.IsUnknownSize) return;
            ebml.SkipElement(element);
        }
    }

    private bool ReadTopLevelElement(in EbmlElementHeader element, ref bool haveInfo, ref bool haveTracks)
    {
        switch (element.Id)
        {
            case MatroskaIds.Info when !haveInfo:
                ReadInfo(element);
                haveInfo = true;
                return true;
            case MatroskaIds.Tracks when !haveTracks:
                ReadTracks(element);
                haveTracks = true;
                return true;
            case MatroskaIds.Cues when cues.Count == 0:
                cuesOffset = element.Offset;
                ReadCues(element);
                return true;
            case MatroskaIds.Chapters when chapters.Count == 0:
                ReadChapters(element);
                return true;
            case MatroskaIds.Cluster:
                if (firstClusterOffset < 0) firstClusterOffset = element.Offset;
                return false;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- metadata

    private void ReadInfo(in EbmlElementHeader info)
    {
        long end = info.EndOffset;
        SeekTo(info.DataOffset);

        bool first = true;
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (first && child.Id == EbmlIds.Crc32)
            {
                ebml.VerifyMasterChecksum(info, child, "Info");
                ebml.SkipElement(child);
                first = false;
                continue;
            }

            first = false;
            switch (child.Id)
            {
                case MatroskaIds.TimestampScale:
                {
                    long scale = (long)ebml.ReadUnsignedInteger(child);
                    if (scale > 0) timestampScaleNs = scale;
                    break;
                }

                case MatroskaIds.Duration:
                    durationInScaleUnits = ebml.ReadFloat(child);
                    break;
                case MatroskaIds.Title:
                    Title = ebml.ReadString(child);
                    break;
                case MatroskaIds.MuxingApp:
                    MuxingApp = ebml.ReadString(child);
                    break;
                case MatroskaIds.WritingApp:
                    WritingApp = ebml.ReadString(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }
    }

    private void ReadTracks(in EbmlElementHeader element)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        bool first = true;
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (first && child.Id == EbmlIds.Crc32)
            {
                ebml.VerifyMasterChecksum(element, child, "Tracks");
                ebml.SkipElement(child);
                first = false;
                continue;
            }

            first = false;
            if (child.Id != MatroskaIds.TrackEntry)
            {
                ebml.SkipElement(child);
                continue;
            }

            ReadTrackEntry(child);
        }
    }

    private void ReadTrackEntry(in EbmlElementHeader entry)
    {
        long end = entry.EndOffset;
        SeekTo(entry.DataOffset);

        int trackNumber = 0;
        int trackType = 0;
        string codecId = string.Empty;
        string name = string.Empty;
        // RFC 9559 gives Language a DEFAULT of "eng": a track that says nothing about its language is English
        // by specification. Writers almost always state "und" explicitly, which normalises to "no language".
        string language = "eng";
        string languageBcp47 = string.Empty;
        byte[] codecPrivate = Array.Empty<byte>();
        bool flagDefault = true;
        bool flagForced = false;
        bool flagHearingImpaired = false;
        bool flagEnabled = true;
        long defaultDurationNs = 0;
        long codecDelayNs = 0;
        long seekPreRollNs = 0;
        bool hasContentEncodings = false;

        VideoSettings video = new VideoSettings();
        AudioSettings audio = new AudioSettings();

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.TrackNumber:
                    trackNumber = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.TrackType:
                    trackType = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.CodecId:
                    codecId = ebml.ReadString(child);
                    break;
                case MatroskaIds.CodecPrivate:
                    codecPrivate = ebml.ReadBinary(child);
                    break;
                case MatroskaIds.Name:
                    name = ebml.ReadString(child);
                    break;
                case MatroskaIds.Language:
                    language = ebml.ReadString(child);
                    break;
                case MatroskaIds.LanguageBcp47:
                    languageBcp47 = ebml.ReadString(child);
                    break;
                case MatroskaIds.FlagDefault:
                    flagDefault = ebml.ReadUnsignedInteger(child) != 0;
                    break;
                case MatroskaIds.FlagForced:
                    flagForced = ebml.ReadUnsignedInteger(child) != 0;
                    break;
                case MatroskaIds.FlagHearingImpaired:
                    flagHearingImpaired = ebml.ReadUnsignedInteger(child) != 0;
                    break;
                case MatroskaIds.FlagEnabled:
                    flagEnabled = ebml.ReadUnsignedInteger(child) != 0;
                    break;
                case MatroskaIds.DefaultDuration:
                    defaultDurationNs = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.CodecDelay:
                    codecDelayNs = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.SeekPreRoll:
                    seekPreRollNs = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ContentEncodings:
                    hasContentEncodings = true;
                    ebml.SkipElement(child);
                    break;
                case MatroskaIds.Video:
                    ReadVideoSettings(child, ref video);
                    break;
                case MatroskaIds.Audio:
                    ReadAudioSettings(child, ref audio);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }

        if (hasContentEncodings)
        {
            throw new VideoPlaybackException(
                $"Track {trackNumber} of '{source.Name}' declares ContentEncodings, so its frames have been "
                + "compressed or had their headers stripped. This library reads only unaltered frames, so the file "
                + "cannot be played.");
        }

        string bcp47 = !string.IsNullOrEmpty(languageBcp47)
            ? LanguageTags.Normalize(languageBcp47)
            : LanguageTags.Normalize(language);

        MediaTrackInfo info = new MediaTrackInfo
        {
            Id = trackNumber,
            CodecId = string.Empty,
            Name = name,
            Language = bcp47,
            IsDefault = flagDefault,
            IsForced = flagForced,
            IsHearingImpaired = flagHearingImpaired,
            IsEnabled = flagEnabled,
            DefaultDuration = defaultDurationNs > 0 ? TimeSpan.FromTicks(defaultDurationNs / 100) : TimeSpan.Zero,
            CodecPrivate = codecPrivate,
        };

        TrackState state = new TrackState
        {
            Info = info,
            RawCodecId = codecId,
            DefaultDurationTicks = defaultDurationNs > 0 ? defaultDurationNs / 100 : 0,
        };

        switch (trackType)
        {
            case TrackTypeVideo:
                info.Kind = MediaTrackKind.Video;
                info.CodecId = MapVideoCodec(codecId, trackNumber);
                ApplyVideoSettings(info, video, codecId);
                break;

            case TrackTypeAudio:
                info.Kind = MediaTrackKind.Audio;
                info.CodecId = MapAudioCodec(codecId, trackNumber);
                info.SampleRate = (int)Math.Round(audio.OutputSamplingFrequency > 0
                    ? audio.OutputSamplingFrequency
                    : audio.SamplingFrequency);
                info.Channels = audio.Channels > 0 ? audio.Channels : 1;
                info.CodecDelay = TimeSpan.FromTicks(codecDelayNs / 100);
                info.SeekPreRoll = TimeSpan.FromTicks(seekPreRollNs / 100);
                info.PreSkipSamples = ReadOpusPreSkip(info.CodecId, codecPrivate);
                break;

            case TrackTypeSubtitle:
                if (!TryMapCaptionCodec(codecId, out string captionCodecId, out CaptionFormat format, out bool inline))
                {
                    notices.Add(
                        $"Subtitle track {trackNumber} is in the '{codecId}' format, which this library does not "
                        + "read; the track is ignored and everything else in the file plays normally.");
                    info.Kind = MediaTrackKind.Unknown;
                    info.CodecId = codecId;
                    break;
                }

                info.Kind = MediaTrackKind.Caption;
                info.CodecId = captionCodecId;
                info.CaptionFormat = format;
                state.InlineWebVtt = inline;

                CaptionTrackFlags flags = CaptionTrackFlags.None;
                if (flagDefault) flags |= CaptionTrackFlags.Default;
                if (flagForced) flags |= CaptionTrackFlags.Forced;
                if (flagHearingImpaired) flags |= CaptionTrackFlags.HearingImpaired;

                CaptionTrack captionTrack = new CaptionTrack(trackNumber, bcp47, name, flags, format);
                captionTracks.Add(captionTrack);
                state.Captions = captionTrack;
                break;

            default:
                info.Kind = MediaTrackKind.Unknown;
                info.CodecId = codecId;
                break;
        }

        tracks.Add(info);
        trackStates[trackNumber] = state;
    }

    private void ReadVideoSettings(in EbmlElementHeader element, ref VideoSettings video)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.PixelWidth:
                    video.PixelWidth = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.PixelHeight:
                    video.PixelHeight = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.PixelCropLeft:
                    video.CropLeft = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.PixelCropRight:
                    video.CropRight = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.PixelCropTop:
                    video.CropTop = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.PixelCropBottom:
                    video.CropBottom = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.DisplayWidth:
                    video.DisplayWidth = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.DisplayHeight:
                    video.DisplayHeight = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.DisplayUnit:
                    video.DisplayUnit = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ColourSpace:
                {
                    ReadOnlyMemory<byte> fourcc = ebml.ReadBinaryShared(child);
                    video.ColourSpace = fourcc.Length == 0
                        ? string.Empty
                        : Encoding.ASCII.GetString(fourcc.Span).TrimEnd('\0', ' ');
                    break;
                }

                case MatroskaIds.Colour:
                    ReadColour(child, ref video);
                    break;
                case MatroskaIds.Projection:
                    notices.Add(
                        "The video track carries a Projection element (rotation or a spherical layout). This "
                        + "library does not apply it, so the picture is shown exactly as it was stored.");
                    ebml.SkipElement(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }
    }

    private void ReadColour(in EbmlElementHeader element, ref VideoSettings video)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.MatrixCoefficients:
                    video.Matrix = (int)ebml.ReadUnsignedInteger(child);
                    video.HasMatrix = true;
                    break;
                case MatroskaIds.BitsPerChannel:
                    video.BitsPerChannel = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ChromaSubsamplingHorz:
                    video.ChromaSubsamplingHorz = (int)ebml.ReadUnsignedInteger(child);
                    video.HasChromaSubsampling = true;
                    break;
                case MatroskaIds.ChromaSubsamplingVert:
                    video.ChromaSubsamplingVert = (int)ebml.ReadUnsignedInteger(child);
                    video.HasChromaSubsampling = true;
                    break;
                case MatroskaIds.ChromaSitingHorz:
                    video.ChromaSitingHorz = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ChromaSitingVert:
                    video.ChromaSitingVert = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.Range:
                    video.Range = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.TransferCharacteristics:
                    video.Transfer = (int)ebml.ReadUnsignedInteger(child);
                    video.HasTransfer = true;
                    break;
                case MatroskaIds.Primaries:
                    video.Primaries = (int)ebml.ReadUnsignedInteger(child);
                    video.HasPrimaries = true;
                    break;
                case MatroskaIds.MaxCll:
                    video.MaxCll = (int)ebml.ReadUnsignedInteger(child);
                    video.HasHdr = true;
                    break;
                case MatroskaIds.MaxFall:
                    video.MaxFall = (int)ebml.ReadUnsignedInteger(child);
                    video.HasHdr = true;
                    break;
                case MatroskaIds.MasteringMetadata:
                    ReadMasteringMetadata(child, ref video);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }
    }

    private void ReadMasteringMetadata(in EbmlElementHeader element, ref VideoSettings video)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        HdrMetadata hdr = video.Hdr ?? new HdrMetadata();
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.PrimaryRChromaticityX: hdr.RedPrimaryX = ebml.ReadFloat(child); break;
                case MatroskaIds.PrimaryRChromaticityY: hdr.RedPrimaryY = ebml.ReadFloat(child); break;
                case MatroskaIds.PrimaryGChromaticityX: hdr.GreenPrimaryX = ebml.ReadFloat(child); break;
                case MatroskaIds.PrimaryGChromaticityY: hdr.GreenPrimaryY = ebml.ReadFloat(child); break;
                case MatroskaIds.PrimaryBChromaticityX: hdr.BluePrimaryX = ebml.ReadFloat(child); break;
                case MatroskaIds.PrimaryBChromaticityY: hdr.BluePrimaryY = ebml.ReadFloat(child); break;
                case MatroskaIds.WhitePointChromaticityX: hdr.WhitePointX = ebml.ReadFloat(child); break;
                case MatroskaIds.WhitePointChromaticityY: hdr.WhitePointY = ebml.ReadFloat(child); break;
                case MatroskaIds.LuminanceMax: hdr.MaxLuminance = ebml.ReadFloat(child); break;
                case MatroskaIds.LuminanceMin: hdr.MinLuminance = ebml.ReadFloat(child); break;
                default: ebml.SkipElement(child); break;
            }
        }

        video.Hdr = hdr;
        video.HasHdr = true;
    }

    private void ReadAudioSettings(in EbmlElementHeader element, ref AudioSettings audio)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.SamplingFrequency:
                    audio.SamplingFrequency = ebml.ReadFloat(child);
                    break;
                case MatroskaIds.OutputSamplingFrequency:
                    audio.OutputSamplingFrequency = ebml.ReadFloat(child);
                    break;
                case MatroskaIds.Channels:
                    audio.Channels = (int)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.AudioBitDepth:
                    audio.BitDepth = (int)ebml.ReadUnsignedInteger(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }
    }

    private void ReadCues(in EbmlElementHeader element)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        bool first = true;
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (first && child.Id == EbmlIds.Crc32)
            {
                ebml.VerifyMasterChecksum(element, child, "Cues");
                ebml.SkipElement(child);
                first = false;
                continue;
            }

            first = false;
            if (child.Id != MatroskaIds.CuePoint)
            {
                ebml.SkipElement(child);
                continue;
            }

            long cueTimeUnits = 0;
            long pointEnd = child.EndOffset;
            SeekTo(child.DataOffset);

            while (ebml.TryReadElementHeader(pointEnd, out EbmlElementHeader item))
            {
                if (item.Id == MatroskaIds.CueTime)
                {
                    cueTimeUnits = (long)ebml.ReadUnsignedInteger(item);
                    continue;
                }

                if (item.Id != MatroskaIds.CueTrackPositions)
                {
                    ebml.SkipElement(item);
                    continue;
                }

                int cueTrack = -1;
                long clusterPosition = -1;
                long relativePosition = -1;
                long cueDurationUnits = 0;
                long positionsEnd = item.EndOffset;

                while (ebml.TryReadElementHeader(positionsEnd, out EbmlElementHeader field))
                {
                    switch (field.Id)
                    {
                        case MatroskaIds.CueTrack:
                            cueTrack = (int)ebml.ReadUnsignedInteger(field);
                            break;
                        case MatroskaIds.CueClusterPosition:
                            clusterPosition = (long)ebml.ReadUnsignedInteger(field);
                            break;
                        case MatroskaIds.CueRelativePosition:
                            relativePosition = (long)ebml.ReadUnsignedInteger(field);
                            break;
                        case MatroskaIds.CueDuration:
                            cueDurationUnits = (long)ebml.ReadUnsignedInteger(field);
                            break;
                        default:
                            ebml.SkipElement(field);
                            break;
                    }
                }

                if (clusterPosition < 0) continue;

                cues.Add(new MatroskaCuePoint(
                    TimeSpan.FromTicks(UnitsToTicks(cueTimeUnits)),
                    cueTrack,
                    segmentDataOffset + clusterPosition,
                    relativePosition,
                    cueDurationUnits > 0 ? TimeSpan.FromTicks(UnitsToTicks(cueDurationUnits)) : TimeSpan.Zero));
            }
        }
    }

    private void ReadChapters(in EbmlElementHeader element)
    {
        long end = element.EndOffset;
        SeekTo(element.DataOffset);

        List<EbmlElementHeader> editions = new List<EbmlElementHeader>();
        EbmlElementHeader chosen = default;
        bool haveChosen = false;
        bool canRevisit = source.CanSeek;

        bool first = true;
        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (first && child.Id == EbmlIds.Crc32)
            {
                ebml.VerifyMasterChecksum(element, child, "Chapters");
                ebml.SkipElement(child);
                first = false;
                continue;
            }

            first = false;
            if (child.Id != MatroskaIds.EditionEntry)
            {
                ebml.SkipElement(child);
                continue;
            }

            editions.Add(child);

            if (!canRevisit)
            {
                // Nothing can be revisited on a forward-only source, so the first edition is the one read.
                ReadEdition(child);
                haveChosen = true;
                break;
            }

            if (!haveChosen)
            {
                chosen = child;
                haveChosen = true;
            }

            if (IsDefaultEdition(child)) chosen = child;

            SeekTo(child.EndOffset);
        }

        if (!haveChosen) return;

        if (editions.Count > 1)
        {
            notices.Add(
                $"The file carries {editions.Count} chapter editions; this library reads one - the default "
                + "edition, or the first when none is marked default.");
        }

        if (canRevisit) ReadEdition(chosen);
    }

    private bool IsDefaultEdition(in EbmlElementHeader edition)
    {
        long end = edition.EndOffset;
        SeekTo(edition.DataOffset);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (child.Id == MatroskaIds.EditionFlagDefault) return ebml.ReadUnsignedInteger(child) != 0;
            if (child.Id == MatroskaIds.ChapterAtom) break;
            ebml.SkipElement(child);
        }

        return false;
    }

    private void ReadEdition(in EbmlElementHeader edition)
    {
        long end = edition.EndOffset;
        SeekTo(edition.DataOffset);

        List<Chapter> collected = new List<Chapter>();
        bool nested = false;

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            if (child.Id != MatroskaIds.ChapterAtom)
            {
                ebml.SkipElement(child);
                continue;
            }

            Chapter chapter = ReadChapterAtom(child, collected.Count, ref nested);
            if (chapter != null) collected.Add(chapter);
            SeekTo(child.EndOffset);
        }

        if (nested)
        {
            notices.Add(
                "Some chapters have chapters of their own. This library reads the top level only, so the nested "
                + "ones are not listed.");
        }

        collected.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < collected.Count; i++)
        {
            Chapter c = collected[i];
            chapters.Add(new Chapter(i, c.Start, c.End, c.IsHidden, c.Titles));
        }
    }

    private Chapter ReadChapterAtom(in EbmlElementHeader atom, int index, ref bool nested)
    {
        long end = atom.EndOffset;
        SeekTo(atom.DataOffset);

        long startNs = 0;
        long endNs = 0;
        bool hidden = false;
        Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.ChapterTimeStart:
                    startNs = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ChapterTimeEnd:
                    endNs = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.ChapterFlagHidden:
                    hidden = ebml.ReadUnsignedInteger(child) != 0;
                    break;
                case MatroskaIds.ChapterAtom:
                    nested = true;
                    ebml.SkipElement(child);
                    break;
                case MatroskaIds.ChapterDisplay:
                    ReadChapterDisplay(child, titles);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }

        return new Chapter(
            index,
            TimeSpan.FromTicks(startNs / 100),
            endNs > 0 ? TimeSpan.FromTicks(endNs / 100) : TimeSpan.Zero,
            hidden,
            titles);
    }

    private void ReadChapterDisplay(in EbmlElementHeader display, Dictionary<string, string> titles)
    {
        long end = display.EndOffset;
        SeekTo(display.DataOffset);

        string text = string.Empty;
        string language = string.Empty;
        string bcp47 = string.Empty;

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.ChapString:
                    text = ebml.ReadString(child);
                    break;
                case MatroskaIds.ChapLanguage:
                    language = ebml.ReadString(child);
                    break;
                case MatroskaIds.ChapLanguageBcp47:
                    bcp47 = ebml.ReadString(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }

        if (string.IsNullOrEmpty(text)) return;

        string key = !string.IsNullOrEmpty(bcp47)
            ? LanguageTags.Normalize(bcp47)
            : LanguageTags.Normalize(language);

        titles[key] = text;
    }

    // ---------------------------------------------------------------- clusters and blocks

    private bool AdvanceToNextBlock()
    {
        while (true)
        {
            if (reachedEnd) return false;

            if (clusterEnd >= 0)
            {
                if (source.Position >= clusterEnd)
                {
                    LeaveCluster();
                    continue;
                }

                if (!ebml.TryReadElementHeader(clusterEnd, out EbmlElementHeader child))
                {
                    LeaveCluster();
                    continue;
                }

                if (IsTopLevelId(child.Id))
                {
                    // An unknown-size cluster ends where something that cannot be its child begins.
                    SeekTo(child.Offset);
                    LeaveCluster();
                    continue;
                }

                switch (child.Id)
                {
                    case MatroskaIds.ClusterTimestamp:
                        clusterTimestampUnits = (long)ebml.ReadUnsignedInteger(child);
                        continue;

                    case MatroskaIds.SimpleBlock:
                        if (ReadSimpleBlock(child)) return true;
                        continue;

                    case MatroskaIds.BlockGroup:
                        if (ReadBlockGroup(child)) return true;
                        continue;

                    default:
                        ebml.SkipElement(child);
                        continue;
                }
            }

            if (!MoveToNextCluster()) return false;
        }
    }

    private static bool IsTopLevelId(uint id) =>
        id == MatroskaIds.Cluster || id == MatroskaIds.Cues || id == MatroskaIds.Chapters
        || id == MatroskaIds.Tags || id == MatroskaIds.Attachments || id == MatroskaIds.Tracks
        || id == MatroskaIds.Info || id == MatroskaIds.SeekHead;

    private void LeaveCluster()
    {
        if (clusterEnd >= 0 && !clusterHadUnknownSize) nextTopLevelOffset = clusterEnd;
        else nextTopLevelOffset = source.Position;

        clusterEnd = -1;
    }

    private bool MoveToNextCluster()
    {
        if (hasPendingCluster)
        {
            hasPendingCluster = false;
            EnterCluster(pendingCluster);
            return true;
        }

        if (nextTopLevelOffset < 0)
        {
            reachedEnd = true;
            return false;
        }

        SeekTo(nextTopLevelOffset);

        while (ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element))
        {
            if (element.Id == MatroskaIds.Cluster)
            {
                EnterCluster(element);
                return true;
            }

            if (element.IsUnknownSize)
            {
                reachedEnd = true;
                return false;
            }

            ebml.SkipElement(element);
            nextTopLevelOffset = element.EndOffset;
        }

        reachedEnd = true;
        return false;
    }

    private void EnterCluster(in EbmlElementHeader cluster)
    {
        clusterHadUnknownSize = cluster.IsUnknownSize;
        clusterEnd = cluster.IsUnknownSize ? segmentDataEnd : cluster.EndOffset;
        clusterTimestampUnits = 0;
        ClustersRead++;

        SeekTo(cluster.DataOffset);

        if (!VerifyClusterChecksums || cluster.IsUnknownSize || !source.CanSeek) return;

        long saved = source.Position;
        if (ebml.TryReadElementHeader(clusterEnd, out EbmlElementHeader first) && first.Id == EbmlIds.Crc32)
        {
            ebml.VerifyMasterChecksum(cluster, first, "Cluster");
        }

        SeekTo(saved);
    }

    private TimeSpan EnterClusterAt(long offset)
    {
        SeekTo(offset);
        if (!ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element)
            || element.Id != MatroskaIds.Cluster)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has no cluster at offset {offset}, where its index says one should be. The index "
                + "does not match the file.");
        }

        EnterCluster(element);

        // Read forward to the cluster's Timestamp so the caller is told where it really landed. The element is
        // the first child in every file seen so far, but it is only REQUIRED to come before the first block, so
        // the search stops at the first block rather than after the first child.
        while (source.Position < clusterEnd)
        {
            long before = source.Position;
            if (!ebml.TryReadElementHeader(clusterEnd, out EbmlElementHeader child)) break;

            if (child.Id == MatroskaIds.ClusterTimestamp)
            {
                clusterTimestampUnits = (long)ebml.ReadUnsignedInteger(child);
                continue;
            }

            if (child.Id == MatroskaIds.SimpleBlock || child.Id == MatroskaIds.BlockGroup || IsTopLevelId(child.Id))
            {
                SeekTo(before);
                break;
            }

            ebml.SkipElement(child);
        }

        return TimeSpan.FromTicks(UnitsToTicks(clusterTimestampUnits));
    }

    private MatroskaCuePoint FindCue(TimeSpan position, int keyFrameTrackId)
    {
        MatroskaCuePoint best = null;
        MatroskaCuePoint firstMatch = null;

        for (int i = 0; i < cues.Count; i++)
        {
            MatroskaCuePoint cue = cues[i];
            if (keyFrameTrackId >= 0 && cue.TrackId >= 0 && cue.TrackId != keyFrameTrackId) continue;

            firstMatch ??= cue;
            if (cue.Time > position) break;
            best = cue;
        }

        return best ?? firstMatch;
    }

    private TimeSpan SeekByScanning(TimeSpan position)
    {
        long bestOffset = firstClusterOffset;

        SeekTo(firstClusterOffset);
        while (ebml.TryReadElementHeader(segmentDataEnd, out EbmlElementHeader element))
        {
            if (element.Id != MatroskaIds.Cluster)
            {
                if (element.IsUnknownSize) break;
                ebml.SkipElement(element);
                continue;
            }

            long clusterBound = element.IsUnknownSize ? segmentDataEnd : element.EndOffset;
            SeekTo(element.DataOffset);

            TimeSpan clusterTime = TimeSpan.Zero;
            while (ebml.TryReadElementHeader(clusterBound, out EbmlElementHeader child))
            {
                if (child.Id == MatroskaIds.ClusterTimestamp)
                {
                    clusterTime = TimeSpan.FromTicks(UnitsToTicks((long)ebml.ReadUnsignedInteger(child)));
                    break;
                }

                if (IsTopLevelId(child.Id)) break;
                ebml.SkipElement(child);
            }

            if (clusterTime > position) break;

            bestOffset = element.Offset;

            if (element.IsUnknownSize) break;
            SeekTo(element.EndOffset);
        }

        return EnterClusterAt(bestOffset);
    }

    private bool ReadSimpleBlock(in EbmlElementHeader block)
    {
        int length = ebml.ReadBinaryInto(block, ref blockBuffer);
        blockAdditionLength = 0;
        blockDuration = TimeSpan.Zero;
        blockDiscardPadding = TimeSpan.Zero;
        return ParseBlock(length, fromSimpleBlock: true, keyFrameWhenNotSimple: false);
    }

    private bool ReadBlockGroup(in EbmlElementHeader group)
    {
        long end = group.EndOffset;
        SeekTo(group.DataOffset);

        int length = -1;
        bool sawReference = false;
        long durationUnits = 0;
        long discardPaddingNs = 0;
        blockAdditionLength = 0;

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader child))
        {
            switch (child.Id)
            {
                case MatroskaIds.Block:
                    length = ebml.ReadBinaryInto(child, ref blockBuffer);
                    break;
                case MatroskaIds.BlockDuration:
                    durationUnits = (long)ebml.ReadUnsignedInteger(child);
                    break;
                case MatroskaIds.DiscardPadding:
                    discardPaddingNs = ebml.ReadSignedInteger(child);
                    break;
                case MatroskaIds.ReferenceBlock:
                    sawReference = true;
                    ebml.SkipElement(child);
                    break;
                case MatroskaIds.BlockAdditions:
                    ReadBlockAdditions(child);
                    break;
                default:
                    ebml.SkipElement(child);
                    break;
            }
        }

        SeekTo(end);

        if (length < 0) return false;

        blockDuration = durationUnits > 0 ? TimeSpan.FromTicks(UnitsToTicks(durationUnits)) : TimeSpan.Zero;
        blockDiscardPadding = discardPaddingNs > 0 ? TimeSpan.FromTicks(discardPaddingNs / 100) : TimeSpan.Zero;

        // A Block has no key-frame bit of its own: a BlockGroup with no ReferenceBlock IS the key frame.
        return ParseBlock(length, fromSimpleBlock: false, keyFrameWhenNotSimple: !sawReference);
    }

    private void ReadBlockAdditions(in EbmlElementHeader additions)
    {
        long end = additions.EndOffset;
        SeekTo(additions.DataOffset);

        while (ebml.TryReadElementHeader(end, out EbmlElementHeader more))
        {
            if (more.Id != MatroskaIds.BlockMore)
            {
                ebml.SkipElement(more);
                continue;
            }

            long moreEnd = more.EndOffset;
            long addId = 1;
            int length = -1;

            while (ebml.TryReadElementHeader(moreEnd, out EbmlElementHeader field))
            {
                switch (field.Id)
                {
                    case MatroskaIds.BlockAddId:
                        addId = (long)ebml.ReadUnsignedInteger(field);
                        break;
                    case MatroskaIds.BlockAdditional:
                        length = ebml.ReadBinaryInto(field, ref additionBuffer);
                        break;
                    default:
                        ebml.SkipElement(field);
                        break;
                }
            }

            if (addId == 1 && length >= 0) blockAdditionLength = length;
            SeekTo(moreEnd);
        }

        SeekTo(end);
    }

    private bool ParseBlock(int length, bool fromSimpleBlock, bool keyFrameWhenNotSimple)
    {
        frameCount = 0;
        frameIndex = 0;
        blockTrack = null;

        ReadOnlySpan<byte> data = blockBuffer.AsSpan(0, length);
        if (!EbmlReader.TryReadVint(data, out ulong trackNumber, out int trackLength))
        {
            throw new VideoPlaybackException(
                $"A block in '{source.Name}' has a malformed track number; the file is damaged.");
        }

        int position = trackLength;
        if (position + 3 > data.Length)
        {
            throw new VideoPlaybackException(
                $"A block in '{source.Name}' is only {data.Length} bytes long, too short to carry a timestamp and "
                + "flags; the file is damaged.");
        }

        short relative = (short)((data[position] << 8) | data[position + 1]);
        position += 2;
        byte flags = data[position];
        position++;

        int trackId = (int)trackNumber;
        if (!trackStates.TryGetValue(trackId, out TrackState state) || state.Info.Kind == MediaTrackKind.Unknown)
        {
            return false;
        }

        blockTrack = state;
        blockTimestampUnits = clusterTimestampUnits + relative;
        blockIsKeyFrame = fromSimpleBlock ? (flags & 0x80) != 0 : keyFrameWhenNotSimple;

        int lacing = (flags >> 1) & 0x03;
        if (lacing == 0)
        {
            EnsureFrameCapacity(1);
            frameOffsets[0] = position;
            frameLengths[0] = data.Length - position;
            frameCount = 1;
            return true;
        }

        if (position >= data.Length)
        {
            throw new VideoPlaybackException(
                $"A laced block in '{source.Name}' ends before its frame count; the file is damaged.");
        }

        int count = data[position] + 1;
        position++;
        EnsureFrameCapacity(count);

        switch (lacing)
        {
            case 1:
                ParseXiphLacing(data, ref position, count);
                break;
            case 2:
                ParseFixedLacing(data, position, count);
                break;
            default:
                ParseEbmlLacing(data, ref position, count);
                break;
        }

        if (blockTrack.Info.Kind == MediaTrackKind.Audio
            && frameCount > 1
            && blockTrack.DefaultDurationTicks <= 0
            && blockDuration <= TimeSpan.Zero
            && !blockTrack.WarnedAboutLacedTiming)
        {
            blockTrack.WarnedAboutLacedTiming = true;
            notices.Add(
                $"Audio track {blockTrack.Info.Id} has laced blocks but no DefaultDuration and no BlockDuration, "
                + "so every frame in a lace carries its block's timestamp. Playback is unaffected - the audio "
                + "clock counts samples - but a seek lands on a block boundary rather than a frame boundary.");
        }

        return frameCount > 0;
    }

    private void ParseXiphLacing(ReadOnlySpan<byte> data, ref int position, int count)
    {
        Span<int> sizes = count <= 64 ? stackalloc int[count] : new int[count];
        int total = 0;

        for (int i = 0; i < count - 1; i++)
        {
            int size = 0;
            while (true)
            {
                if (position >= data.Length)
                {
                    throw new VideoPlaybackException(
                        $"A Xiph-laced block in '{source.Name}' ends inside its size table; the file is damaged.");
                }

                byte b = data[position];
                position++;
                size += b;
                if (b != 255) break;
            }

            sizes[i] = size;
            total += size;
        }

        int remaining = data.Length - position - total;
        if (remaining < 0)
        {
            throw new VideoPlaybackException(
                $"A Xiph-laced block in '{source.Name}' declares frame sizes adding up to more than the block "
                + "holds; the file is damaged.");
        }

        sizes[count - 1] = remaining;
        EmitFrames(sizes, position);
    }

    private void ParseFixedLacing(ReadOnlySpan<byte> data, int position, int count)
    {
        int remaining = data.Length - position;
        if (count <= 0 || remaining % count != 0)
        {
            throw new VideoPlaybackException(
                $"A fixed-laced block in '{source.Name}' holds {remaining} bytes for {count} frames, which does not "
                + "divide evenly; the file is damaged.");
        }

        int size = remaining / count;
        Span<int> sizes = count <= 64 ? stackalloc int[count] : new int[count];
        for (int i = 0; i < count; i++) sizes[i] = size;
        EmitFrames(sizes, position);
    }

    private void ParseEbmlLacing(ReadOnlySpan<byte> data, ref int position, int count)
    {
        Span<int> sizes = count <= 64 ? stackalloc int[count] : new int[count];
        int total = 0;

        if (!EbmlReader.TryReadVint(data.Slice(position), out ulong firstSize, out int firstLength))
        {
            throw new VideoPlaybackException(
                $"An EBML-laced block in '{source.Name}' has a malformed first frame size; the file is damaged.");
        }

        position += firstLength;
        long current = (long)firstSize;
        sizes[0] = (int)current;
        total += (int)current;

        for (int i = 1; i < count - 1; i++)
        {
            if (!EbmlReader.TryReadSignedVint(data.Slice(position), out long delta, out int deltaLength))
            {
                throw new VideoPlaybackException(
                    $"An EBML-laced block in '{source.Name}' has a malformed frame-size difference; the file is "
                    + "damaged.");
            }

            position += deltaLength;
            current += delta;
            if (current < 0)
            {
                throw new VideoPlaybackException(
                    $"An EBML-laced block in '{source.Name}' declares a negative frame size; the file is damaged.");
            }

            sizes[i] = (int)current;
            total += (int)current;
        }

        int remaining = data.Length - position - total;
        if (remaining < 0)
        {
            throw new VideoPlaybackException(
                $"An EBML-laced block in '{source.Name}' declares frame sizes adding up to more than the block "
                + "holds; the file is damaged.");
        }

        sizes[count - 1] = remaining;
        EmitFrames(sizes, position);
    }

    private void EmitFrames(ReadOnlySpan<int> sizes, int position)
    {
        int at = position;
        for (int i = 0; i < sizes.Length; i++)
        {
            frameOffsets[i] = at;
            frameLengths[i] = sizes[i];
            at += sizes[i];
        }

        frameCount = sizes.Length;
    }

    private void EnsureFrameCapacity(int count)
    {
        if (frameOffsets.Length >= count) return;

        int capacity = frameOffsets.Length;
        while (capacity < count) capacity *= 2;
        frameOffsets = new int[capacity];
        frameLengths = new int[capacity];
    }

    // ---------------------------------------------------------------- captions

    /// <summary>
    /// Turns a caption block into a cue, coping with the two different ways WebVTT is stored in Matroska.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are two dialects in the wild and both are supported:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     The one the Matroska specification describes, written under the codec identifier
    ///     <c>S_TEXT/WEBVTT</c>: the block carries the cue text alone, and the cue settings and the cue
    ///     identifier travel in a block addition, settings on the first line and identifier on the second.
    ///   </description></item>
    ///   <item><description>
    ///     The one FFmpeg writes, under <c>D_WEBVTT/SUBTITLES</c>: everything is inline in the block, cue
    ///     identifier on the first line, cue settings on the second, and the text after that - the OTHER way
    ///     round.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Relying on the codec identifier alone would break on a file that mixes the conventions, so the order is
    /// decided by looking at the two lines: a WebVTT cue settings list is a run of <c>name:value</c> pairs and
    /// a cue identifier is not, so whichever line looks like settings IS the settings. The codec identifier
    /// only breaks the tie when both lines are empty or neither looks like settings, and in that case it does
    /// not matter much either way.
    /// </para>
    /// </remarks>
    private void AddCaptionCue(TrackState track, ReadOnlySpan<byte> payload, TimeSpan start, TimeSpan duration)
    {
        TimeSpan length = duration;
        if (length <= TimeSpan.Zero) length = track.DefaultDurationTicks > 0
            ? TimeSpan.FromTicks(track.DefaultDurationTicks)
            : FallbackCueDuration;

        string text;
        string settings = string.Empty;
        string identifier = string.Empty;

        switch (track.Info.CaptionFormat)
        {
            case CaptionFormat.Ass:
                text = CaptionFiles.ExtractAssText(DecodeUtf8(payload));
                break;

            case CaptionFormat.WebVtt:
            {
                if (blockAdditionLength > 0)
                {
                    text = DecodeUtf8(payload);
                    SplitWebVttMetadata(
                        DecodeUtf8(additionBuffer.AsSpan(0, blockAdditionLength)),
                        settingsFirstByDialect: true,
                        out settings,
                        out identifier);
                    break;
                }

                string whole = DecodeUtf8(payload);
                int firstBreak = whole.IndexOf('\n');
                int secondBreak = firstBreak < 0 ? -1 : whole.IndexOf('\n', firstBreak + 1);

                if (secondBreak < 0)
                {
                    text = whole;
                    break;
                }

                string lineOne = whole.Substring(0, firstBreak).TrimEnd('\r');
                string lineTwo = whole.Substring(firstBreak + 1, secondBreak - firstBreak - 1).TrimEnd('\r');
                text = whole.Substring(secondBreak + 1);

                AssignWebVttMetadata(lineOne, lineTwo, !track.InlineWebVtt, out settings, out identifier);
                break;
            }

            default:
                text = DecodeUtf8(payload);
                break;
        }

        track.Captions.AddCue(new CaptionCue(start, start + length, text, settings, identifier));
    }

    private static void SplitWebVttMetadata(
        string blob,
        bool settingsFirstByDialect,
        out string settings,
        out string identifier)
    {
        string first = blob;
        string second = string.Empty;

        int lineBreak = blob.IndexOf('\n');
        if (lineBreak >= 0)
        {
            first = blob.Substring(0, lineBreak).TrimEnd('\r');
            string rest = blob.Substring(lineBreak + 1);
            int nextBreak = rest.IndexOf('\n');
            second = (nextBreak >= 0 ? rest.Substring(0, nextBreak) : rest).TrimEnd('\r');
        }

        AssignWebVttMetadata(first, second, settingsFirstByDialect, out settings, out identifier);
    }

    private static void AssignWebVttMetadata(
        string first,
        string second,
        bool settingsFirstByDialect,
        out string settings,
        out string identifier)
    {
        bool firstLooksLikeSettings = LooksLikeCueSettings(first);
        bool secondLooksLikeSettings = LooksLikeCueSettings(second);

        if (firstLooksLikeSettings && !secondLooksLikeSettings)
        {
            settings = first;
            identifier = second;
            return;
        }

        if (secondLooksLikeSettings && !firstLooksLikeSettings)
        {
            identifier = first;
            settings = second;
            return;
        }

        if (settingsFirstByDialect)
        {
            settings = first;
            identifier = second;
            return;
        }

        identifier = first;
        settings = second;
    }

    private static bool LooksLikeCueSettings(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        int tokens = 0;
        int start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length) break;

            int stop = start;
            while (stop < text.Length && !char.IsWhiteSpace(text[stop])) stop++;

            int colon = text.IndexOf(':', start, stop - start);
            if (colon <= start) return false;

            for (int i = start; i < colon; i++)
            {
                char c = text[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            }

            tokens++;
            start = stop;
        }

        return tokens > 0;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);

    // ---------------------------------------------------------------- codec mapping

    private string MapVideoCodec(string codecId, int trackNumber)
    {
        if (string.Equals(codecId, "V_AV1", StringComparison.Ordinal)) return VideoCodecIds.Av1;
        if (string.Equals(codecId, "V_UNCOMPRESSED", StringComparison.Ordinal)) return VideoCodecIds.Raw;

        throw new VideoPlaybackException(
            $"'{source.Name}' has a video track (number {trackNumber}) whose CodecID is '{codecId}'. This library "
            + "reads V_AV1 and V_UNCOMPRESSED video, and A_OPUS or A_VORBIS audio; nothing else.");
    }

    private string MapAudioCodec(string codecId, int trackNumber)
    {
        if (string.Equals(codecId, "A_OPUS", StringComparison.Ordinal)) return VideoCodecIds.Opus;
        if (string.Equals(codecId, "A_VORBIS", StringComparison.Ordinal)) return VideoCodecIds.Vorbis;

        throw new VideoPlaybackException(
            $"'{source.Name}' has an audio track (number {trackNumber}) whose CodecID is '{codecId}'. This library "
            + "reads V_AV1 and V_UNCOMPRESSED video, and A_OPUS or A_VORBIS audio; nothing else.");
    }

    private static bool TryMapCaptionCodec(
        string codecId,
        out string mapped,
        out CaptionFormat format,
        out bool inlineWebVtt)
    {
        mapped = string.Empty;
        format = CaptionFormat.Unknown;
        inlineWebVtt = false;

        if (string.Equals(codecId, "S_TEXT/WEBVTT", StringComparison.Ordinal))
        {
            mapped = VideoCodecIds.WebVtt;
            format = CaptionFormat.WebVtt;
            return true;
        }

        if (codecId.StartsWith("D_WEBVTT/", StringComparison.Ordinal))
        {
            mapped = VideoCodecIds.WebVtt;
            format = CaptionFormat.WebVtt;
            inlineWebVtt = true;
            return true;
        }

        if (string.Equals(codecId, "S_TEXT/UTF8", StringComparison.Ordinal))
        {
            mapped = VideoCodecIds.SubRip;
            format = CaptionFormat.SubRip;
            return true;
        }

        if (string.Equals(codecId, "S_TEXT/ASS", StringComparison.Ordinal)
            || string.Equals(codecId, "S_TEXT/SSA", StringComparison.Ordinal))
        {
            mapped = VideoCodecIds.Ass;
            format = CaptionFormat.Ass;
            return true;
        }

        return false;
    }

    private static int ReadOpusPreSkip(string codecId, byte[] codecPrivate)
    {
        if (!string.Equals(codecId, VideoCodecIds.Opus, StringComparison.Ordinal)) return 0;
        if (codecPrivate == null || codecPrivate.Length < 12) return 0;
        if (codecPrivate[0] != (byte)'O' || codecPrivate[1] != (byte)'p' || codecPrivate[2] != (byte)'u'
            || codecPrivate[3] != (byte)'s')
        {
            return 0;
        }

        // OpusHead: magic[8], version, channels, pre-skip (16-bit little-endian).
        return codecPrivate[10] | (codecPrivate[11] << 8);
    }

    private void ApplyVideoSettings(MediaTrackInfo info, VideoSettings video, string rawCodecId)
    {
        int width = video.PixelWidth - video.CropLeft - video.CropRight;
        int height = video.PixelHeight - video.CropTop - video.CropBottom;

        info.Width = width > 0 ? width : video.PixelWidth;
        info.Height = height > 0 ? height : video.PixelHeight;

        // DisplayUnit 0 means the display size is in pixels; anything else (centimetres, inches, an aspect
        // ratio) does not give a pixel size, so the stored size is used instead.
        if (video.DisplayUnit == 0 && video.DisplayWidth > 0 && video.DisplayHeight > 0)
        {
            info.DisplayWidth = video.DisplayWidth;
            info.DisplayHeight = video.DisplayHeight;
        }
        else
        {
            info.DisplayWidth = info.Width;
            info.DisplayHeight = info.Height;
        }

        info.BitDepth = video.BitsPerChannel;
        info.Layout = MapPixelLayout(video, rawCodecId, info);
        info.Color = new VideoColorInfo(
            video.HasPrimaries ? (VideoColorPrimaries)video.Primaries : VideoColorPrimaries.Unspecified,
            video.HasTransfer ? (VideoTransferCharacteristics)video.Transfer : VideoTransferCharacteristics.Unspecified,
            video.HasMatrix ? (VideoMatrixCoefficients)video.Matrix : VideoMatrixCoefficients.Unspecified,
            MapRange(video.Range),
            MapChromaSiting(video.ChromaSitingHorz, video.ChromaSitingVert));

        if (video.HasHdr && video.Hdr != null)
        {
            video.Hdr.MaxContentLightLevel = video.MaxCll;
            video.Hdr.MaxFrameAverageLightLevel = video.MaxFall;
            info.Hdr = video.Hdr;
        }
    }

    private static VideoColorRange MapRange(int range) =>
        range switch
        {
            1 => VideoColorRange.Limited,
            2 => VideoColorRange.Full,
            _ => VideoColorRange.Unspecified,
        };

    /// <summary>
    /// Folds Matroska's two chroma-siting elements onto the single value the rest of this library uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matroska says 0 = unspecified, 1 = collocated with the luma sample (left horizontally, top vertically),
    /// 2 = half a sample away. The fold is:
    /// </para>
    /// <list type="table">
    ///   <listheader><term>Horizontal / vertical</term><description>Result</description></listheader>
    ///   <item><term>1 / 1</term><description>Colocated - co-sited in both directions</description></item>
    ///   <item><term>1 / 2</term><description>Vertical - the ordinary "left" siting AV1 calls CSP_VERTICAL</description></item>
    ///   <item><term>2 / 2</term><description>Interstitial - centred in both directions</description></item>
    ///   <item><term>2 / 1</term><description>Interstitial, with a notice: nothing in these codecs uses it</description></item>
    ///   <item><term>0 / anything, anything / 0</term><description>Unknown - the colour description decides</description></item>
    /// </list>
    /// </remarks>
    /// <param name="horizontal">Matroska's <c>ChromaSitingHorz</c>.</param>
    /// <param name="vertical">Matroska's <c>ChromaSitingVert</c>.</param>
    /// <returns>The folded value.</returns>
    private VideoChromaSiting MapChromaSiting(int horizontal, int vertical)
    {
        if (horizontal == 0 || vertical == 0) return VideoChromaSiting.Unknown;

        if (horizontal == 1) return vertical == 1 ? VideoChromaSiting.Colocated : VideoChromaSiting.Vertical;

        if (vertical == 1)
        {
            notices.Add(
                "The video track declares chroma siting that is half a sample across horizontally but co-sited "
                + "vertically. No codec this library reads produces that, and the converter has no separate case "
                + "for it, so it is treated as centred in both directions.");
        }

        return VideoChromaSiting.Interstitial;
    }

    private VideoPixelLayout MapPixelLayout(VideoSettings video, string rawCodecId, MediaTrackInfo info)
    {
        if (string.Equals(rawCodecId, "V_UNCOMPRESSED", StringComparison.Ordinal))
        {
            switch (video.ColourSpace)
            {
                case "I420":
                case "YV12":
                    if (info.BitDepth <= 0) info.BitDepth = 8;
                    return VideoPixelLayout.I420;
                case "I422":
                case "YV16":
                    if (info.BitDepth <= 0) info.BitDepth = 8;
                    return VideoPixelLayout.I422;
                case "I444":
                case "YV24":
                    if (info.BitDepth <= 0) info.BitDepth = 8;
                    return VideoPixelLayout.I444;
                case "Y800":
                case "GREY":
                case "Y8  ":
                    if (info.BitDepth <= 0) info.BitDepth = 8;
                    return VideoPixelLayout.Gray;
                default:
                    notices.Add(
                        $"The uncompressed video track declares the pixel format '{video.ColourSpace}', which this "
                        + "library does not recognise; a decoder will have to say what the samples are.");
                    return VideoPixelLayout.Unknown;
            }
        }

        if (!video.HasChromaSubsampling) return VideoPixelLayout.Unknown;

        if (video.ChromaSubsamplingHorz == 1 && video.ChromaSubsamplingVert == 1) return VideoPixelLayout.I420;
        if (video.ChromaSubsamplingHorz == 1 && video.ChromaSubsamplingVert == 0) return VideoPixelLayout.I422;
        if (video.ChromaSubsamplingHorz == 0 && video.ChromaSubsamplingVert == 0) return VideoPixelLayout.I444;

        return VideoPixelLayout.Unknown;
    }

    private void SeekTo(long offset)
    {
        // Only move when a move is needed - assigning the position at all would refuse a forward-only source.
        // A forward move on such a source is still possible by reading and discarding, which is what a
        // progressive download has to do; only a genuine rewind is refused.
        long current = source.Position;
        if (current == offset) return;

        if (!source.CanSeek && offset > current)
        {
            source.Skip(offset - current);
            return;
        }

        source.Position = offset;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(MatroskaReader));
    }

    private sealed class TrackState
    {
        public MediaTrackInfo Info;
        public CaptionTrack Captions;
        public string RawCodecId = string.Empty;
        public long DefaultDurationTicks;
        public bool InlineWebVtt;
        public bool WarnedAboutLacedTiming;
    }

    private struct VideoSettings
    {
        public int PixelWidth;
        public int PixelHeight;
        public int CropLeft;
        public int CropRight;
        public int CropTop;
        public int CropBottom;
        public int DisplayWidth;
        public int DisplayHeight;
        public int DisplayUnit;
        public string ColourSpace;
        public int Matrix;
        public bool HasMatrix;
        public int BitsPerChannel;
        public int ChromaSubsamplingHorz;
        public int ChromaSubsamplingVert;
        public bool HasChromaSubsampling;
        public int ChromaSitingHorz;
        public int ChromaSitingVert;
        public int Range;
        public int Transfer;
        public bool HasTransfer;
        public int Primaries;
        public bool HasPrimaries;
        public int MaxCll;
        public int MaxFall;
        public bool HasHdr;
        public HdrMetadata Hdr;
    }

    private struct AudioSettings
    {
        public double SamplingFrequency;
        public double OutputSamplingFrequency;
        public int Channels;
        public int BitDepth;
    }
}
