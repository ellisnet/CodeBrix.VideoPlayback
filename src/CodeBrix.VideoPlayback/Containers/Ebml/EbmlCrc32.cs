using System;

namespace CodeBrix.VideoPlayback.Containers.Ebml;

/// <summary>
/// The CRC-32 an EBML file uses to protect a master element's contents - the ISO-HDLC variant, the same one
/// zip files and PNG chunks use.
/// </summary>
/// <remarks>
/// <para>
/// Reflected polynomial 0xEDB88320, initial value and final exclusive-or both 0xFFFFFFFF. The value covers
/// every byte of a master element's payload EXCEPT the CRC-32 element itself, which by rule is the master's
/// first child.
/// </para>
/// <para>
/// A pitfall worth knowing: EBML stores the result LITTLE-endian, unlike every other integer in the format,
/// which is big-endian. A reader that treats the four bytes as a normal EBML unsigned integer will compare
/// the value byte-reversed and reject every valid file.
/// </para>
/// </remarks>
public static class EbmlCrc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>The value a running checksum starts from, before any bytes have been added.</summary>
    public const uint InitialValue = 0xFFFFFFFFu;

    /// <summary>Adds bytes to a running checksum.</summary>
    /// <param name="running">
    /// The running value: <see cref="InitialValue" /> for the first call, then whatever the previous call
    /// returned.
    /// </param>
    /// <param name="data">The bytes to add.</param>
    /// <returns>The new running value. Pass it to <see cref="Finish" /> when there are no more bytes.</returns>
    public static uint Continue(uint running, ReadOnlySpan<byte> data)
    {
        uint crc = running;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>Turns a running value into the checksum itself.</summary>
    /// <param name="running">The value the last <see cref="Continue" /> returned.</param>
    /// <returns>The finished checksum.</returns>
    public static uint Finish(uint running) => running ^ 0xFFFFFFFFu;

    /// <summary>Computes the checksum of a block of bytes in one call.</summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Continue(InitialValue, data));

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
