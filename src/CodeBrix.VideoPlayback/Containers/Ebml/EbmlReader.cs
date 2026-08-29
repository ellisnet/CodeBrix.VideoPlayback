using System;
using System.Text;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Ebml;

/// <summary>
/// Reads the EBML structure RFC 8794 describes: variable-length identifiers and sizes, the primitive value
/// types, and master elements walked within explicit bounds.
/// </summary>
/// <remarks>
/// <para>
/// EBML is a binary cousin of XML - a tree of elements, each with an identifier, a length and either a
/// payload or more elements. This reader knows nothing about Matroska; it hands out element headers and
/// decodes values, and the caller decides what the identifiers mean.
/// </para>
/// <para>
/// <b>Bounds are the whole safety story.</b> Every read is given the offset its parent ends at, and an
/// element that claims to extend past that is refused rather than followed. Nothing is allocated in
/// proportion to a length until that length has been checked against both the parent and the source, so a
/// deliberately corrupt file cannot ask for a gigabyte before anybody notices.
/// </para>
/// <para>
/// <b>Nothing allocates in the hot path.</b> Element headers come back as a value type, and payload bytes go
/// into a buffer the reader owns and reuses, or into one the caller owns and lends.
/// </para>
/// <para>Used from one thread at a time, like the source underneath it.</para>
/// </remarks>
public sealed class EbmlReader : IDisposable
{
    private const int MaxIdLength = 4;
    private const int MaxSizeLength = 8;

    private readonly IMediaSource source;
    private readonly bool leaveSourceOpen;
    private byte[] scratch = new byte[4096];
    private byte[] crcChunk;
    private bool disposed;

