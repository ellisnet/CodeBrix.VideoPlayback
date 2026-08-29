using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Internal;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// Reads just enough of an AV1 bitstream to author a container around it: the units it is built from, its
/// sequence header, whether a temporal unit is a key frame, and the codec configuration record a container
/// stores as a track's private data.
/// </summary>
/// <remarks>
/// <para>
/// This is emphatically NOT a decoder. It walks the open bit units of a temporal unit, parses the sequence
/// header's fields, and reads the first two fields of a frame header. That is what a muxer needs and no more.
/// </para>
/// <para>
/// The bitstream is expected in the "low overhead" form - every unit carrying its own size field - which is
/// what an IVF file and every container this library reads contain. Length-delimited (Annex B) streams are
/// not accepted.
/// </para>
/// </remarks>
public static class Av1Bitstream
{
    /// <summary>One unit found inside a temporal unit.</summary>
    public readonly struct ObuSpan
    {
        /// <summary>Creates a unit description.</summary>
        /// <param name="type">What the unit carries.</param>
        /// <param name="temporalId">The temporal layer it belongs to, or 0 when it has no extension header.</param>
        /// <param name="spatialId">The spatial layer it belongs to, or 0 when it has no extension header.</param>
        /// <param name="start">The offset of the unit's first byte within the temporal unit.</param>
        /// <param name="length">The whole unit's length in bytes, header and payload together.</param>
        /// <param name="payloadStart">The offset of the unit's payload within the temporal unit.</param>
        /// <param name="payloadLength">The payload's length in bytes.</param>
        public ObuSpan(
            Av1ObuType type,
            int temporalId,
            int spatialId,
            int start,
            int length,
            int payloadStart,
            int payloadLength)
        {
            Type = type;
            TemporalId = temporalId;
            SpatialId = spatialId;
            Start = start;
            Length = length;
            PayloadStart = payloadStart;
            PayloadLength = payloadLength;
        }

        /// <summary>What the unit carries.</summary>
        public Av1ObuType Type { get; }

        /// <summary>The temporal layer the unit belongs to.</summary>
        public int TemporalId { get; }

        /// <summary>The spatial layer the unit belongs to.</summary>
        public int SpatialId { get; }

        /// <summary>The offset of the unit's first byte within the temporal unit.</summary>
        public int Start { get; }

        /// <summary>The whole unit's length in bytes, header and payload together.</summary>
        public int Length { get; }

        /// <summary>The offset of the unit's payload within the temporal unit.</summary>
        public int PayloadStart { get; }

        /// <summary>The payload's length in bytes.</summary>
        public int PayloadLength { get; }
    }

    /// <summary>Walks the units inside one temporal unit.</summary>
    /// <param name="temporalUnit">The bytes of one temporal unit - one IVF frame, or one container packet.</param>
    /// <returns>Every unit found, in order.</returns>
    /// <exception cref="VideoPlaybackException">A unit is malformed, or does not carry a size field.</exception>
    public static IReadOnlyList<ObuSpan> ReadUnits(ReadOnlySpan<byte> temporalUnit)
    {
        List<ObuSpan> units = new List<ObuSpan>();
        int offset = 0;

        while (offset < temporalUnit.Length)
        {
            int start = offset;
            byte header = temporalUnit[offset++];

            if ((header & 0x80) != 0)
            {
                throw new VideoPlaybackException(
                    $"An AV1 unit at offset {start} has its forbidden bit set, so this is not an AV1 bitstream.");
            }

            Av1ObuType type = (Av1ObuType)((header >> 3) & 0x0F);
            bool hasExtension = (header & 0x04) != 0;
            bool hasSize = (header & 0x02) != 0;

            int temporalId = 0;
            int spatialId = 0;
            if (hasExtension)
            {
                if (offset >= temporalUnit.Length)
                {
                    throw new VideoPlaybackException(
                        $"An AV1 unit at offset {start} claims an extension header that is not in the data.");
                }

                byte extension = temporalUnit[offset++];
                temporalId = (extension >> 5) & 0x07;
                spatialId = (extension >> 3) & 0x03;
            }

            int payloadLength;
            if (hasSize)
            {
                payloadLength = (int)ReadLeb128(temporalUnit, ref offset, "an AV1 unit size");
            }
            else
            {
                payloadLength = temporalUnit.Length - offset;
                if (units.Count > 0 || offset + payloadLength != temporalUnit.Length)
                {
                    throw new VideoPlaybackException(
                        $"An AV1 unit at offset {start} carries no size field. This library reads the low-overhead "
                        + "bitstream form, in which every unit states its own size; a length-delimited (Annex B) "
                        + "stream must be converted first.");
                }
            }

            if (payloadLength < 0 || offset + payloadLength > temporalUnit.Length)
            {
                throw new VideoPlaybackException(
                    $"An AV1 unit at offset {start} claims a {payloadLength}-byte payload but only "
                    + $"{temporalUnit.Length - offset} bytes remain.");
            }

            units.Add(new ObuSpan(type, temporalId, spatialId, start, offset + payloadLength - start, offset, payloadLength));
            offset += payloadLength;
        }

        return units;
    }

