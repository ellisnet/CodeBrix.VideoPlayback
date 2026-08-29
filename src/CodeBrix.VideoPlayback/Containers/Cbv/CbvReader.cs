using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Internal;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// Reads the bespoke <c>.cbv</c> container: a header carrying every track, every caption cue and every
/// chapter, an index covering every chunk, and then the chunks themselves.
/// </summary>
/// <remarks>
/// <para>
/// The format is index-first by construction, which is the point of it. Opening a file reads the header and
/// the index and nothing else; after that the reader knows where every packet is and when it is for, so a
/// seek is arithmetic and a caption cue is available the instant the file is open - including immediately
/// after a seek, which is exactly when a player needs one.
/// </para>
/// <para>
/// The complete layout is written out in <c>CBV-FORMAT.txt</c> at the root of this library's repository.
/// </para>
/// </remarks>
public sealed class CbvReader : IMediaContainerReader
{
    private readonly IMediaSource source;
    private readonly bool leaveSourceOpen;
    private readonly List<MediaTrackInfo> tracks = new List<MediaTrackInfo>();
    private readonly List<CaptionTrack> captionTracks = new List<CaptionTrack>();
    private readonly List<Chapter> chapters = new List<Chapter>();
    private readonly List<string> notices = new List<string>();
    private readonly List<CbvIndexEntry> index = new List<CbvIndexEntry>();
    private readonly Dictionary<int, MediaTrackInfo> tracksById = new Dictionary<int, MediaTrackInfo>();
    private readonly Dictionary<int, List<int>> keyFramesByTrack = new Dictionary<int, List<int>>();
    private readonly Dictionary<int, int> lastEntryByTrack = new Dictionary<int, int>();
    private readonly Dictionary<int, long> lastTimestampByTrack = new Dictionary<int, long>();

    private byte[] chunkBuffer = new byte[64 * 1024];
    private int nextEntry;
    private long sequentialPosition;
    private bool disposed;

