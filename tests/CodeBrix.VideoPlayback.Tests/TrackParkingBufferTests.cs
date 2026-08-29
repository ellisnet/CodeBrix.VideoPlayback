using System;
using CodeBrix.VideoPlayback.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the holding area that lets the demultiplexer keep reading when one track's queue is full: that it
/// never disturbs a track's order, that it stays inside its budget, and that a seek empties it.
/// </summary>
public class TrackParkingBufferTests
{
    [Fact]
    public void A_new_buffer_is_empty_and_takes_packets()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(1024);

        //Act
        parking.Park(new byte[] { 1, 2, 3 }, TimeSpan.Zero, TimeSpan.Zero, true, TimeSpan.Zero, 0);

        //Assert
        parking.IsEmpty.Should().BeFalse();
        parking.Count.Should().Be(1);
        parking.Bytes.Should().Be(3L);
        parking.IsAtBudget.Should().BeFalse();
    }

    [Fact]
    public void Packets_come_back_out_in_the_order_they_went_in()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(1024);
        PacketRing ring = new PacketRing(8);

        for (int i = 0; i < 5; i++)
        {
            parking.Park(
                new[] { (byte)i },
                TimeSpan.FromMilliseconds(i * 40),
                TimeSpan.FromMilliseconds(40),
                i == 0,
                TimeSpan.Zero,
                7);
        }

        //Act
        bool emptied = parking.TryDrainInto(ring);

        //Assert
        emptied.Should().BeTrue();
        parking.IsEmpty.Should().BeTrue();
        parking.Bytes.Should().Be(0L);

        for (int i = 0; i < 5; i++)
        {
            ring.TryBeginRead(out RingPacket packet).Should().BeTrue();
            packet.Data.Span[0].Should().Be((byte)i);
            packet.Timestamp.Should().Be(TimeSpan.FromMilliseconds(i * 40));
            packet.Generation.Should().Be(7);
            ring.EndRead();
        }
    }

    [Fact]
    public void A_queue_with_room_for_only_some_takes_only_some_and_keeps_the_rest_in_order()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(1024);
        PacketRing ring = new PacketRing(2);

        for (int i = 0; i < 4; i++)
        {
            parking.Park(new[] { (byte)i }, TimeSpan.Zero, TimeSpan.Zero, false, TimeSpan.Zero, 0);
        }

        //Act
        bool emptied = parking.TryDrainInto(ring);

        //Assert
        emptied.Should().BeFalse();
        parking.Count.Should().Be(2);

        ring.TryBeginRead(out RingPacket first).Should().BeTrue();
        first.Data.Span[0].Should().Be((byte)0);
        ring.EndRead();

        // Taking one out makes room for exactly one more, and it is the next in order.
        ring.TryBeginRead(out RingPacket second).Should().BeTrue();
        second.Data.Span[0].Should().Be((byte)1);
        ring.EndRead();

        parking.TryDrainInto(ring).Should().BeTrue();
        ring.TryBeginRead(out RingPacket third).Should().BeTrue();
        third.Data.Span[0].Should().Be((byte)2);
        ring.EndRead();
    }

    [Fact]
    public void The_budget_is_reported_once_the_bytes_reach_it()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(10);

        //Act
        parking.Park(new byte[6], TimeSpan.Zero, TimeSpan.Zero, false, TimeSpan.Zero, 0);
        bool underBudget = parking.IsAtBudget;
        parking.Park(new byte[6], TimeSpan.Zero, TimeSpan.Zero, false, TimeSpan.Zero, 0);

        //Assert
        underBudget.Should().BeFalse();
        parking.IsAtBudget.Should().BeTrue();
        parking.Bytes.Should().Be(12L);
        parking.PeakBytes.Should().Be(12L);
        parking.ParkedCount.Should().Be(2L);
    }

    [Fact]
    public void Clearing_throws_everything_away_which_is_what_a_seek_does()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(1024);
        parking.Park(new byte[100], TimeSpan.Zero, TimeSpan.Zero, false, TimeSpan.Zero, 0);
        parking.Park(new byte[100], TimeSpan.Zero, TimeSpan.Zero, false, TimeSpan.Zero, 0);

        //Act
        parking.Clear();

        //Assert
        parking.IsEmpty.Should().BeTrue();
        parking.Count.Should().Be(0);
        parking.Bytes.Should().Be(0L);
        parking.PeakBytes.Should().Be(0L);
    }

    [Fact]
    public void A_zero_length_packet_is_parked_and_returned_like_any_other()
    {
        //Arrange
        TrackParkingBuffer parking = new TrackParkingBuffer(1024);
        PacketRing ring = new PacketRing(4);

        //Act
        parking.Park(ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(1), TimeSpan.Zero, false, TimeSpan.Zero, 3);
        bool emptied = parking.TryDrainInto(ring);

        //Assert
        emptied.Should().BeTrue();
        ring.TryBeginRead(out RingPacket packet).Should().BeTrue();
        packet.Data.Length.Should().Be(0);
        packet.Timestamp.Should().Be(TimeSpan.FromSeconds(1));
        ring.EndRead();
    }
}
