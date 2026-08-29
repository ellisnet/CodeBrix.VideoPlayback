using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Internal;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// Writes the bespoke <c>.cbv</c> container: declare the tracks, add the caption cues and chapters, write the
/// chunks in presentation order, and finish.
/// </summary>
/// <remarks>
/// <para>
/// The file it produces is index-first: the header and the whole index sit in front of every chunk, so a
/// reader knows the shape of the media - and every caption cue - before it has read a single byte of media
/// data. That is what makes a shipped clip start instantly and a seek cost one read.
/// </para>
/// <para>
/// Getting that layout without buffering the whole file in memory takes one temporary file. Chunks are
/// written to it as they arrive while the index accumulates; <see cref="Complete" /> then writes the header
/// and the index to the real output and copies the chunks in behind them, adjusting each index offset by the
/// distance the chunks moved.
/// </para>
/// <para>
/// The complete layout is written out in <c>CBV-FORMAT.txt</c> at the root of this library's repository.
/// </para>
/// </remarks>
public sealed class CbvMuxer : IDisposable
{
    private readonly Stream output;
    private readonly bool leaveOutputOpen;
    private readonly string temporaryPath;
    private readonly FileStream temporary;
    private readonly List<TrackDefinition> trackDefinitions = new List<TrackDefinition>();
    private readonly List<Chapter> chapters = new List<Chapter>();
    private readonly List<CbvIndexEntry> entries = new List<CbvIndexEntry>();

    private long chunkBytes;
    private long maximumEndTicks;
    private bool completed;
    private bool disposed;