    /// <summary>Opens a bespoke file over a source.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this reader is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The file is not a bespoke file, or is malformed.</exception>
    public CbvReader(IMediaSource source, bool leaveSourceOpen = false)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.leaveSourceOpen = leaveSourceOpen;
        Read();
    }

    /// <summary>Reports whether a file's first bytes are the bespoke container's magic.</summary>
    /// <param name="firstBytes">At least the first four bytes of the file.</param>
    /// <returns>True when the bytes begin with <c>CBVF</c>.</returns>
    public static bool IsCbv(ReadOnlySpan<byte> firstBytes) =>
        firstBytes.Length >= 4 && firstBytes.Slice(0, 4).SequenceEqual(CbvFormat.Magic);

    /// <inheritdoc />
    public string FormatName => "CodeBrix Video (.cbv)";

    /// <inheritdoc />
    public TimeSpan Duration { get; private set; }

    /// <inheritdoc />
    public bool CanSeek => index.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrackInfo> Tracks => tracks;

    /// <inheritdoc />
    public IReadOnlyList<CaptionTrack> CaptionTracks => captionTracks;

    /// <inheritdoc />
    public IReadOnlyList<Chapter> Chapters => chapters;

    /// <inheritdoc />
    public IReadOnlyList<string> Notices => notices;

    /// <summary>The format version the file declares. This library writes and reads version 0.</summary>
    public ushort Version { get; private set; }

    /// <summary>The file's header flags.</summary>
    public CbvHeaderFlags Flags { get; private set; }

    /// <summary>How many ticks the file counts to the second.</summary>
    public uint Timescale { get; private set; }

    /// <summary>The file's whole index, in storage order.</summary>
    public IReadOnlyList<CbvIndexEntry> Index => index;

    /// <summary>True when the file carried a header checksum and it matched.</summary>
    public bool HeaderChecksumVerified { get; private set; }

    /// <summary>True when the file carried an index checksum and it matched.</summary>
    public bool IndexChecksumVerified { get; private set; }

    /// <inheritdoc />
    public bool TryReadPacket(out MediaPacket packet)
    {
        ThrowIfDisposed();

        while (nextEntry < index.Count)
        {
            CbvIndexEntry entry = index[nextEntry];
            nextEntry++;

            if (entry.Size > CbvFormat.MaximumChunkLength)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' declares a {entry.Size}-byte chunk, which is beyond the "
                    + $"{CbvFormat.MaximumChunkLength}-byte limit this reader will accept.");
            }

            EnsureChunkBuffer((int)entry.Size + CbvFormat.ChunkHeaderLength);
            Span<byte> whole = chunkBuffer.AsSpan(0, CbvFormat.ChunkHeaderLength + (int)entry.Size);
            ReadRegion((long)entry.Offset, whole, "a chunk");

            byte trackId = whole[0];
            CbvChunkFlags flags = (CbvChunkFlags)whole[1];
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(whole.Slice(2, 4));
            long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(whole.Slice(6, 8));
            long durationTicks = BinaryPrimitives.ReadInt64LittleEndian(whole.Slice(14, 8));

            if (trackId != entry.TrackId || size != entry.Size)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' has an index entry for track {entry.TrackId} of {entry.Size} bytes at offset "
                    + $"{entry.Offset}, but the chunk there says track {trackId} of {size} bytes. The index and the "
                    + "chunks disagree.");
            }

            if (!tracksById.ContainsKey(trackId))
            {
                notices.Add($"A chunk names track {trackId}, which the header does not declare; it was skipped.");
                continue;
            }

            packet = new MediaPacket(
                trackId,
                chunkBuffer.AsMemory(CbvFormat.ChunkHeaderLength, (int)size),
                TicksToTimeSpan(timestampTicks),
                TicksToTimeSpan(durationTicks),
                (flags & CbvChunkFlags.KeyFrame) != 0);

            return true;
        }

        packet = default;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// EXACT, AND KNOWN BEFORE ANYTHING HAS BEEN READ. The index at the front of the file names the track of
    /// every chunk in it, so this reader works out where each track's last chunk sits while it is opening the
    /// file and can then answer from the read cursor alone. A track the header does not declare, and a track
    /// with no chunks at all, read as exhausted immediately; so does every track of a file that carries no
    /// index, because such a file yields no packets to this reader in the first place.
    /// </remarks>
    public bool IsTrackExhausted(int trackId)
    {
        ThrowIfDisposed();
        return !lastEntryByTrack.TryGetValue(trackId, out int last) || nextEntry > last;
    }

    /// <inheritdoc />
    /// <remarks>
    /// EXACT for every track of an indexed file, and null for a track that has no chunks or that the file
    /// does not declare. It is the timestamp the last chunk carries, not that chunk's end, so a track's last
    /// packet still has its own duration to run.
    /// </remarks>
    public TimeSpan? GetTrackEndTimestamp(int trackId)
    {
        ThrowIfDisposed();
        if (!lastTimestampByTrack.TryGetValue(trackId, out long ticks)) return null;
        return TicksToTimeSpan(ticks);
    }

    /// <inheritdoc />
    public TimeSpan Seek(TimeSpan position, int keyFrameTrackId)
    {
        ThrowIfDisposed();

        if (index.Count == 0)
        {
            throw new NotSupportedException($"'{source.Name}' carries no index, so it cannot be seeked.");
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        long wantedTicks = TimeSpanToTicks(position);
        int target = -1;

        if (keyFrameTrackId >= 0 && keyFramesByTrack.TryGetValue(keyFrameTrackId, out List<int> keyFrames))
        {
            for (int i = keyFrames.Count - 1; i >= 0; i--)
            {
                if (index[keyFrames[i]].TimestampTicks > wantedTicks) continue;
                target = keyFrames[i];
                break;
            }

            if (target < 0 && keyFrames.Count > 0) target = keyFrames[0];
        }

        if (target < 0)
        {
            for (int i = index.Count - 1; i >= 0; i--)
            {
                if (index[i].TimestampTicks > wantedTicks) continue;
                target = i;
                break;
            }
        }

        if (target < 0) target = 0;

        nextEntry = target;
        return TicksToTimeSpan(index[target].TimestampTicks);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveSourceOpen) source.Dispose();
    }

    private void Read()
    {
        Span<byte> fixedHeader = stackalloc byte[CbvFormat.FixedHeaderLength];
        ReadRegion(0, fixedHeader, "the file header");

        if (!IsCbv(fixedHeader))
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' does not begin with the bespoke container's magic 'CBVF'. If it begins with "
                + "1A 45 DF A3 it is a Matroska or WebM file and should be opened with the Matroska reader.");
        }

        Version = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.Slice(4, 2));
        if (Version > CbvFormat.Version)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' is version {Version} of the bespoke container and this library reads version "
                + $"{CbvFormat.Version}. A newer writer produced it.");
        }

        Flags = (CbvHeaderFlags)BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.Slice(6, 2));
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.Slice(8, 4));
        ulong indexOffset = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.Slice(12, 8));
        ulong indexLength = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.Slice(20, 8));
        ulong durationTicks = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.Slice(28, 8));
        Timescale = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.Slice(36, 4));
        uint indexCrc = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.Slice(40, 4));
        uint headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.Slice(44, 4));

        if (Timescale == 0)
        {
            throw new VideoPlaybackException($"'{source.Name}' declares a timescale of zero, which cannot be used.");
        }

        if (headerLength < CbvFormat.FixedHeaderLength || headerLength > CbvFormat.MaximumHeaderLength)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares a {headerLength}-byte header region; it must be between "
                + $"{CbvFormat.FixedHeaderLength} and {CbvFormat.MaximumHeaderLength} bytes.");
        }

        Duration = TicksToTimeSpan((long)durationTicks);

        byte[] header = new byte[headerLength];
        ReadRegion(0, header, "the header region");

        if (headerCrc != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(CbvFormat.HeaderCrcOffset, 4), 0);
            uint computed = Crc32.Compute(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(CbvFormat.HeaderCrcOffset, 4), headerCrc);

            if (computed != headerCrc)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' has a header checksum of 0x{headerCrc:X8} but its header computes to "
                    + $"0x{computed:X8}. The file is damaged.");
            }

            HeaderChecksumVerified = true;
        }

        ParseHeader(header);
        ReadIndex(indexOffset, indexLength, indexCrc);
    }

    private void ParseHeader(byte[] header)
    {
        SpanReader reader = new SpanReader(header, $"the header of '{source.Name}'");
        reader.Seek(CbvFormat.FixedHeaderLength);

        int trackCount = reader.ReadUInt16("the track count");
        for (int i = 0; i < trackCount; i++)
        {
            uint entryLength = reader.ReadUInt32("a track entry length");
            int entryStart = reader.Position;
            ReadTrack(ref reader, entryLength);
            reader.Seek(entryStart + (int)entryLength);
        }

        int chapterCount = (int)reader.ReadUInt32("the chapter count");
        for (int i = 0; i < chapterCount; i++)
        {
            long startTicks = reader.ReadInt64("a chapter start");
            long endTicks = reader.ReadInt64("a chapter end");
            byte flags = reader.ReadByte("a chapter's flags");
            int titleCount = reader.ReadUInt16("a chapter's title count");

            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int t = 0; t < titleCount; t++)
            {
                string language = CbvFormat.ReadLanguage(
                    reader.Take(CbvFormat.LanguageFieldLength, "a chapter title's language"));
                int titleLength = reader.ReadUInt16("a chapter title's length");
                titles[language] = reader.ReadUtf8(titleLength, "a chapter title");
            }

            chapters.Add(new Chapter(
                i,
                TicksToTimeSpan(startTicks),
                TicksToTimeSpan(endTicks),
                (flags & 1) != 0,
                titles));
        }
    }

    private void ReadTrack(ref SpanReader reader, uint entryLength)
    {
        byte trackId = reader.ReadByte("a track identifier");
        byte kind = reader.ReadByte("a track kind");
        string codecId = CbvFormat.ReadCodecId(reader.Take(CbvFormat.CodecIdFieldLength, "a track codec identifier"));
        string language = CbvFormat.ReadLanguage(reader.Take(CbvFormat.LanguageFieldLength, "a track language"));
        int nameLength = reader.ReadUInt16("a track name length");
        string name = reader.ReadUtf8(nameLength, "a track name");
        CbvTrackFlags flags = (CbvTrackFlags)reader.ReadByte("a track's flags");
        int codecPrivateLength = (int)reader.ReadUInt32("a track's codec-private length");
        ReadOnlySpan<byte> codecPrivate = reader.Take(codecPrivateLength, "a track's codec-private data");

        MediaTrackInfo track = new MediaTrackInfo
        {
            Id = trackId,
            CodecId = codecId,
            Language = LanguageTags.Normalize(language),
            Name = name,
            IsDefault = (flags & CbvTrackFlags.Default) != 0,
            IsForced = (flags & CbvTrackFlags.Forced) != 0,
            IsHearingImpaired = (flags & CbvTrackFlags.HearingImpaired) != 0,
            IsEnabled = (flags & CbvTrackFlags.Disabled) == 0,
            CodecPrivate = codecPrivate.ToArray(),
        };

        switch (kind)
        {
            case 1:
                track.Kind = MediaTrackKind.Video;
                ReadVideoTrack(ref reader, track);
                break;

            case 2:
                track.Kind = MediaTrackKind.Audio;
                ReadAudioTrack(ref reader, track);
                break;

            case 3:
                track.Kind = MediaTrackKind.Caption;
                ReadCaptionTrack(ref reader, track);
                break;

            default:
                track.Kind = MediaTrackKind.Unknown;
                notices.Add(
                    $"Track {trackId} declares kind {kind}, which this reader does not know; it was listed but "
                    + "will not be played.");
                break;
        }

        tracks.Add(track);
        tracksById[track.Id] = track;
    }

    private void ReadVideoTrack(ref SpanReader reader, MediaTrackInfo track)
    {
        track.Width = (int)reader.ReadUInt32("a video track's width");
        track.Height = (int)reader.ReadUInt32("a video track's height");
        track.DisplayWidth = (int)reader.ReadUInt32("a video track's display width");
        track.DisplayHeight = (int)reader.ReadUInt32("a video track's display height");
        track.BitDepth = reader.ReadByte("a video track's bit depth");
        track.Layout = (VideoPixelLayout)reader.ReadByte("a video track's pixel layout");

        VideoColorPrimaries primaries = (VideoColorPrimaries)reader.ReadByte("a video track's primaries");
        VideoTransferCharacteristics transfer =
            (VideoTransferCharacteristics)reader.ReadByte("a video track's transfer characteristic");
        VideoMatrixCoefficients matrix = (VideoMatrixCoefficients)reader.ReadByte("a video track's matrix");
        VideoColorRange range = (VideoColorRange)reader.ReadByte("a video track's colour range");
        VideoChromaSiting siting = (VideoChromaSiting)reader.ReadByte("a video track's chroma siting");
        track.Color = new VideoColorInfo(primaries, transfer, matrix, range, siting);

        byte hdrPresent = reader.ReadByte("a video track's high-dynamic-range flag");
        track.DefaultDuration = TicksToTimeSpan(reader.ReadInt64("a video track's frame duration"));

        if (hdrPresent == 0) return;

        track.Hdr = new HdrMetadata
        {
            RedPrimaryX = reader.ReadDouble("a mastering red x"),
            RedPrimaryY = reader.ReadDouble("a mastering red y"),
            GreenPrimaryX = reader.ReadDouble("a mastering green x"),
            GreenPrimaryY = reader.ReadDouble("a mastering green y"),
            BluePrimaryX = reader.ReadDouble("a mastering blue x"),
            BluePrimaryY = reader.ReadDouble("a mastering blue y"),
            WhitePointX = reader.ReadDouble("a mastering white x"),
            WhitePointY = reader.ReadDouble("a mastering white y"),
            MaxLuminance = reader.ReadDouble("a mastering maximum luminance"),
            MinLuminance = reader.ReadDouble("a mastering minimum luminance"),
            MaxContentLightLevel = (int)reader.ReadUInt32("a maximum content light level"),
            MaxFrameAverageLightLevel = (int)reader.ReadUInt32("a maximum frame-average light level"),
        };
    }

    private void ReadAudioTrack(ref SpanReader reader, MediaTrackInfo track)
    {
        track.SampleRate = (int)reader.ReadUInt32("an audio track's sample rate");
        track.Channels = reader.ReadByte("an audio track's channel count");
        track.PreSkipSamples = reader.ReadUInt16("an audio track's pre-skip");
        track.TrailingTrimSamples = (int)reader.ReadUInt32("an audio track's trailing trim");
        track.CodecDelay = TicksToTimeSpan(reader.ReadInt64("an audio track's codec delay"));
        track.SeekPreRoll = TicksToTimeSpan(reader.ReadInt64("an audio track's seek pre-roll"));
    }

    private void ReadCaptionTrack(ref SpanReader reader, MediaTrackInfo track)
    {
        CaptionFormat format = track.CodecId switch
        {
            VideoCodecIds.WebVtt => CaptionFormat.WebVtt,
            VideoCodecIds.SubRip => CaptionFormat.SubRip,
            VideoCodecIds.Ass => CaptionFormat.Ass,
            _ => CaptionFormat.Unknown,
        };

        track.CaptionFormat = format;

        CaptionTrackFlags flags = CaptionTrackFlags.None;
        if (track.IsDefault) flags |= CaptionTrackFlags.Default;
        if (track.IsForced) flags |= CaptionTrackFlags.Forced;
        if (track.IsHearingImpaired) flags |= CaptionTrackFlags.HearingImpaired;

        CaptionTrack captions = new CaptionTrack(track.Id, track.Language, track.Name, flags, format);

        int cueCount = (int)reader.ReadUInt32("a caption track's cue count");
        for (int i = 0; i < cueCount; i++)
        {
            long startTicks = reader.ReadInt64("a cue start");
            long endTicks = reader.ReadInt64("a cue end");
            int settingsLength = reader.ReadUInt16("a cue's settings length");
            string settings = reader.ReadUtf8(settingsLength, "a cue's settings");
            int identifierLength = reader.ReadUInt16("a cue's identifier length");
            string identifier = reader.ReadUtf8(identifierLength, "a cue's identifier");
            int textLength = (int)reader.ReadUInt32("a cue's text length");
            string text = reader.ReadUtf8(textLength, "a cue's text");

            captions.AddCue(new CaptionCue(
                TicksToTimeSpan(startTicks),
                TicksToTimeSpan(endTicks),
                text,
                settings,
                identifier));
        }

        captions.AreCuesComplete = true;
        captionTracks.Add(captions);
    }

    private void ReadIndex(ulong indexOffset, ulong indexLength, uint indexCrc)
    {
        if (indexLength == 0) return;

        if (indexLength > int.MaxValue)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares a {indexLength}-byte index, which is larger than this reader will read.");
        }

        byte[] buffer = new byte[(int)indexLength];
        ReadRegion((long)indexOffset, buffer, "the index");

        if (indexCrc != 0)
        {
            uint computed = Crc32.Compute(buffer);
            if (computed != indexCrc)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' has an index checksum of 0x{indexCrc:X8} but its index computes to "
                    + $"0x{computed:X8}. The file is damaged.");
            }

            IndexChecksumVerified = true;
        }

        SpanReader reader = new SpanReader(buffer, $"the index of '{source.Name}'");
        int entryCount = (int)reader.ReadUInt32("the index entry count");
        if (entryCount < 0 || entryCount > CbvFormat.MaximumIndexEntries)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares {entryCount} index entries, which is beyond the "
                + $"{CbvFormat.MaximumIndexEntries} this reader will accept.");
        }

        index.Capacity = entryCount;
        for (int i = 0; i < entryCount; i++)
        {
            byte trackId = reader.ReadByte("an index entry's track");
            CbvChunkFlags flags = (CbvChunkFlags)reader.ReadByte("an index entry's flags");
            uint size = reader.ReadUInt32("an index entry's size");
            ulong offset = reader.ReadUInt64("an index entry's offset");
            long ticks = reader.ReadInt64("an index entry's timestamp");

            index.Add(new CbvIndexEntry(trackId, flags, size, offset, ticks));

            if ((flags & CbvChunkFlags.KeyFrame) == 0) continue;

            if (!keyFramesByTrack.TryGetValue(trackId, out List<int> keyFrames))
            {
                keyFrames = new List<int>();
                keyFramesByTrack[trackId] = keyFrames;
            }

            keyFrames.Add(i);
        }

        // Where each track's LAST chunk sits. The index names the track of every chunk in the file, so this
        // is known before a single chunk has been read - which is what lets IsTrackExhausted answer exactly
        // and early rather than only when the whole file has been demultiplexed.
        for (int i = 0; i < index.Count; i++)
        {
            lastEntryByTrack[index[i].TrackId] = i;
            lastTimestampByTrack[index[i].TrackId] = index[i].TimestampTicks;
        }
    }

    private void EnsureChunkBuffer(int needed)
    {
        if (chunkBuffer.Length >= needed) return;

        int size = chunkBuffer.Length;
        while (size < needed) size *= 2;
        chunkBuffer = new byte[size];
    }

    private void ReadRegion(long offset, Span<byte> destination, string what)
    {
        if (source.CanSeek)
        {
            source.ReadExactlyAt(offset, destination, what);
            return;
        }

        if (offset < sequentialPosition)
        {
            throw new NotSupportedException(
                $"'{source.Name}' cannot seek, and reading {what} needs byte {offset} when the source has already "
                + $"reached byte {sequentialPosition}. Play this file from a seekable source.");
        }

        if (offset > sequentialPosition)
        {
            if (!source.Skip(offset - sequentialPosition))
            {
                throw new VideoPlaybackException($"'{source.Name}' ended before {what} at offset {offset}.");
            }

            sequentialPosition = offset;
        }

        source.ReadExactly(destination, what);
        sequentialPosition += destination.Length;
    }

    private TimeSpan TicksToTimeSpan(long ticks)
    {
        if (Timescale == CbvFormat.DefaultTimescale) return TimeSpan.FromTicks(ticks);
        return TimeSpan.FromTicks((long)Math.Round(ticks * (double)TimeSpan.TicksPerSecond / Timescale));
    }

    private long TimeSpanToTicks(TimeSpan value)
    {
        if (Timescale == CbvFormat.DefaultTimescale) return value.Ticks;
        return (long)Math.Round(value.Ticks * (double)Timescale / TimeSpan.TicksPerSecond);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CbvReader));
    }
}
