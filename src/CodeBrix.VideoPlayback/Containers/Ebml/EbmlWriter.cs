using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.Containers.Ebml;

/// <summary>
/// Builds EBML elements in memory, bottom up: a master element is written by building its children into a
/// writer of their own and then wrapping them, so every size is known exactly and nothing is ever written
/// with an unknown size.
/// </summary>
/// <remarks>
/// <para>
/// Sizes are written in the shortest form that can hold them, except where a caller asks for a fixed width
/// (the Segment's own size, and the positions in a seek index, are written at eight bytes so that the bytes
/// in front of them do not move when the values change). A size whose every value bit would be set in the
/// shortest form is written one byte longer, because that bit pattern is reserved for "unknown size".
/// </para>
/// <para>Integers are written big-endian in the fewest bytes that hold them, one byte at the least.</para>
/// </remarks>
internal sealed class EbmlWriter
{
    private byte[] buffer;
    private int length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The initial capacity, in bytes.</param>
    internal EbmlWriter(int capacity = 256)
    {
        buffer = new byte[Math.Max(16, capacity)];
    }

    /// <summary>How many bytes have been written.</summary>
    internal int Length => length;

    /// <summary>The bytes written so far.</summary>
    internal ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, length);

    /// <summary>Forgets everything written, keeping the storage.</summary>
    internal void Clear() => length = 0;

    /// <summary>Copies the written bytes to a stream.</summary>
    /// <param name="stream">The destination.</param>
    internal void CopyTo(Stream stream) => stream.Write(buffer, 0, length);

    /// <summary>Copies the written bytes into a new array.</summary>
    /// <returns>The bytes.</returns>
    internal byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

    /// <summary>Appends raw bytes.</summary>
    /// <param name="data">The bytes.</param>
    internal void WriteRaw(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    /// <summary>Appends one raw byte.</summary>
    /// <param name="value">The byte.</param>
    internal void WriteByte(byte value)
    {
        Ensure(1);
        buffer[length++] = value;
    }

    /// <summary>Appends an element identifier, which carries its own length marker.</summary>
    /// <param name="id">The identifier, as the constants in <see cref="EbmlIds" /> hold it.</param>
    internal void WriteId(uint id)
    {
        int bytes = MeasureId(id);
        for (int i = bytes - 1; i >= 0; i--) WriteByte((byte)(id >> (8 * i)));
    }

    /// <summary>Appends a size field.</summary>
    /// <param name="size">The size.</param>
    /// <param name="width">A fixed width in bytes (1 to 8), or 0 for the shortest form.</param>
    internal void WriteSize(long size, int width = 0)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), size, "An element size cannot be negative.");

        int bytes = width > 0 ? width : MeasureSize(size);
        if (bytes < MeasureSize(size) && width > 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"A size of {size} does not fit in {width} byte(s).");
        }

        ulong value = (ulong)size | (1UL << (7 * bytes));
        for (int i = bytes - 1; i >= 0; i--) WriteByte((byte)(value >> (8 * i)));
    }

    /// <summary>Appends an element header: identifier and size.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="size">The payload size.</param>
    /// <param name="sizeWidth">A fixed size width, or 0 for the shortest.</param>
    internal void WriteHeader(uint id, long size, int sizeWidth = 0)
    {
        WriteId(id);
        WriteSize(size, sizeWidth);
    }

    /// <summary>Appends an unsigned-integer element in the fewest bytes that hold the value.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    internal void WriteUnsigned(uint id, ulong value)
    {
        int bytes = MeasureUnsigned(value);
        WriteHeader(id, bytes);
        for (int i = bytes - 1; i >= 0; i--) WriteByte((byte)(value >> (8 * i)));
    }

    /// <summary>Appends an unsigned-integer element at a fixed width, so its size does not depend on its value.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    /// <param name="width">The width in bytes, 1 to 8.</param>
    internal void WriteUnsignedFixed(uint id, ulong value, int width)
    {
        WriteHeader(id, width);
        for (int i = width - 1; i >= 0; i--) WriteByte((byte)(value >> (8 * i)));
    }

    /// <summary>Appends a signed-integer element in the fewest bytes that hold the value.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    internal void WriteSigned(uint id, long value)
    {
        int bytes = 1;
        while (bytes < 8)
        {
            long min = -(1L << ((8 * bytes) - 1));
            long max = (1L << ((8 * bytes) - 1)) - 1;
            if (value >= min && value <= max) break;
            bytes++;
        }

        WriteHeader(id, bytes);
        for (int i = bytes - 1; i >= 0; i--) WriteByte((byte)(value >> (8 * i)));
    }

    /// <summary>Appends an eight-byte floating-point element.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    internal void WriteFloat(uint id, double value)
    {
        WriteHeader(id, 8);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        WriteRaw(bytes);
    }

    /// <summary>Appends an ASCII string element, with no terminator.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    internal void WriteAscii(uint id, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        WriteBinary(id, bytes);
    }

    /// <summary>Appends a UTF-8 string element, with no terminator.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    internal void WriteUtf8(uint id, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteBinary(id, bytes);
    }

    /// <summary>Appends a binary element.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The payload.</param>
    internal void WriteBinary(uint id, ReadOnlySpan<byte> value)
    {
        WriteHeader(id, value.Length);
        WriteRaw(value);
    }

    /// <summary>Appends a master element whose children were built in another writer.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="children">The children.</param>
    internal void WriteMaster(uint id, EbmlWriter children)
    {
        WriteHeader(id, children.Length);
        WriteRaw(children.WrittenSpan);
    }

    /// <summary>How many bytes an identifier occupies.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>1 to 4.</returns>
    internal static int MeasureId(uint id) =>
        id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;

    /// <summary>How many bytes the shortest size field for a value occupies.</summary>
    /// <param name="size">The size.</param>
    /// <returns>1 to 8.</returns>
    /// <remarks>The all-ones pattern of each width means "unknown size", so it is never produced.</remarks>
    internal static int MeasureSize(long size)
    {
        for (int bytes = 1; bytes <= 8; bytes++)
        {
            long limit = (1L << (7 * bytes)) - 1;
            if (size < limit) return bytes;
        }

        throw new ArgumentOutOfRangeException(nameof(size), size, "The size is beyond what EBML can express.");
    }

    /// <summary>How many bytes an unsigned integer occupies in its shortest form.</summary>
    /// <param name="value">The value.</param>
    /// <returns>1 to 8.</returns>
    internal static int MeasureUnsigned(ulong value)
    {
        int bytes = 1;
        while (bytes < 8 && (value >> (8 * bytes)) != 0) bytes++;
        return bytes;
    }

    /// <summary>How many bytes a whole unsigned-integer element occupies.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="value">The value.</param>
    /// <returns>The element's total length.</returns>
    internal static int MeasureUnsignedElement(uint id, ulong value)
    {
        int payload = MeasureUnsigned(value);
        return MeasureId(id) + MeasureSize(payload) + payload;
    }

    /// <summary>How many bytes a whole element occupies, given its payload length.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="payloadLength">The payload length.</param>
    /// <returns>The element's total length.</returns>
    internal static long MeasureElement(uint id, long payloadLength) =>
        MeasureId(id) + MeasureSize(payloadLength) + payloadLength;

    private void Ensure(int extra)
    {
        if (length + extra <= buffer.Length) return;

        long wanted = Math.Max((long)buffer.Length * 2, (long)length + extra);
        if (wanted > Array.MaxLength) wanted = Array.MaxLength;
        if (wanted < length + extra)
        {
            throw new VideoPlaybackException("An element grew beyond the largest buffer this process can hold.");
        }

        Array.Resize(ref buffer, (int)wanted);
    }
}
