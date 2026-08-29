using System;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// A most-significant-bit-first cursor over a block of bytes, which is how video bitstream syntax is written.
/// </summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> data;
    private readonly string what;
    private int bitPosition;

    internal BitReader(ReadOnlySpan<byte> data, string what)
    {
        this.data = data;
        this.what = what;
        bitPosition = 0;
    }

    internal int BitPosition => bitPosition;

    internal int RemainingBits => (data.Length * 8) - bitPosition;

    /// <summary>Reads one bit as a flag - the specification's <c>f(1)</c>.</summary>
    internal bool ReadFlag() => ReadBits(1) != 0;

    /// <summary>Reads a fixed number of bits, most significant first - the specification's <c>f(n)</c>.</summary>
    internal uint ReadBits(int count)
    {
        if (count is < 0 or > 32)
        {
            throw new VideoPlaybackException($"{what}: a bit field of {count} bits cannot be read.");
        }

        if (count > RemainingBits)
        {
            throw new VideoPlaybackException(
                $"{what} is truncated: {count} more bits were needed and only {RemainingBits} remain.");
        }

        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = bitPosition >> 3;
            int bitIndex = 7 - (bitPosition & 7);
            value = (value << 1) | (uint)((data[byteIndex] >> bitIndex) & 1);
            bitPosition++;
        }

        return value;
    }

    /// <summary>Reads a variable-length unsigned value - the specification's <c>uvlc()</c>.</summary>
    internal uint ReadUvlc()
    {
        int leadingZeros = 0;
        while (true)
        {
            if (RemainingBits <= 0)
            {
                throw new VideoPlaybackException($"{what} is truncated inside a variable-length value.");
            }

            if (ReadFlag()) break;
            leadingZeros++;

            if (leadingZeros >= 32) return uint.MaxValue;
        }

        return leadingZeros == 0 ? 0 : ReadBits(leadingZeros) + ((1u << leadingZeros) - 1);
    }
}