    /// <summary>Finds and parses the sequence header inside a temporal unit.</summary>
    /// <param name="temporalUnit">The bytes of one temporal unit.</param>
    /// <param name="header">The parsed header, or null when the unit carries none.</param>
    /// <param name="headerUnitStart">The offset of the whole sequence-header unit within the temporal unit.</param>
    /// <param name="headerUnitLength">The length of the whole sequence-header unit.</param>
    /// <returns>True when a sequence header was found and parsed.</returns>
    /// <exception cref="VideoPlaybackException">A unit is malformed, or the header is truncated.</exception>
    public static bool TryReadSequenceHeader(
        ReadOnlySpan<byte> temporalUnit,
        out Av1SequenceHeader header,
        out int headerUnitStart,
        out int headerUnitLength)
    {
        header = null;
        headerUnitStart = 0;
        headerUnitLength = 0;

        foreach (ObuSpan unit in ReadUnits(temporalUnit))
        {
            if (unit.Type != Av1ObuType.SequenceHeader) continue;

            header = ParseSequenceHeader(temporalUnit.Slice(unit.PayloadStart, unit.PayloadLength));
            headerUnitStart = unit.Start;
            headerUnitLength = unit.Length;
            return true;
        }

        return false;
    }

    /// <summary>Parses a sequence header's payload.</summary>
    /// <param name="payload">The unit's payload, without its header or size field.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="VideoPlaybackException">The payload is truncated or malformed.</exception>
    public static Av1SequenceHeader ParseSequenceHeader(ReadOnlySpan<byte> payload)
    {
        BitReader bits = new BitReader(payload, "An AV1 sequence header");
        Av1SequenceHeader header = new Av1SequenceHeader();

        header.SeqProfile = (int)bits.ReadBits(3);
        header.StillPicture = bits.ReadFlag();
        header.ReducedStillPictureHeader = bits.ReadFlag();

        bool decoderModelInfoPresent = false;
        int bufferDelayLength = 0;

        if (header.ReducedStillPictureHeader)
        {
            header.SeqLevelIdx0 = (int)bits.ReadBits(5);
            header.SeqTier0 = 0;
        }
        else
        {
            bool timingInfoPresent = bits.ReadFlag();
            if (timingInfoPresent)
            {
                bits.ReadBits(32);
                bits.ReadBits(32);
                bool equalPictureInterval = bits.ReadFlag();
                if (equalPictureInterval) bits.ReadUvlc();

                decoderModelInfoPresent = bits.ReadFlag();
                if (decoderModelInfoPresent)
                {
                    bufferDelayLength = (int)bits.ReadBits(5) + 1;
                    bits.ReadBits(32);
                    bits.ReadBits(5);
                    bits.ReadBits(5);
                }
            }

            bool initialDisplayDelayPresent = bits.ReadFlag();
            int operatingPoints = (int)bits.ReadBits(5) + 1;

            for (int i = 0; i < operatingPoints; i++)
            {
                bits.ReadBits(12);
                int levelIdx = (int)bits.ReadBits(5);
                int tier = levelIdx > 7 ? (int)bits.ReadBits(1) : 0;

                if (i == 0)
                {
                    header.SeqLevelIdx0 = levelIdx;
                    header.SeqTier0 = tier;
                }

                if (decoderModelInfoPresent)
                {
                    bool decoderModelForThisOp = bits.ReadFlag();
                    if (decoderModelForThisOp)
                    {
                        bits.ReadBits(bufferDelayLength);
                        bits.ReadBits(bufferDelayLength);
                        bits.ReadFlag();
                    }
                }

                if (!initialDisplayDelayPresent) continue;

                bool initialDisplayDelayForThisOp = bits.ReadFlag();
                if (initialDisplayDelayForThisOp) bits.ReadBits(4);
            }
        }

        int frameWidthBits = (int)bits.ReadBits(4) + 1;
        int frameHeightBits = (int)bits.ReadBits(4) + 1;
        header.MaxFrameWidth = (int)bits.ReadBits(frameWidthBits) + 1;
        header.MaxFrameHeight = (int)bits.ReadBits(frameHeightBits) + 1;

        if (!header.ReducedStillPictureHeader)
        {
            header.FrameIdNumbersPresent = bits.ReadFlag();
            if (header.FrameIdNumbersPresent)
            {
                bits.ReadBits(4);
                bits.ReadBits(3);
            }
        }

        bits.ReadFlag();
        bits.ReadFlag();
        bits.ReadFlag();

        if (!header.ReducedStillPictureHeader)
        {
            bits.ReadFlag();
            bits.ReadFlag();
            bits.ReadFlag();
            bits.ReadFlag();
            bool enableOrderHint = bits.ReadFlag();
            if (enableOrderHint)
            {
                bits.ReadFlag();
                bits.ReadFlag();
            }

            int forceScreenContentTools;
            bool chooseScreenContentTools = bits.ReadFlag();
            forceScreenContentTools = chooseScreenContentTools ? 2 : (int)bits.ReadBits(1);

            if (forceScreenContentTools > 0)
            {
                bool chooseIntegerMv = bits.ReadFlag();
                if (!chooseIntegerMv) bits.ReadBits(1);
            }

            if (enableOrderHint) bits.ReadBits(3);
        }

        bits.ReadFlag();
        bits.ReadFlag();
        bits.ReadFlag();

        ReadColorConfig(ref bits, header);

        header.FilmGrainParamsPresent = bits.ReadFlag();
        return header;
    }

