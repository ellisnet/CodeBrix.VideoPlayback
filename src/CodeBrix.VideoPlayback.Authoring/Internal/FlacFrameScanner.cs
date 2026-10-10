using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Splits a <c>.flac</c> file into the two things a container's FLAC track needs: the stream header (the
/// codec-private data) and one packet per FLAC frame.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg writes the master flavour's sound as a plain <c>.flac</c> file; the muxer wants Matroska's
/// <c>A_FLAC</c> shape instead - the <c>fLaC</c> marker and the metadata blocks as codec-private data, and
/// each frame as its own packet. Nothing is decoded and nothing is re-encoded: every frame is copied out byte
/// for byte, from its sync code through its CRC-16.
/// </para>
/// <para>
/// A frame boundary is never guessed from the sync code alone - 0xFFF8 can occur inside compressed audio.
/// A candidate boundary is accepted only when its header parses, its CRC-8 matches, its stream parameters agree
/// with STREAMINFO, its frame (or sample) number is the one that must come next, AND the CRC-16 of the frame it
/// closes matches. A file that cannot be split that way is refused with the offset at which it went wrong.
/// </para>
/// <para>
/// The codec-private data keeps STREAMINFO and the VORBIS_COMMENT block, with the last-block flag moved onto
/// whichever is last. PADDING, SEEKTABLE (whose offsets mean nothing once the frames are packets), APPLICATION,
/// CUESHEET and PICTURE blocks are left out.
/// </para>
/// </remarks>
internal sealed class FlacFrameScanner
{
    private const int StreamInfoLength = 34;

    private readonly byte[] file;
    private readonly string name;

    private int minimumBlockSize;
    private int maximumBlockSize;

    private FlacFrameScanner(byte[] file, string name)
    {
        this.file = file;
        this.name = name;
    }

    /// <summary>The codec-private data: <c>fLaC</c>, STREAMINFO and the comment block.</summary>
    internal byte[] CodecPrivate { get; private set; }

    /// <summary>The stream's sample rate, from STREAMINFO.</summary>
    internal int SampleRate { get; private set; }

    /// <summary>The stream's channel count, from STREAMINFO.</summary>
    internal int Channels { get; private set; }

    /// <summary>The stream's bits per sample, from STREAMINFO.</summary>
    internal int BitsPerSample { get; private set; }

    /// <summary>The total number of samples per channel STREAMINFO declares, or 0 when it does not say.</summary>
    internal long TotalSamples { get; private set; }

    /// <summary>The byte offset of the first frame in the file.</summary>
    internal int FirstFrameOffset { get; private set; }

    /// <summary>Every frame, in file order.</summary>
    internal IReadOnlyList<FlacFrame> Frames { get; private set; }

    /// <summary>Reads and splits a <c>.flac</c> file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The split stream.</returns>
    /// <exception cref="VideoAuthoringException">The file is not a FLAC stream this scanner can split.</exception>
    internal static FlacFrameScanner ScanFile(string path) => Scan(File.ReadAllBytes(path), path);

