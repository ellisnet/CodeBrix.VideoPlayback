using System;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// A bounded queue of media packets whose buffers are reused, so a demultiplexer can hand packets to a
/// decoder thread without allocating anything once playback is warm.
/// </summary>
/// <remarks>
/// <para>
/// Each slot owns a byte array that grows to fit the largest packet it has ever held and is then reused
/// forever. A packet is copied in on the producing thread and read in place on the consuming thread; the slot
/// is not released for reuse until the consumer says it has finished with it, which is what lets the consumer
/// hand the memory straight to a decoder without a second copy.
/// </para>
/// <para>
/// One producer, one consumer, both of which may be any thread. One short lock covers everything including
/// the payload copy - a few kilobytes at a few hundred packets a second, which costs nothing and removes
/// every question about what a clear does to a packet somebody is holding.
/// </para>
/// </remarks>
internal sealed class PacketRing
{
    private readonly object gate = new object();
    private readonly Slot[] slots;

    private int head;
    private int tail;
    private int count;
    private bool reading;
    private long bufferedTicks;

    internal PacketRing(int capacity)
    {
        if (capacity < 2) capacity = 2;
        slots = new Slot[capacity];
        for (int i = 0; i < capacity; i++) slots[i] = new Slot();
    }

    /// <summary>How many packets are waiting.</summary>
    internal int Count
    {
        get
        {
            lock (gate) return count;
        }
    }

    /// <summary>How many packets the ring can hold.</summary>
    internal int Capacity => slots.Length;

    /// <summary>True when no further packet will fit until one is taken out.</summary>
    internal bool IsFull
    {
        get
        {
            lock (gate) return count == slots.Length;
        }
    }

    /// <summary>The total duration of the packets waiting, as far as the container stated their durations.</summary>
    internal TimeSpan BufferedDuration
    {
        get
        {
            lock (gate) return TimeSpan.FromTicks(bufferedTicks);
        }
    }

    /// <summary>The number of bytes the ring's slot buffers currently occupy.</summary>
    internal long ResidentBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                byte[] buffer = slots[i].Buffer;
                if (buffer != null) total += buffer.Length;
            }

            return total;
        }
    }

    /// <summary>Copies a packet into the ring.</summary>
    /// <param name="data">The packet's bytes.</param>
    /// <param name="timestamp">When the packet is for.</param>
    /// <param name="duration">How long it lasts.</param>
    /// <param name="isKeyFrame">True when decoding may start here.</param>
    /// <param name="discardPadding">How much of the end of the decoded output to throw away.</param>
    /// <param name="generation">
    /// The seek generation this packet belongs to, so a consumer can recognise and skip packets queued before
    /// a seek.
    /// </param>
    /// <returns>False when the ring is full and the caller should wait.</returns>
    internal bool TryEnqueue(
        ReadOnlySpan<byte> data,
        TimeSpan timestamp,
        TimeSpan duration,
        bool isKeyFrame,
        TimeSpan discardPadding,
        int generation)
    {
        lock (gate)
        {
            if (count == slots.Length) return false;

            Slot slot = slots[tail];
            if (slot.Buffer == null || slot.Buffer.Length < data.Length)
            {
                int capacity = slot.Buffer == null ? 4096 : slot.Buffer.Length;
                while (capacity < data.Length) capacity *= 2;
                slot.Buffer = new byte[capacity];
            }

            data.CopyTo(slot.Buffer);
            slot.Length = data.Length;
            slot.TimestampTicks = timestamp.Ticks;
            slot.DurationTicks = duration.Ticks;
            slot.IsKeyFrame = isKeyFrame;
            slot.DiscardPaddingTicks = discardPadding.Ticks;
            slot.Generation = generation;

            tail = (tail + 1) % slots.Length;
            count++;
            bufferedTicks += slot.DurationTicks;
            return true;
        }
    }

    /// <summary>Looks at the packet at the front of the ring without removing it.</summary>
    /// <param name="packet">The packet, whose memory stays valid until <see cref="EndRead" /> is called.</param>
    /// <returns>False when the ring is empty.</returns>
    internal bool TryBeginRead(out RingPacket packet)
    {
        int index;
        lock (gate)
        {
            if (count == 0 || reading)
            {
                packet = default;
                return false;
            }

            index = head;
            reading = true;
        }

        Slot slot = slots[index];
        packet = new RingPacket(
            slot.Buffer.AsMemory(0, slot.Length),
            TimeSpan.FromTicks(slot.TimestampTicks),
            TimeSpan.FromTicks(slot.DurationTicks),
            slot.IsKeyFrame,
            TimeSpan.FromTicks(slot.DiscardPaddingTicks),
            slot.Generation);
        return true;
    }

    /// <summary>Releases the packet that <see cref="TryBeginRead" /> handed out, freeing its slot.</summary>
    internal void EndRead()
    {
        lock (gate)
        {
            if (!reading) return;
            reading = false;
            bufferedTicks -= slots[head].DurationTicks;
            head = (head + 1) % slots.Length;
            count--;
        }
    }

    /// <summary>
    /// Throws away every packet in the ring except one a consumer is part-way through reading, which stays
    /// valid until that consumer releases it.
    /// </summary>
    /// <remarks>
    /// Keeping the in-flight packet is what makes this safe to call from the producing thread while a
    /// consumer is holding a packet: the memory the consumer is reading is never handed back to the producer
    /// underneath it. One packet from before the clear may therefore still be consumed, which is why every
    /// packet also carries a generation the consumer can check.
    /// </remarks>
    internal void Clear()
    {
        lock (gate)
        {
            if (reading)
            {
                tail = (head + 1) % slots.Length;
                count = 1;
                bufferedTicks = slots[head].DurationTicks;
                return;
            }

            tail = head;
            count = 0;
            bufferedTicks = 0;
        }
    }

    private sealed class Slot
    {
        internal byte[] Buffer;

        internal int Length;

        internal long TimestampTicks;

        internal long DurationTicks;

        internal bool IsKeyFrame;

        internal long DiscardPaddingTicks;

        internal int Generation;
    }
}
