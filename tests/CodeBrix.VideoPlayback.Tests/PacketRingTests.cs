using System;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoPlayback.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the bounded packet queue that sits between the demultiplexing thread and the threads that consume
/// packets: it reuses its buffers, it refuses to overwrite a packet somebody is reading, and clearing it
/// after a seek leaves the in-flight packet alone.
/// </summary>
public class PacketRingTests
{
    [Fact]
    public void A_packet_comes_out_the_way_it_went_in()
    {
        //Arrange
        PacketRing ring = new PacketRing(4);
        byte[] payload = new byte[] { 1, 2, 3, 4, 5 };

        //Act
        bool queued = ring.TryEnqueue(payload, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), true, TimeSpan.Zero, 7);
        bool read = ring.TryBeginRead(out RingPacket packet);

        //Assert
        queued.Should().BeTrue();
        read.Should().BeTrue();
        packet.Data.ToArray().Should().Equal(payload);
        packet.Timestamp.Should().Be(TimeSpan.FromSeconds(2));
        packet.Duration.Should().Be(TimeSpan.FromSeconds(1));
        packet.IsKeyFrame.Should().BeTrue();
        packet.Generation.Should().Be(7);
    }

    [Fact]
    public void A_full_ring_refuses_rather_than_dropping_a_packet()
    {
        //Arrange
        PacketRing ring = new PacketRing(2);
        ring.TryEnqueue(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryEnqueue(new byte[] { 2 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);

        //Act
        bool third = ring.TryEnqueue(new byte[] { 3 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);

        //Assert
        third.Should().BeFalse();
        ring.IsFull.Should().BeTrue();
        ring.Count.Should().Be(2);
    }

    [Fact]
    public void A_slot_is_not_reused_until_the_consumer_says_it_has_finished()
    {
        //Arrange
        PacketRing ring = new PacketRing(2);
        ring.TryEnqueue(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryBeginRead(out RingPacket _);

        //Act
        bool second = ring.TryEnqueue(new byte[] { 2 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        bool third = ring.TryEnqueue(new byte[] { 3 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.EndRead();
        bool afterRelease = ring.TryEnqueue(new byte[] { 4 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);

        //Assert
        second.Should().BeTrue();
        third.Should().BeFalse();
        afterRelease.Should().BeTrue();
    }

    [Fact]
    public void Clear_keeps_the_packet_a_consumer_is_holding()
    {
        //Arrange
        PacketRing ring = new PacketRing(4);
        ring.TryEnqueue(new byte[] { 1, 1, 1 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryEnqueue(new byte[] { 2 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryEnqueue(new byte[] { 3 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryBeginRead(out RingPacket held);

        //Act
        ring.Clear();
        byte[] stillReadable = held.Data.ToArray();
        int countAfterClear = ring.Count;
        ring.EndRead();

        //Assert
        stillReadable.Should().Equal(new byte[] { 1, 1, 1 });
        countAfterClear.Should().Be(1);
        ring.Count.Should().Be(0);
    }

    [Fact]
    public void Clear_with_nothing_in_flight_empties_the_ring()
    {
        //Arrange
        PacketRing ring = new PacketRing(4);
        ring.TryEnqueue(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
        ring.TryEnqueue(new byte[] { 2 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);

        //Act
        ring.Clear();

        //Assert
        ring.Count.Should().Be(0);
        ring.TryBeginRead(out RingPacket _).Should().BeFalse();
    }

    [Fact]
    public void The_ring_stops_growing_its_buffers_once_it_has_seen_the_largest_packet()
    {
        //Arrange
        PacketRing ring = new PacketRing(4);
        byte[] big = new byte[16384];

        for (int i = 0; i < 8; i++)
        {
            ring.TryEnqueue(big, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
            ring.TryBeginRead(out RingPacket _);
            ring.EndRead();
        }

        long residentAfterWarmUp = ring.ResidentBytes;

        //Act
        for (int i = 0; i < 200; i++)
        {
            ring.TryEnqueue(big, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);
            ring.TryBeginRead(out RingPacket _);
            ring.EndRead();
        }

        //Assert
        ring.ResidentBytes.Should().Be(residentAfterWarmUp);
    }

    [Fact]
    public async Task A_producer_and_a_consumer_on_different_threads_agree_on_every_packet()
    {
        //Arrange
        PacketRing ring = new PacketRing(8);
        int total = 5000;
        int consumed = 0;
        bool mismatch = false;

        //Act
        Task producer = Task.Run(
            () =>
            {
                for (int i = 0; i < total; i++)
                {
                    byte[] payload = new byte[] { (byte)(i & 0xFF), (byte)((i >> 8) & 0xFF) };
                    while (!ring.TryEnqueue(payload, TimeSpan.FromTicks(i), TimeSpan.Zero, true, TimeSpan.Zero, 0))
                    {
                        Thread.Yield();
                    }
                }
            },
            TestContext.Current.CancellationToken);

        Task consumer = Task.Run(
            () =>
            {
                while (consumed < total)
                {
                    if (!ring.TryBeginRead(out RingPacket packet))
                    {
                        Thread.Yield();
                        continue;
                    }

                    if (packet.Timestamp.Ticks != consumed) mismatch = true;
                    if (packet.Data.Span[0] != (byte)(consumed & 0xFF)) mismatch = true;

                    ring.EndRead();
                    consumed++;
                }
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll(producer, consumer);

        //Assert
        consumed.Should().Be(total);
        mismatch.Should().BeFalse();
    }
}