    /// <summary>
    /// Reports whether a temporal unit begins a new group of pictures - a key frame a player may start at.
    /// </summary>
    /// <param name="temporalUnit">The bytes of one temporal unit.</param>
    /// <param name="sequenceHeader">
    /// The stream's sequence header, needed to interpret a frame header, or null when it is not known yet.
    /// </param>
    /// <returns>
    /// True when the unit carries a shown key frame, or - when no frame header could be interpreted - when it
    /// carries a sequence header, which is where an encoder puts its key frames.
    /// </returns>
    public static bool IsKeyFrame(ReadOnlySpan<byte> temporalUnit, Av1SequenceHeader sequenceHeader)
    {
        bool sawSequenceHeader = false;

        foreach (ObuSpan unit in ReadUnits(temporalUnit))
        {
            if (unit.Type == Av1ObuType.SequenceHeader)
            {
                sawSequenceHeader = true;
                sequenceHeader ??= ParseSequenceHeader(temporalUnit.Slice(unit.PayloadStart, unit.PayloadLength));
                continue;
            }

            if (unit.Type != Av1ObuType.Frame && unit.Type != Av1ObuType.FrameHeader) continue;
            if (sequenceHeader == null) continue;

            BitReader bits = new BitReader(
                temporalUnit.Slice(unit.PayloadStart, unit.PayloadLength),
                "An AV1 frame header");

            if (sequenceHeader.ReducedStillPictureHeader) return true;

            bool showExistingFrame = bits.ReadFlag();
            if (showExistingFrame) return false;

            int frameType = (int)bits.ReadBits(2);
            bool showFrame = bits.ReadFlag();
            return frameType == 0 && showFrame;
        }

        return sawSequenceHeader;
    }

