using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// Writes one logical bitstream into an Ogg physical bitstream: packets go in, framed pages come out, with
/// the segment table, the page sequence numbers, the continuation flag and the checksums all looked after.
/// </summary>
/// <remarks>
/// <para>
/// This is the framing half of writing Ogg, the mirror of what <see cref="OggReader" /> takes apart. It knows
/// nothing about codecs: what a granule position COUNTS is the codec's business, and
/// <see cref="OggAudioWriter" /> is where that lives.
/// </para>
/// <para>
/// HOW A PAGE FILLS UP. Packets are gathered into the page being built until its segment table is full at 255
/// entries. A packet longer than the room left is split across pages and the next page carries the
/// continuation flag, exactly as RFC 3533 requires. A packet's granule position reaches the file only on the
/// page the packet FINISHES on - a page that ends in the middle of a packet carries -1 - which is what the
/// format asks for and what makes a reader's timing come out right.
/// </para>
/// <para>
/// THE LAST PAGE IS WRITTEN BY <see cref="Complete()" />, and that is what sets the end-of-stream flag. A
/// writer that is never completed produces a stream whose last page never says it is the last, which strict
/// readers refuse. <see cref="Dispose" /> deliberately does not complete for you.
/// </para>
/// <para>
/// THE LAST PAGE'S GRANULE POSITION CAN BE STATED, with <see cref="Complete(long)" />, instead of being
/// whatever the last packet carried. That is how a stream says it ENDS EARLIER than its packets do - the
/// end-trimming both Vorbis and Opus express by putting a granule position on the final page that is SMALLER
/// than decoding every packet would produce. Nothing else in the format carries that number.
/// </para>
/// </remarks>
public sealed class OggStreamWriter : IDisposable
{
    private const int MaximumSegments = 255;
    private const int PageHeaderLength = 27;

    private readonly Stream output;
    private readonly bool leaveOutputOpen;
    private readonly uint serialNumber;
    private readonly List<byte> lacing = new List<byte>(MaximumSegments);
    private readonly List<byte> body = new List<byte>(64 * 1024);

    private uint pageSequence;
    private long pageGranule = -1;
    private long lastFlushedGranule = -1;
    private bool isFirstPage = true;
    private bool continuesPreviousPage;
    private bool completed;
    private bool disposed;

