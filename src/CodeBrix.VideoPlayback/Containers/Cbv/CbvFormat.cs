using System;
using System.Text;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The constants and small helpers that define the bespoke <c>.cbv</c> container - the numbers a reader and a
/// muxer must agree on.
/// </summary>
/// <remarks>
/// The whole layout is written out in <c>CBV-FORMAT.txt</c> at the root of this library's repository, and
/// summarised in AGENT-README.txt. Everything is little-endian and byte-packed: there is no implicit padding
/// anywhere, so a field's offset is exactly the sum of the sizes before it.
/// </remarks>
public static class CbvFormat
{
    /// <summary>The four bytes a bespoke file starts with: <c>CBVF</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "CBVF"u8;

    /// <summary>The four bytes a Matroska or WebM file starts with, which the same reader also accepts.</summary>
    public static ReadOnlySpan<byte> EbmlMagic => new byte[] { 0x1A, 0x45, 0xDF, 0xA3 };

    /// <summary>The format version this library writes and reads.</summary>
    public const ushort Version = 0;

    /// <summary>The number of bytes in the fixed header that precedes the track table.</summary>
    public const int FixedHeaderLength = 48;

    /// <summary>The offset within the fixed header of the header checksum field.</summary>
    public const int HeaderCrcOffset = 44;

    /// <summary>The number of bytes in one index entry.</summary>
    public const int IndexEntryLength = 22;

    /// <summary>The number of bytes in one chunk's header, before its payload.</summary>
    public const int ChunkHeaderLength = 22;

    /// <summary>The fixed width, in bytes, of a language field: a BCP 47 tag in ASCII, padded with zeros.</summary>
    public const int LanguageFieldLength = 36;

    /// <summary>The fixed width, in bytes, of a codec identifier field: ASCII, padded with zeros.</summary>
    public const int CodecIdFieldLength = 8;

    /// <summary>
    /// The timescale this library writes: ten million ticks per second, so a tick is 100 nanoseconds and a
    /// timestamp is exactly a <see cref="TimeSpan" /> tick count.
    /// </summary>
    public const uint DefaultTimescale = 10_000_000;

    /// <summary>The largest header region a reader will accept, as a guard against a malformed file.</summary>
    public const int MaximumHeaderLength = 64 * 1024 * 1024;

    /// <summary>The largest number of index entries a reader will accept, as a guard against a malformed file.</summary>
    public const int MaximumIndexEntries = 64 * 1024 * 1024;

    /// <summary>The largest chunk a reader will accept, as a guard against a malformed file.</summary>
    public const int MaximumChunkLength = 256 * 1024 * 1024;

    /// <summary>Reads a codec identifier out of its fixed-width, zero-padded field.</summary>
    /// <param name="field">The eight bytes of the field.</param>
    /// <returns>The identifier, trimmed of its padding and lower-cased.</returns>
    public static string ReadCodecId(ReadOnlySpan<byte> field) => ReadFixedAscii(field).ToLowerInvariant();

    /// <summary>Reads a language tag out of its fixed-width, zero-padded field.</summary>
    /// <param name="field">The thirty-six bytes of the field.</param>
    /// <returns>The tag, trimmed of its padding.</returns>
    public static string ReadLanguage(ReadOnlySpan<byte> field) => ReadFixedAscii(field);

    /// <summary>Writes a string into a fixed-width, zero-padded ASCII field.</summary>
    /// <param name="value">The text to write. Non-ASCII characters are refused.</param>
    /// <param name="field">The field to fill. It is zeroed first.</param>
    /// <param name="fieldName">The field's name, for the message if the text does not fit.</param>
    /// <exception cref="VideoPlaybackException">The text is too long for the field or is not ASCII.</exception>
    public static void WriteFixedAscii(string value, Span<byte> field, string fieldName)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value)) return;

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] <= 0x7F) continue;

            throw new VideoPlaybackException(
                $"The {fieldName} '{value}' contains a character that is not ASCII; this field holds ASCII only.");
        }

        if (value.Length > field.Length)
        {
            throw new VideoPlaybackException(
                $"The {fieldName} '{value}' is {value.Length} characters, and the field holds {field.Length}.");
        }

        Encoding.ASCII.GetBytes(value, field);
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        int length = field.IndexOf((byte)0);
        if (length < 0) length = field.Length;
        return length == 0 ? string.Empty : Encoding.ASCII.GetString(field.Slice(0, length));
    }
}