    /// <summary>
    /// Builds the codec configuration record a container stores as an AV1 track's private data, from a
    /// temporal unit that contains a sequence header.
    /// </summary>
    /// <param name="temporalUnitWithSequenceHeader">
    /// A temporal unit carrying a sequence header - the first key frame of an elementary stream.
    /// </param>
    /// <returns>The configuration record's bytes.</returns>
    /// <exception cref="VideoPlaybackException">The unit carries no sequence header, or is malformed.</exception>
    /// <remarks>
    /// <para>
    /// The record is the four-byte fixed part defined by the AV1 mapping - a marker and version byte, the
    /// profile and level, the tier and sample-format bits, and a reserved byte - followed by the sequence
    /// header unit copied verbatim. The initial presentation delay is left absent, which is what every
    /// encoder this library has been tested against writes.
    /// </para>
    /// </remarks>
    public static byte[] BuildCodecConfigurationRecord(ReadOnlySpan<byte> temporalUnitWithSequenceHeader)
    {
        if (!TryReadSequenceHeader(
                temporalUnitWithSequenceHeader,
                out Av1SequenceHeader header,
                out int start,
                out int length))
        {
            throw new VideoPlaybackException(
                "This AV1 temporal unit carries no sequence header, so a codec configuration record cannot be "
                + "built from it. Pass the stream's first key frame.");
        }

        return BuildCodecConfigurationRecord(header, temporalUnitWithSequenceHeader.Slice(start, length));
    }

    /// <summary>
    /// Builds the codec configuration record from an already-parsed sequence header and the bytes of the unit
    /// it came from.
    /// </summary>
    /// <param name="header">The parsed sequence header.</param>
    /// <param name="sequenceHeaderUnit">
    /// The whole sequence-header unit - its header byte, any extension byte, its size field and its payload -
    /// which is copied into the record verbatim.
    /// </param>
    /// <returns>The configuration record's bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="header" /> is null.</exception>
    public static byte[] BuildCodecConfigurationRecord(
        Av1SequenceHeader header,
        ReadOnlySpan<byte> sequenceHeaderUnit)
    {
        if (header == null) throw new ArgumentNullException(nameof(header));

        byte[] record = new byte[4 + sequenceHeaderUnit.Length];

        record[0] = 0x81;
        record[1] = (byte)(((header.SeqProfile & 0x07) << 5) | (header.SeqLevelIdx0 & 0x1F));
        record[2] = (byte)(
            ((header.SeqTier0 & 1) << 7)
            | ((header.HighBitDepth ? 1 : 0) << 6)
            | ((header.TwelveBit ? 1 : 0) << 5)
            | ((header.Monochrome ? 1 : 0) << 4)
            | ((header.SubsamplingX & 1) << 3)
            | ((header.SubsamplingY & 1) << 2)
            | (header.ChromaSamplePosition & 0x03));
        record[3] = 0x00;

        sequenceHeaderUnit.CopyTo(record.AsSpan(4));
        return record;
    }

