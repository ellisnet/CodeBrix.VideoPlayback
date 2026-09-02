using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// Writes an Ogg Vorbis or Ogg Opus file from the coded audio packets a container gave up, so that a stream
/// FFmpeg cannot reach inside its original container can be handed to FFmpeg after all.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="OggAudioStream" />, and the two together make the bespoke container a
/// TWO-WAY street: what <see cref="CodeBrix.VideoPlayback.Containers.Cbv.CbvAuthoring" /> muxes IN out of an
/// IVF and an Ogg file can now be written back OUT to an IVF and an Ogg file, with nothing re-encoded and no
/// new dependency.
/// </para>
/// <para>
/// THE SETUP HEADERS. A Vorbis stream's three Xiph headers come out of the track's codec-private data, split
/// by <see cref="OggAudioStream.SplitXiphCodecPrivate" /> - the exact inverse of the
/// <see cref="OggAudioStream.BuildXiphCodecPrivate" /> a container reader used to pack them. An Opus stream's
/// codec-private data IS its <c>OpusHead</c>; its <c>OpusTags</c> comment header is SYNTHESISED here, because
/// no media container stores one and an Ogg Opus file is invalid without it.
/// </para>
/// <para>
/// THE GRANULE POSITIONS are computed from the packets' own timestamps, in the units each format counts in: a
/// Vorbis granule counts samples at the track's own sample rate from zero, an Opus granule counts samples at
/// 48 kHz and is offset by the encoder's pre-skip. They never move backwards, whatever the timestamps do.
/// </para>
/// <para>
/// A SOURCE'S TRAILING TRIM IS THE ONE THING TIMESTAMPS CANNOT SAY, and <see cref="Complete(long)" /> is how
/// it is said. An encoder's tail padding lives in nothing but the FINAL PAGE'S granule position, which it
/// makes SMALLER than decoding every packet would produce; a granule derived from the last packet's end
/// timestamp is by definition the untrimmed number, so a file written with plain <see cref="Complete()" />
/// declares no end trim at all. State the final granule and the trim survives the round trip - along with the
/// priming, which travels in the <c>OpusHead</c> and never needed help.
/// </para>
/// <para>
/// <see cref="Complete()" /> writes the last page and sets the end-of-stream flag; a file whose writer was
/// never completed is one strict readers refuse. This is an authoring-time type - playback never writes
/// anything.
/// </para>
/// </remarks>
public sealed class OggAudioWriter : IDisposable
{
    private const int OpusGranuleSampleRate = 48_000;
    private const int OpusHeadMinimumLength = 19;
    private const int OpusHeadPreSkipOffset = 10;
    private const string Vendor = "CodeBrix.VideoPlayback";

    /// <summary>
    /// The logical-stream serial number every file this writer creates carries.
    /// </summary>
    /// <remarks>
    /// One value is enough because each file holds exactly one logical stream, and a serial number only ever
    /// has to be unique WITHIN a physical bitstream. A caller who needs several streams interleaved into one
    /// file writes the pages with <see cref="OggStreamWriter" /> directly and chooses its own numbers.
    /// </remarks>
    public const uint DefaultSerialNumber = 0x43425650;

    private readonly OggStreamWriter stream;
    private readonly int granuleSampleRate;
    private readonly long granuleOffset;

    private long lastGranule;
    private bool completed;
    private bool disposed;

    private OggAudioWriter(OggStreamWriter stream, string codecId, int granuleSampleRate, long granuleOffset)
    {
        this.stream = stream;
        this.granuleSampleRate = granuleSampleRate;
        this.granuleOffset = granuleOffset;

        CodecId = codecId;
        lastGranule = granuleOffset;
    }

