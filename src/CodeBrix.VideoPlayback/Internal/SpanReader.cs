using System;
using System.Buffers.Binary;
using System.Text;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// A cursor over a block of bytes that reads little-endian fields and refuses to run off the end.
/// </summary>
/// <remarks>
/// Every read checks the remaining length first and raises a <see cref="VideoPlaybackException" /> naming the
/// field, so a truncated or hostile file produces a sentence rather than an index-out-of-range failure.
/// </remarks>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> data;
    private readonly string what;
    private int position;

    internal SpanReader(ReadOnlySpan<byte> data, string what)
    {
        this.data = data;
        this.what = what;
        position = 0;
    }

    internal int Position => position;

    internal int Remaining => data.Length - position;

    internal void Seek(int offset)
    {
        if (offset < 0 || offset > data.Length)
        {
            throw new VideoPlaybackException($"{what} cannot be read at offset {offset}: it is {data.Length} bytes.");
        }

        position = offset;
    }

    internal void Skip(int count) => Seek(position + count);

    internal ReadOnlySpan<byte> Take(int count, string field)
    {
        Require(count, field);
        ReadOnlySpan<byte> slice = data.Slice(position, count);
        position += count;
        return slice;
    }

    internal byte ReadByte(string field)
    {
        Require(1, field);
        return data[position++];
    }

    internal ushort ReadUInt16(string field) => BinaryPrimitives.ReadUInt16LittleEndian(Take(2, field));

    internal uint ReadUInt32(string field) => BinaryPrimitives.ReadUInt32LittleEndian(Take(4, field));

    internal ulong ReadUInt64(string field) => BinaryPrimitives.ReadUInt64LittleEndian(Take(8, field));

    internal long ReadInt64(string field) => BinaryPrimitives.ReadInt64LittleEndian(Take(8, field));

    internal double ReadDouble(string field) => BinaryPrimitives.ReadDoubleLittleEndian(Take(8, field));

    internal string ReadUtf8(int byteCount, string field) =>
        byteCount == 0 ? string.Empty : Encoding.UTF8.GetString(Take(byteCount, field));

    private void Require(int count, string field)
    {
        if (count < 0)
        {
            throw new VideoPlaybackException($"{what} declares a negative length ({count}) for {field}.");
        }

        if (count > Remaining)
        {
            throw new VideoPlaybackException(
                $"{what} is truncated: {field} needs {count} bytes at offset {position} and only {Remaining} remain.");
        }
    }
}