    /// <summary>Creates a reader over a source.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this reader is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public EbmlReader(IMediaSource source, bool leaveSourceOpen = false)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        this.source = source;
        this.leaveSourceOpen = leaveSourceOpen;
    }

    /// <summary>The source the reader is reading from.</summary>
    public IMediaSource Source => source;

    /// <summary>The offset the next element will be read from.</summary>
    /// <exception cref="NotSupportedException">Set on a source that cannot seek.</exception>
    public long Position
    {
        get => source.Position;
        set => source.Position = value;
    }

    /// <summary>
    /// True to check a <c>CRC-32</c> element against the payload it protects when one is asked for. Defaults
    /// to true.
    /// </summary>
    /// <remarks>
    /// A diagnostics tool that wants to REPORT a corrupt checksum rather than fail on it turns this off and
    /// calls <see cref="ComputeCrc32" /> itself.
    /// </remarks>
    public bool VerifyCrc32 { get; set; } = true;

    /// <summary>
    /// The largest payload, in bytes, that <see cref="ReadBinary" /> and its relatives will allocate for.
    /// Defaults to 64 mebibytes.
    /// </summary>
    /// <remarks>
    /// The guard against a file that declares an enormous element. A real Matroska element that matters to a
    /// player - a codec's initialisation data, one video frame - is orders of magnitude smaller than this.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A value of zero or less was assigned.</exception>
    public long MaxBinaryElementSize
    {
        get => maxBinaryElementSize;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The element-size limit must be greater than zero.");
            }

            maxBinaryElementSize = value;
        }
    }

    private long maxBinaryElementSize = 64L * 1024 * 1024;

    /// <summary>The offset past the last byte the source can supply, or <see cref="long.MaxValue" /> when it will not say.</summary>
    public long SourceEnd => source.IsLengthKnown ? source.Length : long.MaxValue;

    /// <summary>
    /// How many elements the reader has decoded whose size field was the all-ones "unknown size" encoding.
    /// </summary>
    /// <remarks>
    /// The count grows as the file is read, so it reflects the metadata pass immediately after opening and
    /// then rises again if the clusters turn out to be unsized too. A live-authored recording has them; a file
    /// written to disk in one pass should not, and the streamable WebM profile forbids them outright.
    /// </remarks>
    public int UnknownSizeElementCount { get; private set; }

    /// <summary>Reads the next element's identifier and size.</summary>
    /// <param name="bound">
    /// The offset the enclosing element ends at. Nothing at or past it is read, and an element that claims to
    /// extend past it is refused.
    /// </param>
    /// <param name="header">The element that was read, or the default value when there was none.</param>
    /// <returns>True when an element was read; false at <paramref name="bound" /> or at the end of the source.</returns>
    /// <exception cref="VideoPlaybackException">The bytes are not a well-formed element header.</exception>
    public bool TryReadElementHeader(long bound, out EbmlElementHeader header)
    {
        header = default;
        long start = source.Position;
        if (start >= bound) return false;

        Span<byte> first = stackalloc byte[1];
        if (source.ReadAtLeast(first) != 1) return false;

        int idLength = MeasureVintLength(first[0]);
        if (idLength < 0 || idLength > MaxIdLength)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has a byte 0x{first[0]:X2} at offset {start} where an element identifier was "
                + "expected. The file is not valid EBML at this point, or the reader has lost its place.");
        }

        uint id = first[0];
        if (idLength > 1)
        {
            Span<byte> rest = stackalloc byte[MaxIdLength];
            Span<byte> wanted = rest.Slice(0, idLength - 1);
            if (source.ReadAtLeast(wanted) != wanted.Length)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' ended in the middle of an element identifier at offset {start}.");
            }

            for (int i = 0; i < wanted.Length; i++) id = (id << 8) | wanted[i];
        }

        long sizeOffset = source.Position;
        Span<byte> sizeFirst = stackalloc byte[1];
        if (source.ReadAtLeast(sizeFirst) != 1)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' ended immediately after the identifier of element 0x{id:X} at offset {start}.");
        }

        int sizeLength = MeasureVintLength(sizeFirst[0]);
        if (sizeLength < 0 || sizeLength > MaxSizeLength)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has a byte 0x{sizeFirst[0]:X2} at offset {sizeOffset} where the size of element "
                + $"0x{id:X} was expected.");
        }

        int marker = 1 << (8 - sizeLength);
        ulong value = (ulong)(sizeFirst[0] & (marker - 1));
        bool allOnes = (sizeFirst[0] & (marker - 1)) == (marker - 1);

        if (sizeLength > 1)
        {
            Span<byte> rest = stackalloc byte[MaxSizeLength];
            Span<byte> wanted = rest.Slice(0, sizeLength - 1);
            if (source.ReadAtLeast(wanted) != wanted.Length)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' ended in the middle of the size of element 0x{id:X} at offset {start}.");
            }

            for (int i = 0; i < wanted.Length; i++)
            {
                value = (value << 8) | wanted[i];
                if (wanted[i] != 0xFF) allOnes = false;
            }
        }

        long dataOffset = source.Position;
        long size = allOnes ? -1L : (long)value;

        if (!allOnes)
        {
            if (size < 0)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' declares element 0x{id:X} at offset {start} with a size of {value} bytes, "
                    + "which is larger than this reader can address.");
            }

            long end = dataOffset + size;
            if (end < dataOffset)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' declares element 0x{id:X} at offset {start} with a size of {size} bytes, "
                    + "which overflows the end of the file.");
            }

            if (end > bound)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' declares element 0x{id:X} at offset {start} as {size} bytes ending at {end}, "
                    + $"but its parent ends at {bound}. The file is malformed or truncated.");
            }
        }

        if (allOnes) UnknownSizeElementCount++;

        header = new EbmlElementHeader(id, start, dataOffset, size);
        return true;
    }

    /// <summary>Moves past an element's payload.</summary>
    /// <param name="header">The element to step over.</param>
    /// <exception cref="VideoPlaybackException">The element declared no size, so there is nothing to step over.</exception>
    public void SkipElement(in EbmlElementHeader header)
    {
        if (header.IsUnknownSize)
        {
            throw new VideoPlaybackException(
                $"Element 0x{header.Id:X} at offset {header.Offset} in '{source.Name}' declares no size, so it "
                + "cannot be stepped over; its children have to be walked instead.");
        }

        SeekTo(header.DataOffset);
        if (source.Skip(header.DataSize)) return;

        throw new VideoPlaybackException(
            $"'{source.Name}' ended before element 0x{header.Id:X} at offset {header.Offset} finished; it claims "
            + $"{header.DataSize} bytes.");
    }

    /// <summary>Reads an element's payload as an unsigned integer.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>The value; zero when the payload is empty, as EBML allows.</returns>
    /// <exception cref="VideoPlaybackException">The payload is longer than eight bytes, or the source ended.</exception>
    public ulong ReadUnsignedInteger(in EbmlElementHeader header)
    {
        ReadOnlySpan<byte> data = ReadPayloadIntoScratch(header, 8, "an unsigned integer");
        ulong value = 0;
        for (int i = 0; i < data.Length; i++) value = (value << 8) | data[i];
        return value;
    }

    /// <summary>Reads an element's payload as a signed integer.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>The value, sign-extended from however many bytes the payload used; zero when it is empty.</returns>
    /// <exception cref="VideoPlaybackException">The payload is longer than eight bytes, or the source ended.</exception>
    public long ReadSignedInteger(in EbmlElementHeader header)
    {
        ReadOnlySpan<byte> data = ReadPayloadIntoScratch(header, 8, "a signed integer");
        if (data.Length == 0) return 0;

        long value = (sbyte)data[0];
        for (int i = 1; i < data.Length; i++) value = (value << 8) | data[i];
        return value;
    }

    /// <summary>Reads an element's payload as an IEEE floating-point number.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>The value; zero when the payload is empty, as EBML allows.</returns>
    /// <exception cref="VideoPlaybackException">The payload is not 0, 4 or 8 bytes long, or the source ended.</exception>
    public double ReadFloat(in EbmlElementHeader header)
    {
        ReadOnlySpan<byte> data = ReadPayloadIntoScratch(header, 8, "a floating-point number");
        if (data.Length == 0) return 0.0;

        if (data.Length == 4)
        {
            uint bits = 0;
            for (int i = 0; i < 4; i++) bits = (bits << 8) | data[i];
            return BitConverter.UInt32BitsToSingle(bits);
        }

        if (data.Length == 8)
        {
            ulong bits = 0;
            for (int i = 0; i < 8; i++) bits = (bits << 8) | data[i];
            return BitConverter.UInt64BitsToDouble(bits);
        }

        throw new VideoPlaybackException(
            $"Element 0x{header.Id:X} at offset {header.Offset} in '{source.Name}' is {data.Length} bytes long, "
            + "but an EBML floating-point value has to be 0, 4 or 8 bytes.");
    }

    /// <summary>Reads an element's payload as text.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>
    /// The text, decoded as UTF-8 and cut at the first NUL - EBML pads a string element with NULs rather than
    /// shortening it. Never null.
    /// </returns>
    /// <exception cref="VideoPlaybackException">The payload is longer than the size limit, or the source ended.</exception>
    public string ReadString(in EbmlElementHeader header)
    {
        ReadOnlySpan<byte> data = ReadPayloadIntoScratch(header, 64 * 1024, "a string");
        int end = data.IndexOf((byte)0);
        if (end >= 0) data = data.Slice(0, end);
        return data.Length == 0 ? string.Empty : Encoding.UTF8.GetString(data);
    }

    /// <summary>Reads an element's payload as a date.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>
    /// The moment the element names. EBML counts nanoseconds from the start of 2001 in Universal Time; an
    /// empty payload means exactly that moment.
    /// </returns>
    /// <exception cref="VideoPlaybackException">The payload is not 0 or 8 bytes long, or the source ended.</exception>
    public DateTime ReadDate(in EbmlElementHeader header)
    {
        long nanoseconds = ReadSignedInteger(header);
        DateTime epoch = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddTicks(nanoseconds / 100);
    }

    /// <summary>Reads an element's payload into a new array.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>The payload's bytes. An empty payload gives an empty array.</returns>
    /// <exception cref="VideoPlaybackException">
    /// The payload is longer than <see cref="MaxBinaryElementSize" />, the element declared no size, or the
    /// source ended.
    /// </exception>
    /// <remarks>Use this for the few things worth keeping - a codec's initialisation data. Frames go through
    /// <see cref="ReadBinaryInto" />, which does not allocate.</remarks>
    public byte[] ReadBinary(in EbmlElementHeader header)
    {
        long length = RequirePayloadLength(header, MaxBinaryElementSize, "binary data");
        if (length == 0) return Array.Empty<byte>();

        byte[] result = new byte[length];
        SeekTo(header.DataOffset);
        source.ReadExactly(result, $"element 0x{header.Id:X} at offset {header.Offset}");
        return result;
    }

    /// <summary>Reads an element's payload into the reader's own reusable buffer.</summary>
    /// <param name="header">The element to read.</param>
    /// <returns>
    /// The payload. It is valid only until the next call that uses the shared buffer - which is every value
    /// read on this reader - so copy it if you need to keep it.
    /// </returns>
    /// <exception cref="VideoPlaybackException">
    /// The payload is longer than <see cref="MaxBinaryElementSize" />, the element declared no size, or the
    /// source ended.
    /// </exception>
    public ReadOnlyMemory<byte> ReadBinaryShared(in EbmlElementHeader header)
    {
        long length = RequirePayloadLength(header, MaxBinaryElementSize, "binary data");
        if (length == 0) return ReadOnlyMemory<byte>.Empty;

        EnsureScratch((int)length);
        SeekTo(header.DataOffset);
        source.ReadExactly(scratch.AsSpan(0, (int)length), $"element 0x{header.Id:X} at offset {header.Offset}");
        return scratch.AsMemory(0, (int)length);
    }

    /// <summary>Reads an element's payload into a buffer the caller owns, growing it if it is too small.</summary>
    /// <param name="header">The element to read.</param>
    /// <param name="buffer">
    /// The buffer to read into. It is replaced with a larger array when the payload does not fit, so pass it
    /// back in on the next call and the growth happens once.
    /// </param>
    /// <returns>How many bytes were written to the start of <paramref name="buffer" />.</returns>
    /// <exception cref="VideoPlaybackException">
    /// The payload is longer than <see cref="MaxBinaryElementSize" />, the element declared no size, or the
    /// source ended.
    /// </exception>
    /// <remarks>
    /// This is how a demultiplexer reads frames: the buffer belongs to the caller, so it survives while other
    /// elements are read, and it settles at the size of the largest frame and stops growing.
    /// </remarks>
    public int ReadBinaryInto(in EbmlElementHeader header, ref byte[] buffer)
    {
        long length = RequirePayloadLength(header, MaxBinaryElementSize, "binary data");
        if (length == 0)
        {
            buffer ??= Array.Empty<byte>();
            return 0;
        }

        if (buffer == null || buffer.Length < length) buffer = new byte[RoundUpCapacity((int)length)];

        SeekTo(header.DataOffset);
        source.ReadExactly(buffer.AsSpan(0, (int)length), $"element 0x{header.Id:X} at offset {header.Offset}");
        return (int)length;
    }

    /// <summary>Computes the checksum of a range of the source, reading it in chunks.</summary>
    /// <param name="offset">The absolute offset to start at.</param>
    /// <param name="length">How many bytes to cover.</param>
    /// <returns>The CRC-32 of that range.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The offset or the length is negative.</exception>
    /// <exception cref="VideoPlaybackException">The source ended inside the range.</exception>
    /// <remarks>
    /// The range is read in 64-kibibyte chunks and the checksum accumulated, so covering a large element
    /// costs a fixed amount of memory however large it is.
    /// </remarks>
    public uint ComputeCrc32(long offset, long length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), length, "The length cannot be negative.");

        crcChunk ??= new byte[64 * 1024];

        uint running = EbmlCrc32.InitialValue;
        long remaining = length;
        long at = offset;

        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, crcChunk.Length);
            Span<byte> target = crcChunk.AsSpan(0, want);
            source.ReadExactlyAt(at, target, "a checksummed range");
            running = EbmlCrc32.Continue(running, target);
            at += want;
            remaining -= want;
        }

        return EbmlCrc32.Finish(running);
    }

    /// <summary>
    /// Checks a master element's <c>CRC-32</c> child against the payload that follows it, when
    /// <see cref="VerifyCrc32" /> allows.
    /// </summary>
    /// <param name="master">The master element whose payload is protected.</param>
    /// <param name="crcElement">The <c>CRC-32</c> element, which must be the master's first child.</param>
    /// <param name="elementName">The master's name, for the message if the check fails.</param>
    /// <exception cref="VideoPlaybackException">The stored and computed checksums differ.</exception>
    /// <remarks>
    /// Does nothing when <see cref="VerifyCrc32" /> is false, when the master declared no size, or when the
    /// checksum element is not four bytes long. The stored value is little-endian, unlike every other integer
    /// in an EBML file.
    /// </remarks>
    public void VerifyMasterChecksum(in EbmlElementHeader master, in EbmlElementHeader crcElement, string elementName)
    {
        if (!VerifyCrc32 || master.IsUnknownSize || crcElement.DataSize != 4) return;
        if (!source.CanSeek) return;

        long saved = source.Position;
        try
        {
            Span<byte> stored = stackalloc byte[4];
            source.ReadExactlyAt(crcElement.DataOffset, stored, "a CRC-32 value");
            uint expected = (uint)(stored[0] | (stored[1] << 8) | (stored[2] << 16) | (stored[3] << 24));

            long start = crcElement.EndOffset;
            long length = master.EndOffset - start;
            if (length < 0) return;

            uint actual = ComputeCrc32(start, length);
            if (actual == expected) return;

            throw new VideoPlaybackException(
                $"The {elementName} element at offset {master.Offset} in '{source.Name}' carries a CRC-32 of "
                + $"0x{expected:X8} but its {length} bytes of content check as 0x{actual:X8}. The file is damaged.");
        }
        finally
        {
            SeekTo(saved);
        }
    }

    /// <summary>Reports how many bytes a variable-length integer beginning with a given byte occupies.</summary>
    /// <param name="first">The first byte of the value.</param>
    /// <returns>The length in bytes, or -1 when the byte is zero, which no valid value begins with.</returns>
    public static int MeasureVintLength(byte first)
    {
        if (first == 0) return -1;

        int length = 1;
        int mask = 0x80;
        while ((first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        return length;
    }

    /// <summary>Reads a variable-length unsigned integer out of a span, the way a Matroska block codes its track number.</summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="value">The value, with the marker bit removed.</param>
    /// <param name="length">How many bytes the value occupied.</param>
    /// <returns>True when a well-formed value was read.</returns>
    public static bool TryReadVint(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.Length == 0) return false;

        int measured = MeasureVintLength(data[0]);
        if (measured < 0 || measured > MaxSizeLength || measured > data.Length) return false;

        int marker = 1 << (8 - measured);
        ulong result = (ulong)(data[0] & (marker - 1));
        for (int i = 1; i < measured; i++) result = (result << 8) | data[i];

        value = result;
        length = measured;
        return true;
    }

    /// <summary>
    /// Reads a SIGNED variable-length integer out of a span - the form the sizes after the first one in an
    /// EBML-laced block use.
    /// </summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="value">The value, re-centred around zero.</param>
    /// <param name="length">How many bytes the value occupied.</param>
    /// <returns>True when a well-formed value was read.</returns>
    /// <remarks>
    /// The encoding is the unsigned one with the range shifted so that it straddles zero: a value coded in
    /// <c>n</c> bytes has <c>2^(7n-1) - 1</c> subtracted from it. That is what lets a lace record each frame's
    /// size as a small difference from the one before.
    /// </remarks>
    public static bool TryReadSignedVint(ReadOnlySpan<byte> data, out long value, out int length)
    {
        value = 0;
        if (!TryReadVint(data, out ulong unsigned, out length)) return false;

        long bias = (1L << ((7 * length) - 1)) - 1;
        value = (long)unsigned - bias;
        return true;
    }

    /// <summary>Releases the source unless the reader was told to leave it open.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveSourceOpen) source.Dispose();
    }

    private ReadOnlySpan<byte> ReadPayloadIntoScratch(in EbmlElementHeader header, long limit, string what)
    {
        long length = RequirePayloadLength(header, limit, what);
        if (length == 0) return ReadOnlySpan<byte>.Empty;

        EnsureScratch((int)length);
        SeekTo(header.DataOffset);
        source.ReadExactly(scratch.AsSpan(0, (int)length), $"element 0x{header.Id:X} at offset {header.Offset}");
        return scratch.AsSpan(0, (int)length);
    }

    private long RequirePayloadLength(in EbmlElementHeader header, long limit, string what)
    {
        if (header.IsUnknownSize)
        {
            throw new VideoPlaybackException(
                $"Element 0x{header.Id:X} at offset {header.Offset} in '{source.Name}' declares no size, so its "
                + $"payload cannot be read as {what}.");
        }

        if (header.DataSize > limit)
        {
            throw new VideoPlaybackException(
                $"Element 0x{header.Id:X} at offset {header.Offset} in '{source.Name}' declares {header.DataSize} "
                + $"bytes of {what}, past the {limit}-byte limit this reader will accept. The file is damaged or "
                + "deliberately malformed.");
        }

        long end = SourceEnd;
        if (end != long.MaxValue && header.EndOffset > end)
        {
            throw new VideoPlaybackException(
                $"Element 0x{header.Id:X} at offset {header.Offset} in '{source.Name}' claims {header.DataSize} "
                + $"bytes ending at {header.EndOffset}, but the file is only {end} bytes long.");
        }

        return header.DataSize;
    }

    private void SeekTo(long offset)
    {
        // Only move when a move is needed - assigning the position at all would refuse a forward-only source.
        // A forward move on such a source is still possible by reading and discarding, which is what a
        // progressive download has to do; only a genuine rewind is refused.
        long current = source.Position;
        if (current == offset) return;

        if (!source.CanSeek && offset > current)
        {
            source.Skip(offset - current);
            return;
        }

        source.Position = offset;
    }

    private void EnsureScratch(int length)
    {
        if (scratch.Length >= length) return;
        scratch = new byte[RoundUpCapacity(length)];
    }

    private static int RoundUpCapacity(int length)
    {
        int capacity = 4096;
        while (capacity < length)
        {
            if (capacity > int.MaxValue / 2) return length;
            capacity *= 2;
        }

        return capacity;
    }
}