    /// <summary>Creates an Ogg Vorbis file and writes its three setup headers.</summary>
    /// <param name="path">The file to create, overwriting anything already there.</param>
    /// <param name="codecPrivate">The track's Xiph-laced codec-private data, as a container stored it.</param>
    /// <param name="sampleRate">The track's sample rate in hertz, which its granules count in.</param>
    /// <returns>A writer ready for audio packets.</returns>
    /// <exception cref="ArgumentException">The path is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate is not positive.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The codec-private data is not three Xiph-laced Vorbis headers.
    /// </exception>
    public static OggAudioWriter CreateVorbis(string path, ReadOnlySpan<byte> codecPrivate, int sampleRate)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An Ogg Vorbis file needs a path to be written to.", nameof(path));
        }

        return CreateVorbis(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), codecPrivate, sampleRate);
    }

    /// <summary>Creates an Ogg Vorbis stream and writes its three setup headers.</summary>
    /// <param name="output">Where the pages go. It must be writable.</param>
    /// <param name="codecPrivate">The track's Xiph-laced codec-private data, as a container stored it.</param>
    /// <param name="sampleRate">The track's sample rate in hertz, which its granules count in.</param>
    /// <param name="leaveOutputOpen">True to leave the stream open when this writer is disposed.</param>
    /// <returns>A writer ready for audio packets.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate is not positive.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The codec-private data is not three Xiph-laced Vorbis headers.
    /// </exception>
    public static OggAudioWriter CreateVorbis(
        Stream output,
        ReadOnlySpan<byte> codecPrivate,
        int sampleRate,
        bool leaveOutputOpen = false)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "A Vorbis sample rate has to be positive; its granules count in it.");
        }

        OggAudioStream.SplitXiphCodecPrivate(
            codecPrivate, out byte[] identification, out byte[] comment, out byte[] setup);

        OggStreamWriter pages = new OggStreamWriter(output, DefaultSerialNumber, leaveOutputOpen);

        // The identification header has to sit ALONE on the first page; the other two follow on pages of
        // their own, so that the first audio packet starts a fresh page.
        pages.WritePacket(identification, 0);
        pages.FlushPage();
        pages.WritePacket(comment, 0);
        pages.WritePacket(setup, 0);
        pages.FlushPage();

        return new OggAudioWriter(pages, VideoCodecIds.Vorbis, sampleRate, 0);
    }

    /// <summary>Creates an Ogg Opus file and writes its identification and comment headers.</summary>
    /// <param name="path">The file to create, overwriting anything already there.</param>
    /// <param name="codecPrivate">The track's <c>OpusHead</c> identification header.</param>
    /// <param name="preSkipSamples">
    /// The encoder's pre-skip in 48 kHz samples, which every Opus granule is offset by, or zero to take it
    /// from the <c>OpusHead</c> itself.
    /// </param>
    /// <returns>A writer ready for audio packets.</returns>
    /// <exception cref="ArgumentException">The path is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pre-skip is negative.</exception>
    /// <exception cref="VideoPlaybackException">The codec-private data is not an <c>OpusHead</c>.</exception>
    public static OggAudioWriter CreateOpus(string path, ReadOnlySpan<byte> codecPrivate, int preSkipSamples = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An Ogg Opus file needs a path to be written to.", nameof(path));
        }

        return CreateOpus(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), codecPrivate, preSkipSamples);
    }

    /// <summary>Creates an Ogg Opus stream and writes its identification and comment headers.</summary>
    /// <param name="output">Where the pages go. It must be writable.</param>
    /// <param name="codecPrivate">The track's <c>OpusHead</c> identification header.</param>
    /// <param name="preSkipSamples">
    /// The encoder's pre-skip in 48 kHz samples, which every Opus granule is offset by, or zero to take it
    /// from the <c>OpusHead</c> itself.
    /// </param>
    /// <param name="leaveOutputOpen">True to leave the stream open when this writer is disposed.</param>
    /// <returns>A writer ready for audio packets.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pre-skip is negative.</exception>
    /// <exception cref="VideoPlaybackException">The codec-private data is not an <c>OpusHead</c>.</exception>
    public static OggAudioWriter CreateOpus(
        Stream output,
        ReadOnlySpan<byte> codecPrivate,
        int preSkipSamples = 0,
        bool leaveOutputOpen = false)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));

        if (preSkipSamples < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preSkipSamples), preSkipSamples, "An Opus pre-skip cannot be negative.");
        }

        if (codecPrivate.Length < OpusHeadMinimumLength
            || !codecPrivate.Slice(0, 8).SequenceEqual("OpusHead"u8))
        {
            throw new VideoPlaybackException(
                "The codec-private data offered for an Ogg Opus file is not an 'OpusHead' identification header. "
                + "An Opus track's codec-private data IS its OpusHead, exactly as the container stored it.");
        }

        int preSkip = preSkipSamples > 0
            ? preSkipSamples
            : BinaryPrimitives.ReadUInt16LittleEndian(codecPrivate.Slice(OpusHeadPreSkipOffset, 2));

        OggStreamWriter pages = new OggStreamWriter(output, DefaultSerialNumber, leaveOutputOpen);

        // Both header packets sit alone on their own pages, which is what an Ogg Opus reader looks for.
        pages.WritePacket(codecPrivate, 0);
        pages.FlushPage();
        pages.WritePacket(BuildOpusTags(), 0);
        pages.FlushPage();

        return new OggAudioWriter(pages, VideoCodecIds.Opus, OpusGranuleSampleRate, preSkip);
    }

    /// <summary>The codec identifier being written - <c>opus</c> or <c>vorbis</c>. See <see cref="VideoCodecIds" />.</summary>
    public string CodecId { get; }

    /// <summary>The rate the granule positions count samples at: 48000 for Opus, the track's rate for Vorbis.</summary>
    public int GranuleSampleRate => granuleSampleRate;

    /// <summary>How many audio packets have been written so far. The setup headers are not counted.</summary>
    public long PacketsWritten { get; private set; }

    /// <summary>Writes one coded audio packet.</summary>
    /// <param name="data">The packet's bytes, exactly as the container stored them.</param>
    /// <param name="endTimestamp">
    /// Where the packet ENDS in the media's own timeline - for a packet read with
    /// <see cref="OggAudioStream.TryReadPacket" />, its <see cref="OggAudioPacket.Timestamp" /> plus its
    /// <see cref="OggAudioPacket.Duration" />. The granule position is computed from it, and never allowed to
    /// move backwards.
    /// </param>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void WritePacket(ReadOnlySpan<byte> data, TimeSpan endTimestamp)
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException(
                "This Ogg audio file has already been completed; nothing more can go in it.");
        }

        long granule = granuleOffset + SamplesAt(endTimestamp);
        if (granule < lastGranule) granule = lastGranule;

        lastGranule = granule;
        stream.WritePacket(data, granule);
        PacketsWritten++;
    }

    /// <summary>Writes the last page, with the end-of-stream flag set.</summary>
    /// <remarks>
    /// The last page carries the granule position the last packet's own end timestamp produced, so the file
    /// declares every sample its packets decode to and NO end trim. Use <see cref="Complete(long)" /> to
    /// state a source's trailing padding.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Complete()
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This Ogg audio file has already been completed.");
        }

        stream.Complete();
        completed = true;
    }

    /// <summary>
    /// Writes the last page, with the end-of-stream flag set and the granule position you state - which is
    /// how a stream declares that it ends BEFORE its packets do.
    /// </summary>
    /// <param name="finalGranulePosition">
    /// Where the audio ENDS, as an absolute granule position on the same scale as the ones this writer
    /// derives: samples at <see cref="GranuleSampleRate" />, counting the Opus pre-skip in. Say it SMALLER
    /// than decoding every packet would produce and the difference is the stream's end trim.
    /// </param>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. Nothing but the final page's granule position carries an encoder's tail padding -
    /// there is no header field for it in either format - and a granule worked out from a packet's end
    /// timestamp can only ever be the untrimmed number. This is the one field of an Ogg round trip that the
    /// timestamps cannot reproduce; state it and there is no lossy field left.
    /// </para>
    /// <para>
    /// THE RULE THIS ENFORCES, and why it is that rule. The position may not be NEGATIVE: <c>-1</c> in a page
    /// header means "no packet ends on this page", not a position. It may not be GREATER than the granule the
    /// packets' own timestamps arrive at, because that would declare audio no packet in the file carries. It
    /// may be SMALLER, by any amount, because that is exactly what end trimming IS in both formats - a final
    /// granule position short of what the packets decode to, with the difference discarded - and how much of
    /// the tail an encoder padded is not this writer's business to second-guess. The one floor is the
    /// format's own: granule positions never go backwards, so a value below one an already-written page
    /// carries is refused.
    /// </para>
    /// <para>
    /// WHAT TO STATE WHEN COPYING A STREAM. For audio read with <see cref="OggAudioStream" />, the value that
    /// reproduces the source exactly is the sum of the packets'
    /// <see cref="OggAudioPacket.SampleCount" /> less its
    /// <see cref="OggAudioStream.TrailingTrimSamples" /> - which is the granule the source's own last page
    /// carried. Read the file back and its trim and its duration are the ones it started with.
    /// </para>
    /// <para>
    /// The value is checked BEFORE anything is written, so a refused call leaves the file exactly as it was
    /// and it can still be completed with a value that passes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The position is negative, beyond what the packets carry, or below one an earlier page already carries.
    /// </exception>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Complete(long finalGranulePosition)
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This Ogg audio file has already been completed.");
        }

        if (finalGranulePosition < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalGranulePosition),
                finalGranulePosition,
                "A stated final granule position cannot be negative. In an Ogg page header -1 means 'no packet "
                + "ends on this page', which is not something a stream can end at.");
        }

        if (finalGranulePosition > lastGranule)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalGranulePosition),
                finalGranulePosition,
                $"The packets written to this file end at granule {lastGranule}, so a final granule position of "
                + $"{finalGranulePosition} would declare audio the file does not carry. A stated final granule "
                + "may be SMALLER than that - the difference is the stream's end trim - but never larger.");
        }

        stream.Complete(finalGranulePosition);
        completed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    // Whole-number arithmetic rather than seconds as a double: a granule is a sample count and the rounding
    // has to land on the sample the timestamp names, not near it.
    private long SamplesAt(TimeSpan timestamp)
    {
        long ticks = timestamp.Ticks;
        if (ticks <= 0) return 0;

        return ((ticks * granuleSampleRate) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond;
    }

    // No container stores an Opus comment header, and an Ogg Opus file is invalid without one, so the
    // smallest legal one is written: the magic, a vendor string, and a count of zero user comments.
    private static byte[] BuildOpusTags()
    {
        byte[] vendor = Encoding.UTF8.GetBytes(Vendor);
        byte[] tags = new byte[8 + 4 + vendor.Length + 4];

        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8, 4), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(12 + vendor.Length, 4), 0);

        return tags;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OggAudioWriter));
    }
}
