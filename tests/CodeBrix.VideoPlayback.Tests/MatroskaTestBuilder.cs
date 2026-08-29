using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Containers.Matroska;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Writes EBML and small Matroska documents in memory so the reader can be tested against bytes whose every
/// field is known, rather than only against files a muxer happened to produce.
/// </summary>
internal sealed class MatroskaTestBuilder
{
    private readonly MemoryStream stream = new MemoryStream();
    private readonly Stack<long> openMasters = new Stack<long>();

    public long Position => stream.Position;

    public byte[] ToArray() => stream.ToArray();

    public void WriteId(uint id)
    {
        int length = id switch
        {
            <= 0xFF => 1,
            <= 0xFFFF => 2,
            <= 0xFFFFFF => 3,
            _ => 4,
        };

        for (int i = length - 1; i >= 0; i--) stream.WriteByte((byte)(id >> (8 * i)));
    }

    public void WriteSize(long size)
    {
        int length = 1;
        while (length < 8 && size >= (1L << (7 * length)) - 1) length++;

        long marker = 1L << (7 * length);
        long value = marker | size;
        for (int i = length - 1; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i)));
    }

    /// <summary>Writes a size field of a fixed width, so a placeholder can be patched later.</summary>
    public void WriteSizeFixed(long size, int length)
    {
        long marker = 1L << (7 * length);
        long value = marker | size;
        for (int i = length - 1; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i)));
    }

    public void WriteUnknownSize() => stream.WriteByte(0xFF);

    public void Element(uint id, ReadOnlySpan<byte> payload)
    {
        WriteId(id);
        WriteSize(payload.Length);
        stream.Write(payload);
    }

    public void UInt(uint id, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        int length = 1;
        for (int i = 7; i >= 1; i--)
        {
            if ((value >> (8 * i)) != 0)
            {
                length = i + 1;
                break;
            }
        }

        for (int i = 0; i < length; i++) buffer[i] = (byte)(value >> (8 * (length - 1 - i)));
        Element(id, buffer.Slice(0, length));
    }

    public void SInt(uint id, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        for (int i = 0; i < 8; i++) buffer[i] = (byte)(value >> (8 * (7 - i)));

        int start = 0;
        while (start < 7)
        {
            bool redundant = (buffer[start] == 0x00 && (buffer[start + 1] & 0x80) == 0)
                || (buffer[start] == 0xFF && (buffer[start + 1] & 0x80) != 0);
            if (!redundant) break;
            start++;
        }

        Element(id, buffer.Slice(start, 8 - start));
    }

    public void Str(uint id, string text) => Element(id, Encoding.UTF8.GetBytes(text));

    public void Float64(uint id, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        for (int i = 0; i < 8; i++) buffer[i] = (byte)(bits >> (8 * (7 - i)));
        Element(id, buffer);
    }

    public void Float32(uint id, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        uint bits = BitConverter.SingleToUInt32Bits(value);
        for (int i = 0; i < 4; i++) buffer[i] = (byte)(bits >> (8 * (3 - i)));
        Element(id, buffer);
    }

    public void Raw(ReadOnlySpan<byte> bytes) => stream.Write(bytes);

    /// <summary>Opens a master element, writing an eight-byte size placeholder that <see cref="EndMaster" /> patches.</summary>
    public void BeginMaster(uint id)
    {
        WriteId(id);
        long sizeOffset = stream.Position;
        WriteSizeFixed(0, 8);
        openMasters.Push(sizeOffset);
    }

    public void EndMaster()
    {
        long sizeOffset = openMasters.Pop();
        long end = stream.Position;
        long payload = end - sizeOffset - 8;

        stream.Position = sizeOffset;
        WriteSizeFixed(payload, 8);
        stream.Position = end;
    }

    /// <summary>Adds a <c>CRC-32</c> element covering bytes that have already been written.</summary>
    public static byte[] Crc32Element(ReadOnlySpan<byte> covered)
    {
        uint crc = EbmlCrc32.Compute(covered);
        return new byte[]
        {
            0xBF, 0x84, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24),
        };
    }

    /// <summary>Writes an EBML header declaring a document type.</summary>
    public void EbmlHeader(string docType, int docTypeVersion = 4, int readVersion = 2)
    {
        BeginMaster(EbmlIds.EbmlHeader);
        UInt(EbmlIds.EbmlVersion, 1);
        UInt(EbmlIds.EbmlReadVersion, 1);
        UInt(EbmlIds.EbmlMaxIdLength, 4);
        UInt(EbmlIds.EbmlMaxSizeLength, 8);
        Str(EbmlIds.DocType, docType);
        UInt(EbmlIds.DocTypeVersion, (ulong)docTypeVersion);
        UInt(EbmlIds.DocTypeReadVersion, (ulong)readVersion);
        EndMaster();
    }

    /// <summary>Writes an <c>Info</c> element with a timestamp scale and a duration.</summary>
    public void Info(long timestampScaleNs, double durationInScaleUnits)
    {
        BeginMaster(MatroskaIds.Info);
        UInt(MatroskaIds.TimestampScale, (ulong)timestampScaleNs);
        Float64(MatroskaIds.Duration, durationInScaleUnits);
        Str(MatroskaIds.MuxingApp, "CodeBrix test builder");
        Str(MatroskaIds.WritingApp, "CodeBrix test builder");
        EndMaster();
    }

    /// <summary>Writes a block payload: track number, relative timestamp, flags and one unlaced frame.</summary>
    public static byte[] SimpleBlockPayload(int trackNumber, short relativeTimestamp, byte flags, ReadOnlySpan<byte> frame)
    {
        byte[] result = new byte[4 + frame.Length];
        result[0] = (byte)(0x80 | trackNumber);
        result[1] = (byte)(relativeTimestamp >> 8);
        result[2] = (byte)relativeTimestamp;
        result[3] = flags;
        frame.CopyTo(result.AsSpan(4));
        return result;
    }

    /// <summary>Writes a Xiph-laced block payload.</summary>
    public static byte[] XiphLacedBlockPayload(int trackNumber, short relativeTimestamp, byte[][] frames)
    {
        List<byte> bytes = new List<byte>
        {
            (byte)(0x80 | trackNumber),
            (byte)(relativeTimestamp >> 8),
            (byte)relativeTimestamp,
            0x02,
            (byte)(frames.Length - 1),
        };

        for (int i = 0; i < frames.Length - 1; i++)
        {
            int size = frames[i].Length;
            while (size >= 255)
            {
                bytes.Add(255);
                size -= 255;
            }

            bytes.Add((byte)size);
        }

        foreach (byte[] frame in frames) bytes.AddRange(frame);
        return bytes.ToArray();
    }

    /// <summary>Writes a fixed-laced block payload; every frame must be the same length.</summary>
    public static byte[] FixedLacedBlockPayload(int trackNumber, short relativeTimestamp, byte[][] frames)
    {
        List<byte> bytes = new List<byte>
        {
            (byte)(0x80 | trackNumber),
            (byte)(relativeTimestamp >> 8),
            (byte)relativeTimestamp,
            0x04,
            (byte)(frames.Length - 1),
        };

        foreach (byte[] frame in frames) bytes.AddRange(frame);
        return bytes.ToArray();
    }

    /// <summary>Writes an EBML-laced block payload, coding each size after the first as a signed difference.</summary>
    public static byte[] EbmlLacedBlockPayload(int trackNumber, short relativeTimestamp, byte[][] frames)
    {
        List<byte> bytes = new List<byte>
        {
            (byte)(0x80 | trackNumber),
            (byte)(relativeTimestamp >> 8),
            (byte)relativeTimestamp,
            0x06,
            (byte)(frames.Length - 1),
        };

        AppendVint(bytes, (ulong)frames[0].Length);
        for (int i = 1; i < frames.Length - 1; i++)
        {
            AppendSignedVint(bytes, frames[i].Length - frames[i - 1].Length);
        }

        foreach (byte[] frame in frames) bytes.AddRange(frame);
        return bytes.ToArray();
    }

    private static void AppendVint(List<byte> bytes, ulong value)
    {
        int length = 1;
        while (length < 8 && value >= (1UL << (7 * length)) - 1) length++;

        ulong marker = 1UL << (7 * length);
        ulong coded = marker | value;
        for (int i = length - 1; i >= 0; i--) bytes.Add((byte)(coded >> (8 * i)));
    }

    private static void AppendSignedVint(List<byte> bytes, long value)
    {
        int length = 1;
        while (length < 8)
        {
            long bias = (1L << ((7 * length) - 1)) - 1;
            if (value >= -bias && value <= bias) break;
            length++;
        }

        long biasForLength = (1L << ((7 * length) - 1)) - 1;
        ulong unsigned = (ulong)(value + biasForLength);
        ulong marker = 1UL << (7 * length);
        ulong coded = marker | unsigned;
        for (int i = length - 1; i >= 0; i--) bytes.Add((byte)(coded >> (8 * i)));
    }
}
