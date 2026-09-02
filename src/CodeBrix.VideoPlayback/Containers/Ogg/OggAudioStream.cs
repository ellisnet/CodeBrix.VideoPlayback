using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// Turns an Ogg Opus or Ogg Vorbis file into what a media container needs: the codec's initialisation data,
/// the audio packets, and a timestamp for each of them.
/// </summary>
/// <remarks>
/// <para>
/// This is the authoring-time bridge between an encoder's output and the bespoke muxer. It reads the codec's
/// setup headers, assembles them into the exact <c>codecPrivate</c> shape the audio decoders expect - the
/// <c>OpusHead</c> bytes for Opus, the three Xiph-laced setup headers for Vorbis - and then hands out the
/// audio packets with a timestamp on each.
/// </para>
/// <para>
/// <b>How the timestamps are worked out, and how exact they are.</b> Opus states a packet's duration in the
/// first byte of the packet, so Opus timestamps are exact to the sample. Vorbis does not: a packet's length
/// depends on a mode number that can only be read once the setup header's codebooks have been decoded, which
/// is a decoder's job and not a muxer's. So for Vorbis the granule position on each page - which IS exact -
/// is used to time the page, and the packets WITHIN a page share the page's duration equally. Page boundaries
/// are therefore exact and the intra-page error is bounded by one page, which is a few tens of milliseconds
/// in anything an encoder writes. The audio itself is unaffected: a player's clock counts the samples a
/// decoder actually produced, and these timestamps only decide where a seek lands.
/// </para>
/// <para>
/// Timestamps count from the stream's FIRST DECODED SAMPLE, priming included - the same convention a media
/// container uses, where the codec's own priming is declared separately and discarded by the player.
/// </para>
/// </remarks>
public sealed class OggAudioStream : IDisposable
{
    private readonly OggReader reader;
    private readonly List<OggAudioPacket> pending = new List<OggAudioPacket>();
    private readonly List<OggPacket> pageBuffer = new List<OggPacket>();

    private int pendingIndex;
    private long samplesEmitted;
    private long lastGranule;
    private bool sawFirstAudioPage;
    private bool endOfStream;
    private bool disposed;

    private OggAudioStream(OggReader reader)
    {
        this.reader = reader;
        CodecId = string.Empty;
    }

