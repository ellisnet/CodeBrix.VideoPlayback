using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// Reads Ogg pages and reassembles the packets inside them, from the framing described in RFC 3533.
/// </summary>
/// <remarks>
/// <para>
/// Ogg is a framing layer, not a media container: a page carries a serial number, a granule position and a
/// table of segment lengths, and a packet is whatever run of segments ends with one shorter than 255. That
/// is all this reader does. What the packets MEAN is the codec's business, and
/// <see cref="OggAudioStream" /> is where that lives.
/// </para>
/// <para>
/// This is an authoring-time input to the bespoke muxer, written clean-room from the specification. Playback
/// never uses it: audio inside a media container arrives as bare codec packets with no Ogg framing at all.
/// </para>
/// <para>
/// Multiplexed streams are handled: pages of different serial numbers may interleave, and packets are
/// reassembled per stream. A page whose checksum does not match is refused.
/// </para>
/// </remarks>
public sealed class OggReader : IDisposable
{
    private const int MaximumPacketLength = 64 * 1024 * 1024;

    private readonly IMediaSource source;
    private readonly bool leaveSourceOpen;
    private readonly Dictionary<uint, StreamState> streams =
        new Dictionary<uint, StreamState>();

    private byte[] pageBuffer = new byte[64 * 1024];
    private long position;
    private bool endOfSource;
    private bool disposed;

