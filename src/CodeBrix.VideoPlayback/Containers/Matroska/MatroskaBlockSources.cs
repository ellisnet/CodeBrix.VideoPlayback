using System;
using System.Collections.Generic;
using System.Text;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>One block on its way into a cluster.</summary>
internal sealed class MatroskaPendingBlock
{
    internal MatroskaMuxTrack Track;
    internal long TimestampMs;
    internal byte[] Data;
    internal bool IsKeyFrame;

    /// <summary>The block's duration in milliseconds, written as a <c>BlockDuration</c>; -1 for none.</summary>
    internal long DurationMs = -1;

    /// <summary>Nanoseconds of padding at the end of the block, written as a <c>DiscardPadding</c>; 0 for none.</summary>
    internal long DiscardPaddingNs;

    /// <summary>True when the block needs a <c>BlockGroup</c> rather than a <c>SimpleBlock</c>.</summary>
    internal bool NeedsBlockGroup => DurationMs >= 0 || DiscardPaddingNs != 0;
}

/// <summary>A stream of blocks for one track, in timestamp order, read one ahead.</summary>
internal abstract class MatroskaBlockSource : IDisposable
{
    /// <summary>The track the blocks belong to.</summary>
    internal MatroskaMuxTrack Track { get; init; }

    /// <summary>The block at the head of the stream, or null once the stream is exhausted.</summary>
    internal MatroskaPendingBlock Current { get; private protected set; }

    /// <summary>Moves to the next block.</summary>
    internal abstract void MoveNext();

    /// <summary>The end of the track's presentation, known once the stream is exhausted.</summary>
    internal TimeSpan PresentationEnd { get; private protected set; }

    /// <summary>Releases whatever the source reads from.</summary>
    public virtual void Dispose()
    {
    }

