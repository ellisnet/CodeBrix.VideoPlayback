using System;
using System.Buffers.Binary;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// The uncompressed video "codec" - how a frame of planar samples is laid out in a packet, and how a track's
/// shape is written into its codec-private data.
/// </summary>
/// <remarks>
/// <para>
/// Uncompressed video earns its place twice over. Matroska's <c>V_UNCOMPRESSED</c> is a real thing that real
/// files use, and a pipeline that can carry raw planes end to end is how every other part of this library -
/// the containers, the session, the clock, the seek logic, the frame pool and the presenter - is exercised
/// without a codec being involved at all.
/// </para>
/// <para>
/// <b>A packet</b> is one frame's planes, written one after another with each row packed tight: the luma
/// plane's stride is <c>width * bytesPerSample</c> and each chroma plane's is
/// <c>chromaWidth * bytesPerSample</c>, with no padding between rows or between planes. Ten-bit and twelve-bit
/// samples are little-endian 16-bit words justified towards the least significant bit, which is the same
/// convention a frame buffer uses.
/// </para>
/// <para>
/// <b>Codec-private data</b> is the 24-byte descriptor this class writes and parses. A Matroska
/// <c>V_UNCOMPRESSED</c> track carries none - it states its shape in ordinary track elements instead - so a
/// player builds the descriptor from those.
/// </para>
/// </remarks>
public static class RawVideoFormat
{
    /// <summary>The four bytes a descriptor starts with: <c>CBRV</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "CBRV"u8;

    /// <summary>The number of bytes in a descriptor.</summary>
    public const int DescriptorLength = 24;

    /// <summary>The descriptor version this library writes and reads.</summary>
    public const ushort Version = 0;

    /// <summary>Writes a track's shape as codec-private data.</summary>
    /// <param name="descriptor">The shape to write.</param>
    /// <returns>The 24 bytes of the descriptor.</returns>
    /// <exception cref="ArgumentException">The descriptor does not describe a decodable frame.</exception>
    public static byte[] CreateDescriptor(in RawVideoDescriptor descriptor)
    {
        if (!descriptor.IsValid)
        {
            throw new ArgumentException(
                $"An uncompressed video descriptor must state a positive size, a known layout and a bit depth of "
                + $"8, 10 or 12; this one says {descriptor}.",
                nameof(descriptor));
        }

        byte[] bytes = new byte[DescriptorLength];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)descriptor.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)descriptor.Height);
        bytes[16] = (byte)descriptor.BitDepth;
        bytes[17] = (byte)descriptor.Layout;
        bytes[18] = (byte)descriptor.Color.Primaries;
        bytes[19] = (byte)descriptor.Color.Transfer;
        bytes[20] = (byte)descriptor.Color.Matrix;
        bytes[21] = (byte)descriptor.Color.Range;
        bytes[22] = (byte)descriptor.Color.ChromaSiting;
        bytes[23] = 0;
        return bytes;
    }

    /// <summary>Reads a descriptor out of codec-private data.</summary>
    /// <param name="data">The codec-private bytes.</param>
    /// <param name="descriptor">The shape the data describes, or the default value when it is not a descriptor.</param>
    /// <returns>True when the data is a descriptor this library understands.</returns>
    public static bool TryParseDescriptor(ReadOnlySpan<byte> data, out RawVideoDescriptor descriptor)
    {
        descriptor = default;

        if (data.Length < DescriptorLength) return false;
        if (!data.Slice(0, 4).SequenceEqual(Magic)) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)) > Version) return false;

        descriptor = new RawVideoDescriptor(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4)),
            data[16],
            (VideoPixelLayout)data[17],
            new VideoColorInfo(
                (VideoColorPrimaries)data[18],
                (VideoTransferCharacteristics)data[19],
                (VideoMatrixCoefficients)data[20],
                (VideoColorRange)data[21],
                (VideoChromaSiting)data[22]));

        return descriptor.IsValid;
    }

    /// <summary>The number of visible samples in a row of one plane.</summary>
    /// <param name="descriptor">The frame's shape.</param>
    /// <param name="plane">0 for luma, 1 or 2 for chroma.</param>
    /// <returns>The sample count, or 0 for a chroma plane of a monochrome frame.</returns>
    public static int GetPlaneWidth(in RawVideoDescriptor descriptor, int plane)
    {
        if (plane == 0) return descriptor.Width;
        if (descriptor.Layout == VideoPixelLayout.Gray) return 0;

        int shift = descriptor.Layout == VideoPixelLayout.I444 ? 0 : 1;
        return (descriptor.Width + (1 << shift) - 1) >> shift;
    }

    /// <summary>The number of visible rows in one plane.</summary>
    /// <param name="descriptor">The frame's shape.</param>
    /// <param name="plane">0 for luma, 1 or 2 for chroma.</param>
    /// <returns>The row count, or 0 for a chroma plane of a monochrome frame.</returns>
    public static int GetPlaneHeight(in RawVideoDescriptor descriptor, int plane)
    {
        if (plane == 0) return descriptor.Height;
        if (descriptor.Layout == VideoPixelLayout.Gray) return 0;

        int shift = descriptor.Layout == VideoPixelLayout.I420 ? 1 : 0;
        return (descriptor.Height + (1 << shift) - 1) >> shift;
    }

    /// <summary>The number of bytes one plane occupies inside a packet.</summary>
    /// <param name="descriptor">The frame's shape.</param>
    /// <param name="plane">0 for luma, 1 or 2 for chroma.</param>
    /// <returns>The byte count.</returns>
    public static long GetPlaneByteCount(in RawVideoDescriptor descriptor, int plane) =>
        (long)GetPlaneWidth(descriptor, plane)
        * GetPlaneHeight(descriptor, plane)
        * descriptor.BytesPerSample;

    /// <summary>The number of bytes one whole frame occupies inside a packet.</summary>
    /// <param name="descriptor">The frame's shape.</param>
    /// <returns>The byte count of all three planes together.</returns>
    public static long GetFrameByteCount(in RawVideoDescriptor descriptor) =>
        GetPlaneByteCount(descriptor, 0)
        + GetPlaneByteCount(descriptor, 1)
        + GetPlaneByteCount(descriptor, 2);
}