    /// <summary>Creates a writer over a stream.</summary>
    /// <param name="output">Where the pages go. It must be writable; it does not have to be seekable.</param>
    /// <param name="serialNumber">
    /// The logical bitstream's serial number. Every page this writer emits carries it, and a reader uses it
    /// to tell interleaved streams apart.
    /// </param>
    /// <param name="leaveOutputOpen">True to leave the stream open when this writer is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    public OggStreamWriter(Stream output, uint serialNumber, bool leaveOutputOpen = false)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.serialNumber = serialNumber;
        this.leaveOutputOpen = leaveOutputOpen;
    }

    /// <summary>The serial number every page carries.</summary>
    public uint SerialNumber => serialNumber;

    /// <summary>How many pages have been written so far.</summary>
    public long PagesWritten => pageSequence;

    /// <summary>Adds one packet to the stream.</summary>
    /// <param name="data">
    /// The packet's bytes. A zero-length packet is legal and is written as one zero-length segment.
    /// </param>
    /// <param name="granulePosition">
    /// The codec's own position at the END of this packet. It reaches the file only on the page the packet
    /// finishes in, which is what the format asks for.
    /// </param>
    /// <exception cref="InvalidOperationException">The stream has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void WritePacket(ReadOnlySpan<byte> data, long granulePosition)
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException(
                "This Ogg stream has already been completed; nothing more can go in it.");
        }

        int offset = 0;
        int remaining = data.Length;

        while (true)
        {
            if (lacing.Count == MaximumSegments)
            {
                // The previous packet finished exactly at the page boundary, so this page closes with nothing
                // left over and the next one continues nothing.
                FlushPage(false, false);
            }

            int room = MaximumSegments - lacing.Count;
            int segmentsNeeded = (remaining / 255) + 1;
            int take = room < segmentsNeeded ? room : segmentsNeeded;

            for (int i = 0; i < take; i++)
            {
                int chunk = remaining < 255 ? remaining : 255;
                lacing.Add((byte)chunk);

                for (int b = 0; b < chunk; b++) body.Add(data[offset + b]);

                offset += chunk;
                remaining -= chunk;
            }

            if (take >= segmentsNeeded)
            {
                pageGranule = granulePosition;
                return;
            }

            // The page filled up part-way through the packet; the rest goes on the next one, which says so.
            FlushPage(false, true);
        }
    }

    /// <summary>
    /// Closes the page being built, so that whatever comes next starts a new one.
    /// </summary>
    /// <remarks>
    /// Vorbis and Opus both require their identification header to sit ALONE on the first page, and both want
    /// their remaining setup headers off the first audio page, which is what this is for. It does nothing
    /// when no page is being built.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stream has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void FlushPage()
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This Ogg stream has already been completed.");
        }

        if (lacing.Count > 0) FlushPage(false, false);
    }

    /// <summary>Writes the last page, with the end-of-stream flag set, and flushes the stream.</summary>
    /// <remarks>
    /// The last page carries the granule position of the last packet that finished on it. Use
    /// <see cref="Complete(long)" /> to state a different one, which is how a stream declares that it ends
    /// before its packets do.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stream has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Complete()
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This Ogg stream has already been completed.");
        }

        CompleteCore();
    }

    /// <summary>
    /// Writes the last page, with the end-of-stream flag set and a granule position you state, and flushes
    /// the stream.
    /// </summary>
    /// <param name="finalGranulePosition">
    /// The granule position the last page is to carry, in whatever units this stream's codec counts in. It
    /// replaces the one the last packet would have put there.
    /// </param>
    /// <remarks>
    /// <para>
    /// THE RULE THIS ENFORCES. The position may not be negative - <c>-1</c> is the format's marker for "no
    /// packet ends on this page" and not a position anything can end at - and it may not be SMALLER than a
    /// granule position already written to an earlier page, because an Ogg stream's granule positions never
    /// go backwards. Nothing else is checked here: what a granule COUNTS is the codec's business, and so is
    /// how far back a final page may legitimately reach. <see cref="OggAudioWriter.Complete(long)" /> knows
    /// what its packets carry and holds the value to that as well.
    /// </para>
    /// <para>
    /// The value is checked BEFORE anything is written, so a refused call leaves the stream exactly as it
    /// was and a completing call can still be made with a value that passes.
    /// </para>
    /// <para>
    /// STATE IT WHILE THE LAST PACKETS ARE STILL PENDING. Called on a writer whose page was already flushed,
    /// this puts the position on a page carrying no packets at all - legal Ogg, but a page some readers,
    /// <see cref="OggAudioStream" /> among them, take nothing from.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The position is negative, or below one an earlier page already carries.
    /// </exception>
    /// <exception cref="InvalidOperationException">The stream has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Complete(long finalGranulePosition)
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This Ogg stream has already been completed.");
        }

        if (finalGranulePosition < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalGranulePosition),
                finalGranulePosition,
                "A stated final granule position cannot be negative. In an Ogg page header -1 means 'no packet "
                + "ends on this page', which is not something a stream can end at.");
        }

        if (finalGranulePosition < lastFlushedGranule)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalGranulePosition),
                finalGranulePosition,
                $"An Ogg stream's granule positions never go backwards, and a page already written to this "
                + $"stream ends at granule {lastFlushedGranule}.");
        }

        pageGranule = finalGranulePosition;
        CompleteCore();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveOutputOpen) output.Dispose();
    }

    private void CompleteCore()
    {
        FlushPage(true, false);
        completed = true;
        output.Flush();
    }

    private void FlushPage(bool isLastPage, bool packetContinues)
    {
        if (lacing.Count == 0 && !isLastPage) return;

        byte[] page = new byte[PageHeaderLength + lacing.Count + body.Count];
        Span<byte> header = page.AsSpan(0, PageHeaderLength);

        header[0] = (byte)'O';
        header[1] = (byte)'g';
        header[2] = (byte)'g';
        header[3] = (byte)'S';
        header[4] = 0;

        byte headerType = 0;
        if (continuesPreviousPage) headerType |= 0x01;
        if (isFirstPage) headerType |= 0x02;
        if (isLastPage) headerType |= 0x04;
        header[5] = headerType;

        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(6, 8), pageGranule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), serialNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), pageSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(22, 4), 0);
        header[26] = (byte)lacing.Count;

        lacing.CopyTo(page, PageHeaderLength);
        body.CopyTo(page, PageHeaderLength + lacing.Count);

        // The checksum covers the whole page with its own four bytes zeroed, which they still are.
        uint checksum = OggChecksum.Compute(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), checksum);

        output.Write(page, 0, page.Length);

        // A page that ends no packet carries -1 and states nothing, so it is not a floor for anything.
        if (pageGranule >= 0) lastFlushedGranule = pageGranule;

        pageSequence++;
        isFirstPage = false;
        continuesPreviousPage = packetContinues;
        pageGranule = -1;
        lacing.Clear();
        body.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OggStreamWriter));
    }
}
