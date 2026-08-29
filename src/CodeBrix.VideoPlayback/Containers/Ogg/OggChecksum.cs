using System;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// The page checksum Ogg uses - a CRC-32 with the generator polynomial 0x04C11DB7 computed in its DIRECT
/// (non-reflected) form, with no initial value and no final inversion.
/// </summary>
/// <remarks>
/// It is deliberately not the same CRC-32 as the one in zlib, in a PNG file, or in a Matroska CRC-32 element:
/// those reflect the bits and invert at both ends. Feeding an Ogg page to the wrong one produces a plausible
/// number that never matches, which is a classic way to spend an afternoon.
/// </remarks>
internal static class OggChecksum
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the checksum of a whole Ogg page.</summary>
    /// <param name="page">
    /// The page's bytes, header and payload together, with the four checksum bytes at offset 22 set to zero.
    /// </param>
    /// <returns>The checksum.</returns>
    internal static uint Compute(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
        {
            crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ page[i]];
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000u) != 0 ? (value << 1) ^ 0x04C11DB7u : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}