    /// <summary>Splits a FLAC stream held in memory.</summary>
    /// <param name="bytes">The whole stream, as a <c>.flac</c> file holds it.</param>
    /// <param name="name">A name for messages.</param>
    /// <returns>The split stream.</returns>
    /// <exception cref="VideoAuthoringException">The bytes are not a FLAC stream this scanner can split.</exception>
    internal static FlacFrameScanner Scan(byte[] bytes, string name)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));

        FlacFrameScanner scanner = new FlacFrameScanner(bytes, name ?? "the FLAC stream");
        scanner.ReadMetadata();
        scanner.ReadFrames();
        return scanner;
    }

    /// <summary>The bytes of one frame - its sync code through its CRC-16.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>A copy of the frame's bytes.</returns>
    internal byte[] CopyFrame(FlacFrame frame) => file.AsSpan(frame.Offset, frame.Length).ToArray();

    /// <summary>When a frame's first sample plays.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>Its timestamp.</returns>
    internal TimeSpan TimestampOf(FlacFrame frame) =>
        TimeSpan.FromTicks(frame.FirstSample * TimeSpan.TicksPerSecond / SampleRate);

    /// <summary>How long a frame plays.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>Its duration.</returns>
    internal TimeSpan DurationOf(FlacFrame frame) =>
        TimeSpan.FromTicks((long)frame.BlockSize * TimeSpan.TicksPerSecond / SampleRate);

    private void ReadMetadata()
    {
        if (file.Length < 4 + 4 + StreamInfoLength
            || file[0] != (byte)'f' || file[1] != (byte)'L' || file[2] != (byte)'a' || file[3] != (byte)'C')
        {
            throw Fail("does not begin with the FLAC stream marker 'fLaC'");
        }

        List<(int Type, int Offset, int Length)> kept = new List<(int, int, int)>();
        int position = 4;
        bool last = false;
        bool first = true;

        while (!last)
        {
            if (position + 4 > file.Length) throw Fail("ends inside its metadata blocks");

            last = (file[position] & 0x80) != 0;
            int type = file[position] & 0x7F;
            int length = (file[position + 1] << 16) | (file[position + 2] << 8) | file[position + 3];

            if (position + 4 + length > file.Length) throw Fail("ends inside a metadata block");

            if (first)
            {
                if (type != 0 || length != StreamInfoLength)
                {
                    throw Fail($"has a type-{type} metadata block of {length} bytes first; STREAMINFO must come first");
                }

                ReadStreamInfo(position + 4);
                first = false;
            }

            if (type == 0 || type == 4) kept.Add((type, position, length));
            position += 4 + length;
        }

        FirstFrameOffset = position;

        using MemoryStream header = new MemoryStream();
        header.Write(file, 0, 4);
        for (int i = 0; i < kept.Count; i++)
        {
            (int type, int offset, int length) = kept[i];
            header.WriteByte((byte)((i == kept.Count - 1 ? 0x80 : 0x00) | type));
            header.Write(file, offset + 1, 3 + length);
        }

        CodecPrivate = header.ToArray();
    }

    private void ReadStreamInfo(int offset)
    {
        minimumBlockSize = (file[offset] << 8) | file[offset + 1];
        maximumBlockSize = (file[offset + 2] << 8) | file[offset + 3];

        ulong packed = 0;
        for (int i = 0; i < 8; i++) packed = (packed << 8) | file[offset + 10 + i];

        SampleRate = (int)(packed >> 44);
        Channels = (int)((packed >> 41) & 0x7) + 1;
        BitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
        TotalSamples = (long)(packed & 0xFFFFFFFFFUL);

        if (SampleRate == 0) throw Fail("declares a sample rate of zero in STREAMINFO");
    }

    private void ReadFrames()
    {
        List<FlacFrame> frames = new List<FlacFrame>();
        int position = FirstFrameOffset;

        if (position >= file.Length)
        {
            Frames = frames;
            return;
        }

        if (!TryParseHeader(position, out FrameHeader current))
        {
            throw Fail($"has no valid frame header where the first frame should start, at byte {Text(position)}");
        }

        while (true)
        {
            int end = FindNextFrame(position, current, out FrameHeader next);
            int length = end - position;

            if (!FrameCrcMatches(position, length))
            {
                throw Fail($"has a frame at byte {Text(position)} whose CRC-16 does not match");
            }

            long firstSample = current.IsVariable ? current.Number : current.Number * NominalBlockSize(current);
            frames.Add(new FlacFrame(position, length, firstSample, current.BlockSize));

            if (end >= file.Length) break;

            position = end;
            current = next;
        }

        Frames = frames;
    }

    private int NominalBlockSize(in FrameHeader header) =>
        maximumBlockSize > 0 && maximumBlockSize == minimumBlockSize ? maximumBlockSize : header.BlockSize;

    private int FindNextFrame(int start, in FrameHeader current, out FrameHeader next)
    {
        long expectedNumber = current.IsVariable ? current.Number + current.BlockSize : current.Number + 1;

        for (int candidate = start + current.HeaderLength; candidate + 1 < file.Length; candidate++)
        {
            if (file[candidate] != 0xFF || (file[candidate + 1] & 0xFE) != 0xF8) continue;
            if (!TryParseHeader(candidate, out FrameHeader header)) continue;
            if (header.IsVariable != current.IsVariable || header.Number != expectedNumber) continue;
            if (!FrameCrcMatches(start, candidate - start)) continue;

            next = header;
            return candidate;
        }

        next = default;
        return file.Length;
    }

    private bool TryParseHeader(int offset, out FrameHeader header)
    {
        header = default;
        if (offset + 6 > file.Length) return false;
        if (file[offset] != 0xFF || (file[offset + 1] & 0xFE) != 0xF8) return false;

        bool variable = (file[offset + 1] & 0x01) != 0;
        int blockSizeCode = file[offset + 2] >> 4;
        int sampleRateCode = file[offset + 2] & 0x0F;
        int channelCode = file[offset + 3] >> 4;
        int sampleSizeCode = (file[offset + 3] >> 1) & 0x07;

        if ((file[offset + 3] & 0x01) != 0 || blockSizeCode == 0 || sampleRateCode == 15) return false;
        if (channelCode > 10 || sampleSizeCode == 3 || sampleSizeCode == 7) return false;

        int position = offset + 4;
        if (!TryReadCodedNumber(ref position, out long number)) return false;

        int blockSize;
        switch (blockSizeCode)
        {
            case 1: blockSize = 192; break;
            case >= 2 and <= 5: blockSize = 576 << (blockSizeCode - 2); break;
            case 6:
                if (position >= file.Length) return false;
                blockSize = file[position++] + 1;
                break;
            case 7:
                if (position + 1 >= file.Length) return false;
                blockSize = ((file[position] << 8) | file[position + 1]) + 1;
                position += 2;
                break;
            default: blockSize = 256 << (blockSizeCode - 8); break;
        }

        int sampleRate = sampleRateCode switch
        {
            0 => SampleRate,
            1 => 88200,
            2 => 176400,
            3 => 192000,
            4 => 8000,
            5 => 16000,
            6 => 22050,
            7 => 24000,
            8 => 32000,
            9 => 44100,
            10 => 48000,
            11 => 96000,
            _ => -1,
        };

        if (sampleRateCode >= 12)
        {
            int extra = sampleRateCode == 12 ? 1 : 2;
            if (position + extra > file.Length) return false;
            int value = extra == 1 ? file[position] : (file[position] << 8) | file[position + 1];
            sampleRate = sampleRateCode == 12 ? value * 1000 : sampleRateCode == 13 ? value : value * 10;
            position += extra;
        }

        if (position >= file.Length) return false;
        if (Crc8(offset, position - offset) != file[position]) return false;

        // The stream parameters must be the ones STREAMINFO declares, or this is not a frame of this stream.
        int channels = channelCode <= 7 ? channelCode + 1 : 2;
        int bits = sampleSizeCode switch
        {
            0 => BitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            _ => 24,
        };

        if (sampleRate != SampleRate || channels != Channels || bits != BitsPerSample) return false;

        header = new FrameHeader(variable, number, blockSize, position + 1 - offset);
        return true;
    }

    private bool TryReadCodedNumber(ref int position, out long value)
    {
        value = 0;
        if (position >= file.Length) return false;

        byte first = file[position++];
        int extra;

        if ((first & 0x80) == 0) { value = first; return true; }
        if ((first & 0xE0) == 0xC0) { extra = 1; value = first & 0x1F; }
        else if ((first & 0xF0) == 0xE0) { extra = 2; value = first & 0x0F; }
        else if ((first & 0xF8) == 0xF0) { extra = 3; value = first & 0x07; }
        else if ((first & 0xFC) == 0xF8) { extra = 4; value = first & 0x03; }
        else if ((first & 0xFE) == 0xFC) { extra = 5; value = first & 0x01; }
        else if (first == 0xFE) { extra = 6; value = 0; }
        else return false;

        for (int i = 0; i < extra; i++)
        {
            if (position >= file.Length) return false;
            byte next = file[position++];
            if ((next & 0xC0) != 0x80) return false;
            value = (value << 6) | (uint)(next & 0x3F);
        }

        return true;
    }

    private byte Crc8(int offset, int length)
    {
        int crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc ^= file[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }

        return (byte)crc;
    }

    private bool FrameCrcMatches(int offset, int length)
    {
        if (length < 3) return false;

        int crc = 0;
        int covered = length - 2;
        for (int i = 0; i < covered; i++)
        {
            crc ^= file[offset + i] << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        int stored = (file[offset + covered] << 8) | file[offset + covered + 1];
        return crc == stored;
    }

    private VideoAuthoringException Fail(string what) =>
        new VideoAuthoringException("'" + name + "' cannot be split into FLAC frames: it " + what + ".");

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private readonly struct FrameHeader
    {
        internal FrameHeader(bool isVariable, long number, int blockSize, int headerLength)
        {
            IsVariable = isVariable;
            Number = number;
            BlockSize = blockSize;
            HeaderLength = headerLength;
        }

        internal bool IsVariable { get; }

        internal long Number { get; }

        internal int BlockSize { get; }

        internal int HeaderLength { get; }
    }
}