    /// <summary>Opens an Ogg stream over a source.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this reader is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public OggReader(IMediaSource source, bool leaveSourceOpen = false)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.leaveSourceOpen = leaveSourceOpen;
    }

    /// <summary>True when page checksums are verified. Defaults to true.</summary>
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>How many pages have been read.</summary>
    public long PagesRead { get; private set; }

    /// <summary>Reads the next complete packet from any logical stream in the file.</summary>
    /// <param name="packet">
    /// The packet, carrying its own copy of the bytes - it stays valid for as long as you hold it.
    /// </param>
    /// <returns>True when a packet was read; false at the end of the file.</returns>
    /// <exception cref="VideoPlaybackException">The framing is malformed or a page's checksum does not match.</exception>
    public bool TryReadPacket(out OggPacket packet)
    {
        ThrowIfDisposed();

        while (true)
        {
            foreach (KeyValuePair<uint, StreamState> entry in streams)
            {
                if (!entry.Value.TryTakePacket(out packet)) continue;
                return true;
            }

            if (endOfSource)
            {
                packet = default;
                return false;
            }

            ReadPage();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveSourceOpen) source.Dispose();
    }

    private void ReadPage()
    {
        Span<byte> header = stackalloc byte[27];
        int read = source.ReadAtLeast(header);
        if (read == 0)
        {
            endOfSource = true;
            FinishStreams();
            return;
        }

        if (read < 27)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' ends with {read} bytes where an Ogg page header of 27 bytes was expected, at "
                + $"offset {position}.");
        }

        long pageStart = position;
        position += 27;

        if (header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S')
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' has no 'OggS' capture pattern at offset {pageStart}; the file is not Ogg, or is "
                + "damaged.");
        }

        if (header[4] != 0)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares Ogg stream structure version {header[4]} at offset {pageStart}; this "
                + "reader handles version 0.");
        }

        byte headerType = header[5];
        long granulePosition = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(6, 8));
        uint serialNumber = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(14, 4));
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(22, 4));
        int segmentCount = header[26];

        Span<byte> segmentTable = stackalloc byte[255];
        source.ReadExactly(segmentTable.Slice(0, segmentCount), $"an Ogg segment table at offset {pageStart}");
        position += segmentCount;

        int payloadLength = 0;
        for (int i = 0; i < segmentCount; i++) payloadLength += segmentTable[i];

        int totalLength = 27 + segmentCount + payloadLength;
        if (pageBuffer.Length < totalLength)
        {
            int capacity = pageBuffer.Length;
            while (capacity < totalLength) capacity *= 2;
            pageBuffer = new byte[capacity];
        }

        header.CopyTo(pageBuffer);
        segmentTable.Slice(0, segmentCount).CopyTo(pageBuffer.AsSpan(27));
        source.ReadExactly(pageBuffer.AsSpan(27 + segmentCount, payloadLength), $"an Ogg page at offset {pageStart}");
        position += payloadLength;
        PagesRead++;

        if (VerifyChecksums)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pageBuffer.AsSpan(22, 4), 0);
            uint computed = OggChecksum.Compute(pageBuffer.AsSpan(0, totalLength));
            if (computed != checksum)
            {
                throw new VideoPlaybackException(
                    $"'{source.Name}' has an Ogg page at offset {pageStart} whose checksum is 0x{checksum:X8} but "
                    + $"which computes to 0x{computed:X8}. The file is damaged.");
            }
        }

        if (!streams.TryGetValue(serialNumber, out StreamState state))
        {
            state = new StreamState(serialNumber);
            streams[serialNumber] = state;
        }

        state.AddPage(
            pageBuffer.AsSpan(27 + segmentCount, payloadLength),
            segmentTable.Slice(0, segmentCount),
            granulePosition,
            (headerType & 0x01) != 0,
            (headerType & 0x02) != 0,
            (headerType & 0x04) != 0);
    }

    private void FinishStreams()
    {
        foreach (KeyValuePair<uint, StreamState> entry in streams)
        {
            entry.Value.Finish();
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OggReader));
    }

    private sealed class StreamState
    {
        private readonly uint serialNumber;
        private readonly Queue<PendingPacket> ready =
            new Queue<PendingPacket>();

        private byte[] partial = new byte[8192];
        private int partialLength;
        private bool sawFirstPage;
        private bool finished;

        internal StreamState(uint serialNumber)
        {
            this.serialNumber = serialNumber;
        }

        internal void AddPage(
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> segmentTable,
            long granulePosition,
            bool continued,
            bool beginningOfStream,
            bool endOfStream)
        {
            if (!continued && partialLength > 0) partialLength = 0;

            int offset = 0;
            int lastCompletedIndex = -1;

            List<int> completedLengths = new List<int>();
            List<byte[]> completedPayloads = new List<byte[]>();

            for (int i = 0; i < segmentTable.Length; i++)
            {
                int length = segmentTable[i];
                Append(payload.Slice(offset, length));
                offset += length;

                if (length == 255) continue;

                completedPayloads.Add(partial.AsSpan(0, partialLength).ToArray());
                completedLengths.Add(partialLength);
                partialLength = 0;
                lastCompletedIndex = completedPayloads.Count - 1;
            }

            for (int i = 0; i < completedPayloads.Count; i++)
            {
                bool isLast = i == lastCompletedIndex;
                ready.Enqueue(new PendingPacket(
                    completedPayloads[i],
                    isLast ? granulePosition : -1,
                    isLast,
                    beginningOfStream && !sawFirstPage && i == 0,
                    endOfStream && isLast));
            }

            if (beginningOfStream) sawFirstPage = true;
            if (endOfStream) finished = true;
        }

        internal void Finish() => finished = true;

        internal bool TryTakePacket(out OggPacket packet)
        {
            if (ready.Count == 0)
            {
                packet = default;
                return false;
            }

            PendingPacket pending = ready.Dequeue();
            packet = new OggPacket(
                pending.Data,
                serialNumber,
                pending.GranulePosition,
                pending.EndsPage,
                pending.IsFirstOfStream,
                pending.IsLastOfStream || (finished && ready.Count == 0 && pending.EndsPage));
            return true;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            int needed = partialLength + data.Length;
            if (needed > MaximumPacketLength)
            {
                throw new VideoPlaybackException(
                    $"An Ogg packet in stream {serialNumber} grew past {MaximumPacketLength} bytes, which this "
                    + "reader refuses to assemble.");
            }

            if (partial.Length < needed)
            {
                int capacity = partial.Length;
                while (capacity < needed) capacity *= 2;
                Array.Resize(ref partial, capacity);
            }

            data.CopyTo(partial.AsSpan(partialLength));
            partialLength = needed;
        }

        private readonly struct PendingPacket
        {
            internal PendingPacket(
                byte[] data,
                long granulePosition,
                bool endsPage,
                bool isFirstOfStream,
                bool isLastOfStream)
            {
                Data = data;
                GranulePosition = granulePosition;
                EndsPage = endsPage;
                IsFirstOfStream = isFirstOfStream;
                IsLastOfStream = isLastOfStream;
            }

            internal byte[] Data { get; }

            internal long GranulePosition { get; }

            internal bool EndsPage { get; }

            internal bool IsFirstOfStream { get; }

            internal bool IsLastOfStream { get; }
        }
    }
}