    /// <summary>Creates a muxer writing to a stream.</summary>
    /// <param name="output">Where the finished file is written. It must be writable.</param>
    /// <param name="leaveOutputOpen">True to leave the stream open when the muxer is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot be written to.</exception>
    public CbvMuxer(Stream output, bool leaveOutputOpen = false)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));

        this.output = output;
        this.leaveOutputOpen = leaveOutputOpen;

        temporaryPath = Path.Combine(Path.GetTempPath(), $"codebrix-cbv-{Guid.NewGuid():N}.chunks");
        temporary = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.DeleteOnClose);
    }

    /// <summary>Creates a muxer writing to a file, replacing anything already there.</summary>
    /// <param name="path">The path of the file to write.</param>
    /// <returns>A muxer ready to take track declarations.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    public static CbvMuxer Create(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        return new CbvMuxer(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None));
    }

    /// <summary>How many ticks the written file counts to the second.</summary>
    public uint Timescale => CbvFormat.DefaultTimescale;

    /// <summary>How many chunks have been written so far.</summary>
    public int ChunkCount => entries.Count;

    /// <summary>Declares a video track.</summary>
    /// <param name="codecId">The codec identifier - see <see cref="VideoCodecIds" />.</param>
    /// <param name="codecPrivate">The codec's initialisation data, such as an AV1 configuration record.</param>
    /// <param name="width">The coded width in pixels.</param>
    /// <param name="height">The coded height in pixels.</param>
    /// <param name="displayWidth">The width to show frames at, or 0 to use <paramref name="width" />.</param>
    /// <param name="displayHeight">The height to show frames at, or 0 to use <paramref name="height" />.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="hdr">Mastering metadata, or null when the content is not high dynamic range.</param>
    /// <param name="frameDuration">
    /// How long each frame is shown for when the rate is constant, or <see cref="TimeSpan.Zero" /> when it
    /// varies.
    /// </param>
    /// <param name="language">A BCP 47 language tag, or null.</param>
    /// <param name="name">A name for the track, or null.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>The identifier the track was given, for use with <see cref="WriteChunk" />.</returns>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="VideoPlaybackException">A field will not fit the format, or a chunk has already been written.</exception>
    public int AddVideoTrack(
        string codecId,
        ReadOnlyMemory<byte> codecPrivate,
        int width,
        int height,
        int displayWidth = 0,
        int displayHeight = 0,
        int bitDepth = 8,
        VideoPixelLayout layout = VideoPixelLayout.I420,
        VideoColorInfo color = default,
        HdrMetadata hdr = null,
        TimeSpan frameDuration = default,
        string language = null,
        string name = null,
        CbvTrackFlags flags = CbvTrackFlags.None)
    {
        TrackDefinition track = BeginTrack(codecId, 1, language, name, flags, codecPrivate);

        using MemoryStream body = new MemoryStream();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, (uint)height);
        WriteUInt32(body, (uint)(displayWidth > 0 ? displayWidth : width));
        WriteUInt32(body, (uint)(displayHeight > 0 ? displayHeight : height));
        body.WriteByte((byte)bitDepth);
        body.WriteByte((byte)layout);
        body.WriteByte((byte)color.Primaries);
        body.WriteByte((byte)color.Transfer);
        body.WriteByte((byte)color.Matrix);
        body.WriteByte((byte)color.Range);
        body.WriteByte((byte)color.ChromaSiting);
        body.WriteByte((byte)(hdr != null ? 1 : 0));
        WriteInt64(body, frameDuration.Ticks);

        if (hdr != null)
        {
            WriteDouble(body, hdr.RedPrimaryX);
            WriteDouble(body, hdr.RedPrimaryY);
            WriteDouble(body, hdr.GreenPrimaryX);
            WriteDouble(body, hdr.GreenPrimaryY);
            WriteDouble(body, hdr.BluePrimaryX);
            WriteDouble(body, hdr.BluePrimaryY);
            WriteDouble(body, hdr.WhitePointX);
            WriteDouble(body, hdr.WhitePointY);
            WriteDouble(body, hdr.MaxLuminance);
            WriteDouble(body, hdr.MinLuminance);
            WriteUInt32(body, (uint)hdr.MaxContentLightLevel);
            WriteUInt32(body, (uint)hdr.MaxFrameAverageLightLevel);
        }

        track.Body = body.ToArray();
        trackDefinitions.Add(track);
        return track.Id;
    }

    /// <summary>Declares an audio track.</summary>
    /// <param name="codecId">The codec identifier - see <see cref="VideoCodecIds" />.</param>
    /// <param name="codecPrivate">
    /// The codec's initialisation data: the <c>OpusHead</c> bytes for Opus, the three Xiph-laced setup headers
    /// for Vorbis.
    /// </param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="channels">How many channels.</param>
    /// <param name="preSkipSamples">How many samples per channel of codec priming to discard at the start.</param>
    /// <param name="trailingTrimSamples">How many samples per channel to discard at the very end.</param>
    /// <param name="codecDelay">The same priming expressed as a duration, for a player that works in time.</param>
    /// <param name="seekPreRoll">How much audio to decode and discard after a seek before any is heard.</param>
    /// <param name="language">A BCP 47 language tag, or null.</param>
    /// <param name="name">A name for the track, or null.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>The identifier the track was given.</returns>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="VideoPlaybackException">A field will not fit the format, or a chunk has already been written.</exception>
    public int AddAudioTrack(
        string codecId,
        ReadOnlyMemory<byte> codecPrivate,
        int sampleRate,
        int channels,
        int preSkipSamples = 0,
        int trailingTrimSamples = 0,
        TimeSpan codecDelay = default,
        TimeSpan seekPreRoll = default,
        string language = null,
        string name = null,
        CbvTrackFlags flags = CbvTrackFlags.None)
    {
        if (preSkipSamples is < 0 or > ushort.MaxValue)
        {
            throw new VideoPlaybackException(
                $"A pre-skip of {preSkipSamples} samples does not fit the format's 16-bit field.");
        }

        TrackDefinition track = BeginTrack(codecId, 2, language, name, flags, codecPrivate);

        using MemoryStream body = new MemoryStream();
        WriteUInt32(body, (uint)sampleRate);
        body.WriteByte((byte)channels);
        WriteUInt16(body, (ushort)preSkipSamples);
        WriteUInt32(body, (uint)trailingTrimSamples);
        WriteInt64(body, codecDelay.Ticks);
        WriteInt64(body, seekPreRoll.Ticks);

        track.Body = body.ToArray();
        trackDefinitions.Add(track);
        return track.Id;
    }

    /// <summary>Declares a caption track and stores every one of its cues in the header.</summary>
    /// <param name="captions">The caption track, cues and all.</param>
    /// <returns>The identifier the track was given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="captions" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="VideoPlaybackException">A field will not fit the format, or a chunk has already been written.</exception>
    /// <remarks>
    /// Caption tracks produce no chunks: the whole track lives in the header region, which is what makes every
    /// cue available the instant the file is opened.
    /// </remarks>
    public int AddCaptionTrack(CaptionTrack captions)
    {
        if (captions == null) throw new ArgumentNullException(nameof(captions));

        string codecId = captions.Format switch
        {
            CaptionFormat.WebVtt => VideoCodecIds.WebVtt,
            CaptionFormat.SubRip => VideoCodecIds.SubRip,
            CaptionFormat.Ass => VideoCodecIds.Ass,
            _ => VideoCodecIds.WebVtt,
        };

        CbvTrackFlags flags = CbvTrackFlags.None;
        if (captions.IsDefault) flags |= CbvTrackFlags.Default;
        if (captions.IsForced) flags |= CbvTrackFlags.Forced;
        if (captions.IsHearingImpaired) flags |= CbvTrackFlags.HearingImpaired;

        TrackDefinition track = BeginTrack(codecId, 3, captions.Language, captions.Name, flags, ReadOnlyMemory<byte>.Empty);

        using MemoryStream body = new MemoryStream();
        IReadOnlyList<CaptionCue> cues = captions.Cues;
        WriteUInt32(body, (uint)cues.Count);

        foreach (CaptionCue cue in cues)
        {
            WriteInt64(body, cue.Start.Ticks);
            WriteInt64(body, cue.End.Ticks);
            WriteUtf8WithUInt16Length(body, cue.Settings, "a cue's settings");
            WriteUtf8WithUInt16Length(body, cue.Identifier, "a cue's identifier");

            byte[] text = Encoding.UTF8.GetBytes(cue.Text);
            WriteUInt32(body, (uint)text.Length);
            body.Write(text, 0, text.Length);

            if (cue.End.Ticks > maximumEndTicks) maximumEndTicks = cue.End.Ticks;
        }

        track.Body = body.ToArray();
        trackDefinitions.Add(track);
        return track.Id;
    }

    /// <summary>Adds chapters to the header region.</summary>
    /// <param name="newChapters">The chapters to add. They are stored in ascending order of start time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="newChapters" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    public void AddChapters(IEnumerable<Chapter> newChapters)
    {
        if (newChapters == null) throw new ArgumentNullException(nameof(newChapters));
        ThrowIfCompleted();

        foreach (Chapter chapter in newChapters)
        {
            if (chapter == null) continue;
            chapters.Add(chapter);
            if (chapter.End.Ticks > maximumEndTicks) maximumEndTicks = chapter.End.Ticks;
        }

        chapters.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    /// <summary>Writes one chunk of media data.</summary>
    /// <param name="trackId">The track the chunk belongs to, as returned when the track was declared.</param>
    /// <param name="data">The chunk's bytes - one coded video frame, or one audio packet.</param>
    /// <param name="timestamp">When the chunk is for, relative to the start of the media.</param>
    /// <param name="duration">How long it lasts, or <see cref="TimeSpan.Zero" /> when that is not known.</param>
    /// <param name="isKeyFrame">True when decoding may start at this chunk.</param>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="VideoPlaybackException">The track was never declared, or the chunk is unreasonably large.</exception>
    /// <remarks>
    /// Write chunks in ascending presentation order across all tracks. A reader plays them in the order they
    /// are stored, so interleaving them the way they will be consumed is what keeps a player's queues short.
    /// </remarks>
    public void WriteChunk(int trackId, ReadOnlySpan<byte> data, TimeSpan timestamp, TimeSpan duration, bool isKeyFrame)
    {
        ThrowIfCompleted();

        TrackDefinition track = FindTrack(trackId);
        if (track == null)
        {
            throw new VideoPlaybackException(
                $"No track {trackId} has been declared, so a chunk cannot be written for it.");
        }

        if (track.Kind == 3)
        {
            throw new VideoPlaybackException(
                $"Track {trackId} is a caption track, whose cues live in the header region; caption tracks carry "
                + "no chunks.");
        }

        if (data.Length > CbvFormat.MaximumChunkLength)
        {
            throw new VideoPlaybackException(
                $"A chunk of {data.Length} bytes is beyond the {CbvFormat.MaximumChunkLength}-byte limit the format "
                + "allows.");
        }

        Span<byte> header = stackalloc byte[CbvFormat.ChunkHeaderLength];
        header[0] = (byte)trackId;
        header[1] = (byte)(isKeyFrame ? CbvChunkFlags.KeyFrame : CbvChunkFlags.None);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(2, 4), (uint)data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(6, 8), timestamp.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(14, 8), duration.Ticks);

        entries.Add(new CbvIndexEntry(
            (byte)trackId,
            isKeyFrame ? CbvChunkFlags.KeyFrame : CbvChunkFlags.None,
            (uint)data.Length,
            (ulong)chunkBytes,
            timestamp.Ticks));

        temporary.Write(header);
        temporary.Write(data);
        chunkBytes += CbvFormat.ChunkHeaderLength + data.Length;

        long end = (timestamp + duration).Ticks;
        if (end > maximumEndTicks) maximumEndTicks = end;
    }

    /// <summary>Writes the header and the index, then copies the chunks in behind them.</summary>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="VideoPlaybackException">No track was ever declared.</exception>
    public void Complete()
    {
        ThrowIfCompleted();

        if (trackDefinitions.Count == 0)
        {
            throw new VideoPlaybackException("A bespoke file must declare at least one track.");
        }

        byte[] header = BuildHeader();
        byte[] index = BuildIndex(header.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)header.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12, 8), (ulong)header.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20, 8), (ulong)index.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(28, 8), (ulong)maximumEndTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), CbvFormat.DefaultTimescale);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), Crc32.Compute(index));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(CbvFormat.HeaderCrcOffset, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(CbvFormat.HeaderCrcOffset, 4),
            Crc32.Compute(header));

        output.Write(header, 0, header.Length);
        output.Write(index, 0, index.Length);

        temporary.Flush();
        temporary.Position = 0;
        temporary.CopyTo(output, 128 * 1024);
        output.Flush();

        completed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        temporary.Dispose();
        if (!leaveOutputOpen) output.Dispose();
    }

    private TrackDefinition BeginTrack(
        string codecId,
        byte kind,
        string language,
        string name,
        CbvTrackFlags flags,
        ReadOnlyMemory<byte> codecPrivate)
    {
        ThrowIfCompleted();

        if (entries.Count > 0)
        {
            throw new VideoPlaybackException(
                "Every track must be declared before the first chunk is written; the header region is laid out "
                + "in front of the chunks.");
        }

        if (trackDefinitions.Count >= 255)
        {
            throw new VideoPlaybackException("The format's track identifier is one byte, so there is room for 255 tracks.");
        }

        return new TrackDefinition
        {
            Id = trackDefinitions.Count + 1,
            Kind = kind,
            CodecId = codecId ?? string.Empty,
            Language = language ?? string.Empty,
            Name = name ?? string.Empty,
            Flags = flags,
            CodecPrivate = codecPrivate.ToArray(),
        };
    }

    private TrackDefinition FindTrack(int id)
    {
        foreach (TrackDefinition track in trackDefinitions)
        {
            if (track.Id == id) return track;
        }

        return null;
    }

    private byte[] BuildHeader()
    {
        using MemoryStream stream = new MemoryStream();
        stream.Write(CbvFormat.Magic);
        WriteUInt16(stream, CbvFormat.Version);
        WriteUInt16(stream, (ushort)(CbvHeaderFlags.HasIndex | CbvHeaderFlags.ChunksInPresentationOrder));

        Span<byte> placeholder = stackalloc byte[CbvFormat.FixedHeaderLength - 8];
        placeholder.Clear();
        stream.Write(placeholder);

        WriteUInt16(stream, (ushort)trackDefinitions.Count);

        byte[] codecField = new byte[CbvFormat.CodecIdFieldLength];
        byte[] languageField = new byte[CbvFormat.LanguageFieldLength];

        foreach (TrackDefinition track in trackDefinitions)
        {
            using MemoryStream entry = new MemoryStream();
            entry.WriteByte((byte)track.Id);
            entry.WriteByte(track.Kind);

            CbvFormat.WriteFixedAscii(track.CodecId, codecField, "codec identifier");
            entry.Write(codecField, 0, codecField.Length);

            CbvFormat.WriteFixedAscii(track.Language, languageField, "language tag");
            entry.Write(languageField, 0, languageField.Length);

            WriteUtf8WithUInt16Length(entry, track.Name, "a track name");
            entry.WriteByte((byte)track.Flags);
            WriteUInt32(entry, (uint)track.CodecPrivate.Length);
            entry.Write(track.CodecPrivate, 0, track.CodecPrivate.Length);
            entry.Write(track.Body, 0, track.Body.Length);

            byte[] bytes = entry.ToArray();
            WriteUInt32(stream, (uint)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        WriteUInt32(stream, (uint)chapters.Count);
        foreach (Chapter chapter in chapters)
        {
            WriteInt64(stream, chapter.Start.Ticks);
            WriteInt64(stream, chapter.End.Ticks);
            stream.WriteByte((byte)(chapter.IsHidden ? 1 : 0));
            WriteUInt16(stream, (ushort)chapter.Titles.Count);

            foreach (KeyValuePair<string, string> title in chapter.Titles)
            {
                CbvFormat.WriteFixedAscii(title.Key, languageField, "chapter title language tag");
                stream.Write(languageField, 0, languageField.Length);
                WriteUtf8WithUInt16Length(stream, title.Value, "a chapter title");
            }
        }

        return stream.ToArray();
    }

    private byte[] BuildIndex(int headerLength)
    {
        int length = 4 + (entries.Count * CbvFormat.IndexEntryLength);
        long chunkBase = headerLength + length;

        byte[] index = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(0, 4), (uint)entries.Count);

        int offset = 4;
        foreach (CbvIndexEntry entry in entries)
        {
            index[offset] = entry.TrackId;
            index[offset + 1] = (byte)entry.Flags;
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(offset + 2, 4), entry.Size);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + 6, 8), entry.Offset + (ulong)chunkBase);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(offset + 14, 8), entry.TimestampTicks);
            offset += CbvFormat.IndexEntryLength;
        }

        return index;
    }

    private void ThrowIfCompleted()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CbvMuxer));
        if (completed) throw new InvalidOperationException("This file has already been completed.");
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> scratch = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(scratch, value);
        stream.Write(scratch);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        stream.Write(scratch);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
        stream.Write(scratch);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(scratch, value);
        stream.Write(scratch);
    }

    private static void WriteUtf8WithUInt16Length(Stream stream, string value, string what)
    {
        byte[] bytes = string.IsNullOrEmpty(value) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new VideoPlaybackException(
                $"{what} is {bytes.Length} bytes long and the format's length field holds {ushort.MaxValue}.");
        }

        WriteUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class TrackDefinition
    {
        internal int Id { get; set; }

        internal byte Kind { get; set; }

        internal string CodecId { get; set; }

        internal string Language { get; set; }

        internal string Name { get; set; }

        internal CbvTrackFlags Flags { get; set; }

        internal byte[] CodecPrivate { get; set; }

        internal byte[] Body { get; set; }
    }
}
