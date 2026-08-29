using System;
using System.Buffers;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// Holds the packets of ONE track that its queue had no room for, so that the demultiplexer can carry on
/// reading the other tracks instead of stopping.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem it solves.</b> A demultiplexer that blocks when a track's queue is full stops reading the
/// FILE, not just that track - and a file is not obliged to interleave its tracks evenly. A clip whose sound
/// finishes a second before its picture has a whole second of video packets and no audio packets at all at
/// the end; if the video queue fills there, the demultiplexer stops, the end of the file is never reached,
/// and nothing ever learns that the audio track finished. Everything then waits for everything else.
/// </para>
/// <para>
/// <b>What this does about it.</b> When a track's queue is full its packets are parked here instead, and the
/// demultiplexer keeps going. Parked packets are always drained back into the queue IN ORDER and always
/// before any newer packet of the same track, so a track's sequence is exactly what it was in the file.
/// </para>
/// <para>
/// <b>What bounds it.</b> A byte budget, stated by the caller. The demultiplexer stops reading only when a
/// track can take nothing at all - its queue full AND its parking at budget - which for any sanely
/// interleaved file does not happen, and for a pathological one bounds the memory rather than the wait.
/// </para>
/// <para>Used from the demultiplexing thread alone.</para>
/// </remarks>
internal sealed class TrackParkingBuffer
{
    private readonly Queue<ParkedPacket> parked = new Queue<ParkedPacket>();
    private readonly long byteBudget;

    private long bytes;
    private long peakBytes;
    private long parkedTotal;

    /// <summary>Creates a parking buffer with a byte budget.</summary>
    /// <param name="byteBudget">
    /// How many bytes of packets may wait here before the demultiplexer has to stop reading for this track.
    /// </param>
    internal TrackParkingBuffer(long byteBudget)
    {
        this.byteBudget = byteBudget < 0 ? 0 : byteBudget;
    }

    /// <summary>True when nothing is waiting, which is the ordinary state.</summary>
    internal bool IsEmpty => parked.Count == 0;

    /// <summary>How many packets are waiting.</summary>
    internal int Count => parked.Count;

    /// <summary>How many bytes of packets are waiting.</summary>
    internal long Bytes => bytes;

    /// <summary>The most bytes that have ever waited here since the last <see cref="Clear" />.</summary>
    internal long PeakBytes => peakBytes;

    /// <summary>How many packets have been parked altogether since the last <see cref="Clear" />.</summary>
    internal long ParkedCount => parkedTotal;

    /// <summary>True when nothing more may be parked until something has been drained out.</summary>
    internal bool IsAtBudget => bytes >= byteBudget;

    /// <summary>Puts a packet at the back of the queue of packets waiting for room.</summary>
    /// <param name="data">The packet's bytes, which are copied.</param>
    /// <param name="timestamp">When the packet is to be presented.</param>
    /// <param name="duration">How long it lasts.</param>
    /// <param name="isKeyFrame">True when it is a key frame.</param>
    /// <param name="discardPadding">How much of its tail is encoder padding.</param>
    /// <param name="generation">The seek generation it belongs to.</param>
    internal void Park(
        ReadOnlySpan<byte> data,
        TimeSpan timestamp,
        TimeSpan duration,
        bool isKeyFrame,
        TimeSpan discardPadding,
        int generation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length == 0 ? 1 : data.Length);
        data.CopyTo(buffer);

        parked.Enqueue(new ParkedPacket(buffer, data.Length, timestamp, duration, isKeyFrame, discardPadding, generation));

        bytes += data.Length;
        parkedTotal++;
        if (bytes > peakBytes) peakBytes = bytes;
    }

    /// <summary>Moves as many waiting packets into a queue as it will take, oldest first.</summary>
    /// <param name="ring">The queue to fill.</param>
    /// <returns>True when nothing is left waiting.</returns>
    internal bool TryDrainInto(PacketRing ring)
    {
        if (ring == null) return parked.Count == 0;

        while (parked.Count > 0)
        {
            ParkedPacket next = parked.Peek();

            if (!ring.TryEnqueue(
                next.Buffer.AsSpan(0, next.Length),
                next.Timestamp,
                next.Duration,
                next.IsKeyFrame,
                next.DiscardPadding,
                next.Generation))
            {
                return false;
            }

            parked.Dequeue();
            bytes -= next.Length;
            ArrayPool<byte>.Shared.Return(next.Buffer);
        }

        return true;
    }

    /// <summary>Throws away everything waiting - what a seek does.</summary>
    internal void Clear()
    {
        while (parked.Count > 0) ArrayPool<byte>.Shared.Return(parked.Dequeue().Buffer);
        bytes = 0;
        peakBytes = 0;
        parkedTotal = 0;
    }

    private readonly struct ParkedPacket
    {
        internal ParkedPacket(
            byte[] buffer,
            int length,
            TimeSpan timestamp,
            TimeSpan duration,
            bool isKeyFrame,
            TimeSpan discardPadding,
            int generation)
        {
            Buffer = buffer;
            Length = length;
            Timestamp = timestamp;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
            DiscardPadding = discardPadding;
            Generation = generation;
        }

        internal byte[] Buffer { get; }

        internal int Length { get; }

        internal TimeSpan Timestamp { get; }

        internal TimeSpan Duration { get; }

        internal bool IsKeyFrame { get; }

        internal TimeSpan DiscardPadding { get; }

        internal int Generation { get; }
    }
}