    /// <summary>Opens an Ogg audio file and reads its headers.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this stream is disposed.</param>
    /// <returns>A stream ready to hand out audio packets.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The file is not Ogg, carries no audio this library can put in a container, or its headers are
    /// malformed.
    /// </exception>
    public static OggAudioStream Open(IMediaSource source, bool leaveSourceOpen = false)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        OggAudioStream stream = new OggAudioStream(new OggReader(source, leaveSourceOpen));
        stream.ReadHeaders(source.Name);
        return stream;
    }

    /// <summary>Opens an Ogg audio file by path.</summary>
    /// <param name="path">The path of the <c>.ogg</c> or <c>.opus</c> file.</param>
    /// <returns>A stream ready to hand out audio packets.</returns>
    public static OggAudioStream Open(string path) => Open(new FileMediaSource(path));

    /// <summary>The codec identifier - <c>opus</c> or <c>vorbis</c>. See <see cref="VideoCodecIds" />.</summary>
    public string CodecId { get; private set; }

    /// <summary>
    /// The codec's initialisation data in the shape a container stores and a packet decoder expects: the
    /// <c>OpusHead</c> bytes for Opus, the three Xiph-laced setup headers for Vorbis.
    /// </summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; private set; }

    /// <summary>The sample rate the audio is played at. Opus always reports 48000.</summary>
    public int SampleRate { get; private set; }

    /// <summary>How many channels the audio has.</summary>
    public int Channels { get; private set; }

    /// <summary>
    /// How many samples per channel of codec priming sit at the front of the decoded audio and must be thrown
    /// away. Non-zero for Opus, zero for Vorbis.
    /// </summary>
    public int PreSkipSamples { get; private set; }

    /// <summary>
    /// How many samples per channel to drop from the very end, worked out from the last page's granule
    /// position. Zero when the packets end exactly where the stream does.
    /// </summary>
    public int TrailingTrimSamples { get; private set; }

    /// <summary>How long the audio lasts once the priming and the trailing padding have been taken off.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>How many audio packets have been handed out so far.</summary>
    public long PacketsRead { get; private set; }

    /// <summary>Reads the next audio packet.</summary>
    /// <param name="packet">The packet, with its timing filled in.</param>
    /// <returns>True when a packet was read; false at the end of the audio.</returns>
    /// <exception cref="VideoPlaybackException">The file is malformed.</exception>
    public bool TryReadPacket(out OggAudioPacket packet)
    {
        ThrowIfDisposed();

        while (true)
        {
            if (pendingIndex < pending.Count)
            {
                packet = pending[pendingIndex];
                pendingIndex++;
                PacketsRead++;
                return true;
            }

            pending.Clear();
            pendingIndex = 0;

            if (endOfStream)
            {
                packet = default;
                return false;
            }

            FillNextPage();
        }
    }

    /// <summary>Reads every remaining packet into a list.</summary>
    /// <returns>The packets, in order.</returns>
    /// <remarks>
    /// Convenient for a short clip and for tests. A long file is better read one packet at a time with
    /// <see cref="TryReadPacket" />, which never holds more than one page.
    /// </remarks>
    public IReadOnlyList<OggAudioPacket> ReadAllPackets()
    {
        List<OggAudioPacket> all = new List<OggAudioPacket>();
        while (TryReadPacket(out OggAudioPacket packet)) all.Add(packet);
        return all;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        reader.Dispose();
    }

    private void ReadHeaders(string name)
    {
        if (!reader.TryReadPacket(out OggPacket first))
        {
            throw new VideoPlaybackException($"'{name}' contains no Ogg packets at all.");
        }

        ReadOnlySpan<byte> data = first.Data.Span;

        if (data.Length >= 19 && data.Slice(0, 8).SequenceEqual("OpusHead"u8))
        {
            ReadOpusHeaders(name, first);
            return;
        }

        if (data.Length >= 30 && data[0] == 0x01 && data.Slice(1, 6).SequenceEqual("vorbis"u8))
        {
            ReadVorbisHeaders(name, first);
            return;
        }

        throw new VideoPlaybackException(
            $"'{name}' is an Ogg file whose first packet is neither an Opus identification header nor a Vorbis "
            + "one. This library authors Opus and Vorbis audio only.");
    }

    private void ReadOpusHeaders(string name, OggPacket head)
    {
        ReadOnlySpan<byte> data = head.Data.Span;

        byte version = data[8];
        if ((version & 0xF0) != 0)
        {
            throw new VideoPlaybackException(
                $"'{name}' declares Opus encapsulation version {version}, which this library does not know.");
        }

        CodecId = VideoCodecIds.Opus;
        Channels = data[9];
        PreSkipSamples = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2));
        SampleRate = OpusPacketDuration.SampleRate;
        CodecPrivate = head.Data.ToArray();

        if (!reader.TryReadPacket(out OggPacket tags)
            || tags.Data.Length < 8
            || !tags.Data.Span.Slice(0, 8).SequenceEqual("OpusTags"u8))
        {
            throw new VideoPlaybackException(
                $"'{name}' has an Opus identification header that is not followed by an 'OpusTags' comment header.");
        }
    }

    private void ReadVorbisHeaders(string name, OggPacket identification)
    {
        ReadOnlySpan<byte> data = identification.Data.Span;

        CodecId = VideoCodecIds.Vorbis;
        Channels = data[11];
        SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));
        PreSkipSamples = 0;

        if (SampleRate <= 0 || Channels <= 0)
        {
            throw new VideoPlaybackException(
                $"'{name}' has a Vorbis identification header claiming {Channels} channels at {SampleRate} Hz, "
                + "which cannot be played.");
        }

        if (!reader.TryReadPacket(out OggPacket comment)
            || comment.Data.Length < 7
            || comment.Data.Span[0] != 0x03)
        {
            throw new VideoPlaybackException(
                $"'{name}' has a Vorbis identification header that is not followed by a comment header.");
        }

        if (!reader.TryReadPacket(out OggPacket setup)
            || setup.Data.Length < 7
            || setup.Data.Span[0] != 0x05)
        {
            throw new VideoPlaybackException(
                $"'{name}' has Vorbis headers that are not followed by a setup header.");
        }

        CodecPrivate = BuildXiphCodecPrivate(
            identification.Data.Span,
            comment.Data.Span,
            setup.Data.Span);
    }

    /// <summary>
    /// Packs three Vorbis setup headers into the single Xiph-laced block a media container stores as codec
    /// private data.
    /// </summary>
    /// <param name="identification">The identification header.</param>
    /// <param name="comment">The comment header.</param>
    /// <param name="setup">The setup header.</param>
    /// <returns>
    /// A count byte of 2, the lengths of the first two headers written as runs of 255 followed by a
    /// remainder, then the three headers back to back.
    /// </returns>
    public static byte[] BuildXiphCodecPrivate(
        ReadOnlySpan<byte> identification,
        ReadOnlySpan<byte> comment,
        ReadOnlySpan<byte> setup)
    {
        int lengthBytes = LacedLength(identification.Length) + LacedLength(comment.Length);
        byte[] result = new byte[1 + lengthBytes + identification.Length + comment.Length + setup.Length];

        int offset = 0;
        result[offset++] = 2;
        offset = WriteLacedLength(result, offset, identification.Length);
        offset = WriteLacedLength(result, offset, comment.Length);

        identification.CopyTo(result.AsSpan(offset));
        offset += identification.Length;
        comment.CopyTo(result.AsSpan(offset));
        offset += comment.Length;
        setup.CopyTo(result.AsSpan(offset));

        return result;
    }

    /// <summary>
    /// Splits the Xiph-laced block a container stores as a Vorbis track's codec-private data back into the
    /// three setup headers Vorbis actually wants - the exact inverse of <see cref="BuildXiphCodecPrivate" />.
    /// </summary>
    /// <param name="codecPrivate">The block as the container stored it.</param>
    /// <param name="identification">The Vorbis identification header, packet type 1.</param>
    /// <param name="comment">The Vorbis comment header, packet type 3.</param>
    /// <param name="setup">The Vorbis setup header, packet type 5, which is the rest of the block.</param>
    /// <exception cref="VideoPlaybackException">
    /// The block is too short, declares a packet count other than three, ends inside one of its laced
    /// lengths, declares header lengths longer than the data it carries, or carries no setup header.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A count byte holding the number of packets MINUS ONE - so 2 - comes first, then the length of each
    /// packet except the last, each written as a run of 0xFF bytes followed by a remainder below 0xFF, then
    /// the packets themselves back to back. The last header's length is not stored: it is whatever is left.
    /// </para>
    /// <para>
    /// This is what anything writing an Ogg Vorbis file out of a container needs, and it is published so that
    /// nobody has to re-derive the length decoding - which looks simple and is easy to get subtly wrong when
    /// a header is an exact multiple of 255 bytes long.
    /// </para>
    /// </remarks>
    public static void SplitXiphCodecPrivate(
        ReadOnlySpan<byte> codecPrivate,
        out byte[] identification,
        out byte[] comment,
        out byte[] setup)
    {
        if (codecPrivate.Length < 4)
        {
            throw new VideoPlaybackException(
                $"A Vorbis codec-private block of {codecPrivate.Length} byte(s) is too short to hold the count "
                + "byte, two laced lengths and three setup headers.");
        }

        int packetCount = codecPrivate[0] + 1;
        if (packetCount != 3)
        {
            throw new VideoPlaybackException(
                $"This Vorbis codec-private block declares {packetCount} packet(s); Vorbis has exactly three "
                + "setup headers, so its count byte is always 2.");
        }

        int offset = 1;
        int identificationLength = ReadLacedLength(codecPrivate, ref offset);
        int commentLength = ReadLacedLength(codecPrivate, ref offset);

        long declared = (long)identificationLength + commentLength;
        if (declared > codecPrivate.Length - offset)
        {
            throw new VideoPlaybackException(
                $"This Vorbis codec-private block declares header lengths totalling {declared} byte(s) but "
                + $"carries only {codecPrivate.Length - offset} byte(s) of headers.");
        }

        identification = codecPrivate.Slice(offset, identificationLength).ToArray();
        offset += identificationLength;

        comment = codecPrivate.Slice(offset, commentLength).ToArray();
        offset += commentLength;

        setup = codecPrivate.Slice(offset).ToArray();

        if (setup.Length == 0)
        {
            throw new VideoPlaybackException(
                "This Vorbis codec-private block carries no setup header, so nothing could decode the audio.");
        }
    }

    private static int ReadLacedLength(ReadOnlySpan<byte> data, ref int offset)
    {
        int length = 0;

        while (true)
        {
            if (offset >= data.Length)
            {
                throw new VideoPlaybackException(
                    "This Vorbis codec-private block ended inside one of its laced header lengths.");
            }

            byte value = data[offset++];
            length += value;
            if (value != 255) return length;
        }
    }

    private static int LacedLength(int value) => (value / 255) + 1;

    private static int WriteLacedLength(byte[] destination, int offset, int value)
    {
        while (value >= 255)
        {
            destination[offset++] = 255;
            value -= 255;
        }

        destination[offset++] = (byte)value;
        return offset;
    }

    private void FillNextPage()
    {
        pageBuffer.Clear();

        while (true)
        {
            if (!reader.TryReadPacket(out OggPacket packet))
            {
                endOfStream = true;
                break;
            }

            pageBuffer.Add(packet);
            if (packet.EndsPage) break;
        }

        if (pageBuffer.Count == 0)
        {
            Finish();
            return;
        }

        long granule = pageBuffer[pageBuffer.Count - 1].GranulePosition;

        if (string.Equals(CodecId, VideoCodecIds.Opus, StringComparison.Ordinal))
        {
            EmitOpusPage(granule);
        }
        else
        {
            EmitVorbisPage(granule);
        }

        if (granule >= 0) lastGranule = granule;
        sawFirstAudioPage = true;

        if (endOfStream) Finish();
    }

    private void EmitOpusPage(long granule)
    {
        foreach (OggPacket packet in pageBuffer)
        {
            int samples = OpusPacketDuration.GetSampleCount(packet.Data.Span);
            if (samples <= 0)
            {
                throw new VideoPlaybackException(
                    "An Opus packet declares a frame configuration this library cannot make sense of, so its "
                    + "duration is unknown and it cannot be given a timestamp.");
            }

            pending.Add(new OggAudioPacket(
                packet.Data,
                SamplesToTime(samplesEmitted),
                SamplesToTime(samples),
                samples));

            samplesEmitted += samples;
        }
    }

    private void EmitVorbisPage(long granule)
    {
        long target = granule >= 0 ? granule : samplesEmitted;
        long delta = target - samplesEmitted;
        if (delta < 0) delta = 0;

        int count = pageBuffer.Count;
        long each = delta / count;
        long remainder = delta - (each * count);

        for (int i = 0; i < count; i++)
        {
            long samples = each + (i == count - 1 ? remainder : 0);

            pending.Add(new OggAudioPacket(
                pageBuffer[i].Data,
                SamplesToTime(samplesEmitted),
                SamplesToTime(samples),
                (int)samples));

            samplesEmitted += samples;
        }
    }

    private void Finish()
    {
        long usable;

        if (string.Equals(CodecId, VideoCodecIds.Opus, StringComparison.Ordinal))
        {
            usable = lastGranule - PreSkipSamples;
            if (usable < 0) usable = 0;

            long produced = samplesEmitted - PreSkipSamples;
            if (produced > usable) TrailingTrimSamples = (int)(produced - usable);
        }
        else
        {
            usable = sawFirstAudioPage ? lastGranule : samplesEmitted;
            if (usable < 0) usable = 0;
        }

        Duration = SamplesToTime(usable);
    }

    private TimeSpan SamplesToTime(long samples) =>
        SampleRate <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / SampleRate);

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OggAudioStream));
    }
}
