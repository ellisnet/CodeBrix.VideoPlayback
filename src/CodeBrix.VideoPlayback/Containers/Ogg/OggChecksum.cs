using System;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// The page checksum Ogg uses - a CRC-32 with the generator polynomial 0x04C11DB7 computed in its DIRECT
/// (non-reflected) form, with an initial value of zero and NO final exclusive-or.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS NOT THE SAME CRC-32 AS <see cref="CodeBrix.VideoPlayback.Containers.Ebml.EbmlCrc32" />, AND THE
/// TWO NEVER AGREE.</b> Both are called "CRC-32" and both are thirty-two bits wide, and there the similarity
/// stops. EBML - and with it Matroska, WebM, zlib, zip and PNG - uses the ISO-HDLC variant: the REFLECTED
/// polynomial 0xEDB88320, an initial value of 0xFFFFFFFF, and a final exclusive-or with 0xFFFFFFFF. Ogg uses
/// the polynomial the other way round, starts at zero, and inverts nothing at either end. Feed an Ogg page to
/// the EBML one and it produces a perfectly plausible number that will never match, on every page, for ever.
/// </para>
/// <para>
/// It is published for exactly that reason: anything writing Ogg pages needs this checksum, it is the sort of
/// thing that is easy to reimplement and easy to reimplement WRONG, and the answer is thirty lines away
/// rather than an afternoon away. <see cref="OggStreamWriter" /> uses it, and so does
/// <see cref="OggReader" /> when it verifies a page it has read.
/// </para>
/// </remarks>
public static class OggChecksum
{
    private const uint Polynomial = 0x04C11DB7u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the checksum of a whole Ogg page.</summary>
    /// <param name="page">
    /// The page's bytes, header, segment table and payload together, with the four checksum bytes at offset
    /// 22 set to ZERO. A page whose own checksum field is still filled in checksums to something else.
    /// </param>
    /// <returns>The value that belongs in the page header's checksum field, stored little-endian.</returns>
    public static uint Compute(ReadOnlySpan<byte> page)
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
                value = (value & 0x80000000u) != 0 ? (value << 1) ^ Polynomial : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}