    /// <summary>Parses a codec configuration record back into a sequence header.</summary>
    /// <param name="record">The record's bytes, as a container stored them.</param>
    /// <returns>The sequence header the record carries.</returns>
    /// <exception cref="VideoPlaybackException">The record is malformed or carries no sequence header.</exception>
    public static Av1SequenceHeader ParseCodecConfigurationRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 4)
        {
            throw new VideoPlaybackException(
                $"An AV1 codec configuration record must be at least 4 bytes; this one is {record.Length}.");
        }

        if ((record[0] & 0x80) == 0 || (record[0] & 0x7F) != 1)
        {
            throw new VideoPlaybackException(
                $"An AV1 codec configuration record must start with 0x81 (marker and version 1); this one starts "
                + $"with 0x{record[0]:X2}.");
        }

        if (!TryReadSequenceHeader(record.Slice(4), out Av1SequenceHeader header, out _, out _))
        {
            throw new VideoPlaybackException(
                "This AV1 codec configuration record carries no sequence header in its configuration units.");
        }

        return header;
    }

    /// <summary>Reads a variable-length unsigned integer in the little-endian base-128 form AV1 uses.</summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="offset">The offset to read at; advanced past the value.</param>
    /// <param name="what">What is being read, for the message if it fails.</param>
    /// <returns>The value.</returns>
    /// <exception cref="VideoPlaybackException">The value runs off the end or is longer than eight bytes.</exception>
    public static ulong ReadLeb128(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (offset >= data.Length)
            {
                throw new VideoPlaybackException($"{what} runs off the end of the data at offset {offset}.");
            }

            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) return value;
        }

        throw new VideoPlaybackException($"{what} is longer than the eight bytes the format allows.");
    }

    /// <summary>Writes a value in the little-endian base-128 form AV1 uses.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>Its encoded bytes.</returns>
    public static byte[] WriteLeb128(ulong value)
    {
        Span<byte> scratch = stackalloc byte[8];
        int length = 0;

        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            scratch[length++] = b;
        }
        while (value != 0 && length < 8);

        return scratch.Slice(0, length).ToArray();
    }

    private static void ReadColorConfig(ref BitReader bits, Av1SequenceHeader header)
    {
        header.HighBitDepth = bits.ReadFlag();

        if (header.SeqProfile == 2 && header.HighBitDepth)
        {
            header.TwelveBit = bits.ReadFlag();
            header.BitDepth = header.TwelveBit ? 12 : 10;
        }
        else
        {
            header.BitDepth = header.HighBitDepth ? 10 : 8;
        }

        header.Monochrome = header.SeqProfile != 1 && bits.ReadFlag();
        header.ColorDescriptionPresent = bits.ReadFlag();

        VideoColorPrimaries primaries = VideoColorPrimaries.Unspecified;
        VideoTransferCharacteristics transfer = VideoTransferCharacteristics.Unspecified;
        VideoMatrixCoefficients matrix = VideoMatrixCoefficients.Unspecified;

        if (header.ColorDescriptionPresent)
        {
            primaries = (VideoColorPrimaries)bits.ReadBits(8);
            transfer = (VideoTransferCharacteristics)bits.ReadBits(8);
            matrix = (VideoMatrixCoefficients)bits.ReadBits(8);
        }

        VideoColorRange range;

        if (header.Monochrome)
        {
            range = bits.ReadFlag() ? VideoColorRange.Full : VideoColorRange.Limited;
            header.SubsamplingX = 1;
            header.SubsamplingY = 1;
            header.ChromaSamplePosition = 0;
            header.Color = new VideoColorInfo(primaries, transfer, matrix, range, VideoChromaSiting.Unknown);
            bits.ReadFlag();
            return;
        }

        if (primaries == VideoColorPrimaries.Bt709
            && transfer == VideoTransferCharacteristics.Srgb
            && matrix == VideoMatrixCoefficients.Identity)
        {
            range = VideoColorRange.Full;
            header.SubsamplingX = 0;
            header.SubsamplingY = 0;
        }
        else
        {
            range = bits.ReadFlag() ? VideoColorRange.Full : VideoColorRange.Limited;

            if (header.SeqProfile == 0)
            {
                header.SubsamplingX = 1;
                header.SubsamplingY = 1;
            }
            else if (header.SeqProfile == 1)
            {
                header.SubsamplingX = 0;
                header.SubsamplingY = 0;
            }
            else if (header.BitDepth == 12)
            {
                header.SubsamplingX = (int)bits.ReadBits(1);
                header.SubsamplingY = header.SubsamplingX == 1 ? (int)bits.ReadBits(1) : 0;
            }
            else
            {
                header.SubsamplingX = 1;
                header.SubsamplingY = 0;
            }

            if (header.SubsamplingX == 1 && header.SubsamplingY == 1)
            {
                header.ChromaSamplePosition = (int)bits.ReadBits(2);
            }
        }

        VideoChromaSiting siting = header.ChromaSamplePosition switch
        {
            1 => VideoChromaSiting.Vertical,
            2 => VideoChromaSiting.Colocated,
            _ => VideoChromaSiting.Unknown,
        };

        header.Color = new VideoColorInfo(primaries, transfer, matrix, range, siting);
        bits.ReadFlag();
    }
}
