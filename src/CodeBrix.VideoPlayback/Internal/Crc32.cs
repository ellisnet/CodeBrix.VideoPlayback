using System;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// CRC-32/ISO-HDLC - the checksum both container formats this library reads use.
/// </summary>
/// <remarks>
/// Polynomial 0xEDB88320 (the reflected form of 0x04C11DB7), initial value and final exclusive-or both
/// 0xFFFFFFFF. It is the same algorithm as the one in zlib and in Matroska's CRC-32 element, so a value
/// computed here can be compared with one either of those wrote.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the checksum of a block of bytes.</summary>
    /// <param name="data">The bytes to check.</param>
    /// <returns>The checksum.</returns>
    internal static uint Compute(ReadOnlySpan<byte> data) => Continue(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    /// <summary>Continues a running checksum, for data that arrives in pieces.</summary>
    /// <param name="state">The running state; start with 0xFFFFFFFF.</param>
    /// <param name="data">The next bytes.</param>
    /// <returns>The new running state. Exclusive-or it with 0xFFFFFFFF to finish.</returns>
    internal static uint Continue(uint state, ReadOnlySpan<byte> data)
    {
        uint crc = state;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