    /// <summary>Rounds a time to whole milliseconds, the unit every block timestamp is written in.</summary>
    /// <param name="value">The time.</param>
    /// <returns>Milliseconds.</returns>
    internal static long ToMilliseconds(TimeSpan value) =>
        value.Ticks >= 0
            ? (value.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond
            : -((-value.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond);
}

/// <summary>The frames of an IVF file holding AV1, one block per temporal unit.</summary>
internal sealed class MatroskaVideoSource : MatroskaBlockSource
{
    private readonly IvfReader reader;
    private readonly string path;
    private long firstRaw = long.MinValue;
    private long previousRaw = long.MinValue;
    private int frames;

    internal MatroskaVideoSource(string path, MatroskaMuxTrack track)
    {
        this.path = path;
        Track = track;
        reader = new IvfReader(new Sources.FileMediaSource(path));

        try
        {
            if (!string.Equals(reader.FourCharacterCode, "AV01", StringComparison.OrdinalIgnoreCase))
            {
                throw new VideoPlaybackException(
                    $"'{path}' is an IVF file carrying '{reader.FourCharacterCode}'. This muxer writes AV1 video, "
                    + "whose four-character code is 'AV01'.");
            }

            if (!reader.TryReadFrame(out ReadOnlyMemory<byte> data, out _, out long raw))
            {
                throw new VideoPlaybackException($"'{path}' is an IVF file with no frames in it, so there is no video to write.");
            }

            byte[] copy = data.ToArray();
            if (!Av1Bitstream.TryReadSequenceHeader(copy, out Av1SequenceHeader header, out int start, out int length))
            {
                throw new VideoPlaybackException(
                    $"'{path}' carries no AV1 sequence header in its first frame, so no codec configuration record can "
                    + "be built for it. The first frame of an elementary stream must carry one.");
            }

            if (!Av1Bitstream.IsKeyFrame(copy, header))
            {
                throw new VideoPlaybackException(
                    $"The first frame of '{path}' is not a key frame, so nothing could start decoding the stream. "
                    + "An elementary stream must begin with one.");
            }

            track.SequenceHeader = header;
            track.CodecPrivate = Av1Bitstream.BuildCodecConfigurationRecord(header, copy.AsSpan(start, length));
            track.PixelWidth = reader.Width > 0 ? reader.Width : header.MaxFrameWidth;
            track.PixelHeight = reader.Height > 0 ? reader.Height : header.MaxFrameHeight;

            if (raw < 0)
            {
                throw new VideoPlaybackException(
                    $"The first frame of '{path}' has the negative timestamp {raw}; a stream must start at or after zero.");
            }

            Accept(copy, raw, true);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The IVF time base's numerator.</summary>
    internal uint TimeBaseNumerator => reader.TimeBaseNumerator;

    /// <summary>The IVF time base's denominator.</summary>
    internal uint TimeBaseDenominator => reader.TimeBaseDenominator;

    internal override void MoveNext()
    {
        if (!reader.TryReadFrame(out ReadOnlyMemory<byte> data, out _, out long raw))
        {
            Finish();
            return;
        }

        if (raw <= previousRaw)
        {
            throw new VideoPlaybackException(
                $"Frame {frames} of '{path}' has the timestamp {raw}, which does not come after the previous frame's "
                + $"{previousRaw}. IVF frames must be stored in presentation order with rising timestamps.");
        }

        byte[] copy = data.ToArray();
        Accept(copy, raw, Av1Bitstream.IsKeyFrame(copy, Track.SequenceHeader));
    }

    public override void Dispose() => reader.Dispose();

    /// <summary>
    /// Removes the units the AV1-in-Matroska mapping says a block should not carry - temporal delimiters, padding,
    /// redundant frame headers and tile lists - exactly as FFmpeg's muxer does, so the blocks are byte-identical
    /// to what it writes from the same stream. A block IS one temporal unit, so the delimiter says nothing.
    /// </summary>
    /// <param name="temporalUnit">One temporal unit as the IVF file stored it.</param>
    /// <returns>The unit's bytes without the dropped units; the same array when nothing was dropped.</returns>
    internal static byte[] FilterUnits(byte[] temporalUnit)
    {
        IReadOnlyList<Av1Bitstream.ObuSpan> units = Av1Bitstream.ReadUnits(temporalUnit);

        int kept = 0;
        foreach (Av1Bitstream.ObuSpan unit in units)
        {
            if (!IsDropped(unit.Type)) kept += unit.Length;
        }

        if (kept == temporalUnit.Length || kept == 0) return temporalUnit;

        byte[] filtered = new byte[kept];
        int offset = 0;
        foreach (Av1Bitstream.ObuSpan unit in units)
        {
            if (IsDropped(unit.Type)) continue;
            Buffer.BlockCopy(temporalUnit, unit.Start, filtered, offset, unit.Length);
            offset += unit.Length;
        }

        return filtered;
    }

    private static bool IsDropped(Av1ObuType type) =>
        type == Av1ObuType.TemporalDelimiter
        || type == Av1ObuType.Padding
        || type == Av1ObuType.RedundantFrameHeader
        || type == Av1ObuType.TileList;

    private void Accept(byte[] data, long raw, bool isKey)
    {
        data = FilterUnits(data);

        if (firstRaw == long.MinValue) firstRaw = raw;
        previousRaw = raw;
        frames++;

        Current = new MatroskaPendingBlock
        {
            Track = Track,
            TimestampMs = RawToRounded(raw, 1000),
            Data = data,
            IsKeyFrame = isKey,
        };
    }

    private void Finish()
    {
        Current = null;

        // The frame period is the average step across the whole stream, worked out in the time base's own
        // units before anything is rounded: an IVF file with a millisecond time base stores a 30-per-second
        // stream as steps of 33 and 34, and the average recovers the 33.333... the encoder meant. A single
        // frame lasts one tick of the time base.
        decimal periodRaw = frames > 1 ? (decimal)(previousRaw - firstRaw) / (frames - 1) : 1m;

        Track.DefaultDurationNs = Round(periodRaw * reader.TimeBaseNumerator * 1_000_000_000 / reader.TimeBaseDenominator);
        PresentationEnd = TimeSpan.FromTicks(
            Round((previousRaw + periodRaw) * reader.TimeBaseNumerator * TimeSpan.TicksPerSecond / reader.TimeBaseDenominator));
    }

    private static long Round(decimal value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private long RawToRounded(long raw, long unitsPerSecond)
    {
        decimal value = (decimal)raw * reader.TimeBaseNumerator * unitsPerSecond / reader.TimeBaseDenominator;
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}

/// <summary>The packets of an Ogg Opus or Ogg Vorbis file.</summary>
internal sealed class MatroskaOggAudioSource : MatroskaBlockSource
{
    private readonly OggAudioStream stream;
    private OggAudioPacket next;
    private bool haveNext;
    private TimeSpan rawEnd;

    internal MatroskaOggAudioSource(string path, MatroskaMuxTrack track)
    {
        Track = track;
        stream = OggAudioStream.Open(path);

        try
        {
            bool opus = string.Equals(stream.CodecId, VideoCodecIds.Opus, StringComparison.Ordinal);
            track.CodecId = opus ? "A_OPUS" : "A_VORBIS";
            track.CodecPrivate = stream.CodecPrivate.ToArray();
            track.SampleRate = opus ? OpusPacketDuration.SampleRate : stream.SampleRate;
            track.Channels = stream.Channels;
            track.CodecDelayNs = opus ? SamplesToNanoseconds(stream.PreSkipSamples, OpusPacketDuration.SampleRate) : 0;
            track.SeekPreRollNs = opus ? MatroskaMuxer.OpusSeekPreRollNanoseconds : 0;

            haveNext = stream.TryReadPacket(out next);
            if (!haveNext)
            {
                throw new VideoPlaybackException($"'{path}' carries audio headers but no audio packets.");
            }

            MoveNext();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal override void MoveNext()
    {
        if (!haveNext)
        {
            Current = null;
            long trimNs = Track.CodecId == "A_OPUS"
                ? SamplesToNanoseconds(stream.TrailingTrimSamples, OpusPacketDuration.SampleRate)
                : 0;
            PresentationEnd = rawEnd - TimeSpan.FromTicks((Track.CodecDelayNs + trimNs) / 100);
            return;
        }

        OggAudioPacket packet = next;
        haveNext = stream.TryReadPacket(out next);
        rawEnd = packet.Timestamp + packet.Duration;

        MatroskaPendingBlock block = new MatroskaPendingBlock
        {
            Track = Track,
            TimestampMs = ToMilliseconds(packet.Timestamp),
            Data = packet.Data.ToArray(),
            IsKeyFrame = true,
        };

        // The trim is only settled once the last page has been read, which is exactly when there is no next packet.
        if (!haveNext && Track.CodecId == "A_OPUS" && stream.TrailingTrimSamples > 0)
        {
            block.DiscardPaddingNs = SamplesToNanoseconds(stream.TrailingTrimSamples, OpusPacketDuration.SampleRate);
        }

        Current = block;
    }

    public override void Dispose() => stream.Dispose();

    internal static long SamplesToNanoseconds(long samples, int rate) =>
        rate <= 0 ? 0 : (long)Math.Round(samples * 1_000_000_000.0 / rate, MidpointRounding.AwayFromZero);
}

/// <summary>Audio packets handed over ready-made.</summary>
internal sealed class MatroskaPacketAudioSource : MatroskaBlockSource
{
    private readonly MatroskaPacketAudioInput input;
    private int index;

    internal MatroskaPacketAudioSource(MatroskaPacketAudioInput input, MatroskaMuxTrack track)
    {
        this.input = input;
        Track = track;

        if (input.Packets.Count == 0)
        {
            throw new VideoPlaybackException("The packet audio input carries no packets, so there is no audio to write.");
        }

        if (input.TrailingTrimSamples < 0)
        {
            throw new VideoPlaybackException(
                $"The packet audio input declares a trailing trim of {input.TrailingTrimSamples} samples; a trim "
                + "cannot be negative.");
        }

        for (int i = 1; i < input.Packets.Count; i++)
        {
            if (input.Packets[i].Timestamp < input.Packets[i - 1].Timestamp)
            {
                throw new VideoPlaybackException(
                    $"Audio packet {i} is timed at {input.Packets[i].Timestamp}, before packet {i - 1} at "
                    + $"{input.Packets[i - 1].Timestamp}. Packets must be handed over in timestamp order.");
            }
        }

        index = -1;
        MoveNext();
    }

    internal override void MoveNext()
    {
        index++;
        if (index >= input.Packets.Count)
        {
            Current = null;
            MatroskaAudioPacket last = input.Packets[input.Packets.Count - 1];
            long trimNs = MatroskaOggAudioSource.SamplesToNanoseconds(input.TrailingTrimSamples, Track.SampleRate);
            PresentationEnd = last.Timestamp + last.Duration - TimeSpan.FromTicks((Track.CodecDelayNs + trimNs) / 100);
            return;
        }

        MatroskaAudioPacket packet = input.Packets[index];
        MatroskaPendingBlock block = new MatroskaPendingBlock
        {
            Track = Track,
            TimestampMs = ToMilliseconds(packet.Timestamp),
            Data = packet.Data,
            IsKeyFrame = true,
        };

        if (index == input.Packets.Count - 1 && input.TrailingTrimSamples > 0)
        {
            block.DiscardPaddingNs = MatroskaOggAudioSource.SamplesToNanoseconds(input.TrailingTrimSamples, Track.SampleRate);
        }

        Current = block;
    }
}

/// <summary>The cues of one caption track, one <c>BlockGroup</c> per cue.</summary>
internal sealed class MatroskaCaptionSource : MatroskaBlockSource
{
    private readonly IReadOnlyList<CaptionCue> cues;
    private readonly bool carryMetadata;
    private int index;

    internal MatroskaCaptionSource(CaptionTrack captions, MatroskaMuxTrack track, string path)
    {
        Track = track;
        Path = path;
        carryMetadata = captions.Format == CaptionFormat.WebVtt;

        List<CaptionCue> ordered = new List<CaptionCue>(captions.Cues);

        // A stable sort: cues that start together keep the order the file gave them.
        List<KeyValuePair<int, CaptionCue>> keyed = new List<KeyValuePair<int, CaptionCue>>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) keyed.Add(new KeyValuePair<int, CaptionCue>(i, ordered[i]));
        keyed.Sort((a, b) =>
        {
            int byStart = a.Value.Start.CompareTo(b.Value.Start);
            return byStart != 0 ? byStart : a.Key.CompareTo(b.Key);
        });

        List<CaptionCue> sorted = new List<CaptionCue>(keyed.Count);
        foreach (KeyValuePair<int, CaptionCue> entry in keyed)
        {
            CaptionCue cue = entry.Value;
            if (cue.Start < TimeSpan.Zero)
            {
                throw new VideoPlaybackException(
                    $"'{path}' has a cue starting at {cue.Start}, before the start of the media.");
            }

            if (ToMilliseconds(cue.End) <= ToMilliseconds(cue.Start))
            {
                throw new VideoPlaybackException(
                    $"'{path}' has a cue at {cue.Start} that ends at {cue.End}; a cue must last at least a "
                    + "millisecond, which is the finest time a block can state.");
            }

            sorted.Add(cue);
        }

        cues = sorted;
        index = -1;
        MoveNext();
    }

    /// <summary>The caption file the cues came from.</summary>
    internal string Path { get; }

    /// <summary>The cues, in the order they are written.</summary>
    internal IReadOnlyList<CaptionCue> Cues => cues;

    internal override void MoveNext()
    {
        index++;
        if (index >= cues.Count)
        {
            Current = null;
            PresentationEnd = cues.Count == 0 ? TimeSpan.Zero : cues[cues.Count - 1].End;
            return;
        }

        CaptionCue cue = cues[index];
        long startMs = ToMilliseconds(cue.Start);

        Current = new MatroskaPendingBlock
        {
            Track = Track,
            TimestampMs = startMs,
            Data = BuildPayload(cue, carryMetadata),
            IsKeyFrame = true,
            DurationMs = ToMilliseconds(cue.End) - startMs,
        };
    }

    /// <summary>
    /// Lays a cue out the way the WebM caption mapping does, and the way FFmpeg writes it: the cue identifier,
    /// a line feed, the cue settings list, a line feed, then the cue text. Both lines are always present, empty
    /// when the cue has neither.
    /// </summary>
    /// <param name="cue">The cue.</param>
    /// <param name="carryMetadata">False for a SubRip source, whose numbering is not a WebVTT identifier.</param>
    /// <returns>The block payload.</returns>
    internal static byte[] BuildPayload(CaptionCue cue, bool carryMetadata)
    {
        string identifier = carryMetadata ? cue.Identifier ?? string.Empty : string.Empty;
        string settings = carryMetadata ? cue.Settings ?? string.Empty : string.Empty;
        return Encoding.UTF8.GetBytes(identifier + "\n" + settings + "\n" + (cue.Text ?? string.Empty));
    }
}
