using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the Ogg framing reader and the audio layer over it against the golden Opus and Vorbis files, and
/// against the packets the same audio has when it arrives inside a Matroska file.
/// </summary>
public class OggAudioStreamTests
{
    [Fact]
    public void An_opus_file_reports_its_head_as_the_codec_private_data()
    {
        //Arrange
        string path = TestAssets.Path("opus-audio.ogg");

        //Act
        using OggAudioStream stream = OggAudioStream.Open(path);

        //Assert
        stream.CodecId.Should().Be(VideoCodecIds.Opus);
        stream.SampleRate.Should().Be(48000);
        stream.Channels.Should().Be(1);
        stream.PreSkipSamples.Should().Be(312);
        System.Text.Encoding.ASCII.GetString(stream.CodecPrivate.Span.Slice(0, 8)).Should().Be("OpusHead");
    }

    [Fact]
    public void A_vorbis_file_packs_its_three_setup_headers_the_way_a_container_stores_them()
    {
        //Arrange
        string path = TestAssets.Path("vorbis-audio.ogg");

        //Act
        using OggAudioStream stream = OggAudioStream.Open(path);
        ReadOnlySpan<byte> codecPrivate = stream.CodecPrivate.Span;

        //Assert
        stream.CodecId.Should().Be(VideoCodecIds.Vorbis);
        stream.SampleRate.Should().Be(48000);
        stream.Channels.Should().Be(1);
        stream.PreSkipSamples.Should().Be(0);
        codecPrivate[0].Should().Be(2);
        codecPrivate.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void An_opus_packet_gets_the_duration_its_own_first_byte_states()
    {
        //Arrange
        using OggAudioStream stream = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg"));

        //Act
        IReadOnlyList<OggAudioPacket> packets = stream.ReadAllPackets();

        //Assert
        packets.Count.Should().BeGreaterThan(10);
        foreach (OggAudioPacket packet in packets)
        {
            packet.SampleCount.Should().Be(OpusPacketDuration.GetSampleCount(packet.Data.Span));
        }
    }

    [Fact]
    public void Opus_timestamps_run_forwards_without_a_gap()
    {
        //Arrange
        using OggAudioStream stream = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg"));

        //Act
        IReadOnlyList<OggAudioPacket> packets = stream.ReadAllPackets();

        //Assert
        packets[0].Timestamp.Should().Be(TimeSpan.Zero);
        for (int i = 1; i < packets.Count; i++)
        {
            packets[i].Timestamp.Should().Be(packets[i - 1].Timestamp + packets[i - 1].Duration);
        }
    }

    [Fact]
    public void Vorbis_timestamps_run_forwards_and_end_where_the_granule_says()
    {
        //Arrange
        using OggAudioStream stream = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg"));

        //Act
        IReadOnlyList<OggAudioPacket> packets = stream.ReadAllPackets();
        TimeSpan end = packets[packets.Count - 1].Timestamp + packets[packets.Count - 1].Duration;

        //Assert
        packets[0].Timestamp.Should().Be(TimeSpan.Zero);
        for (int i = 1; i < packets.Count; i++)
        {
            (packets[i].Timestamp >= packets[i - 1].Timestamp).Should().BeTrue();
        }

        (end - stream.Duration).Duration().Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void The_packets_are_the_same_bytes_the_matroska_file_carries()
    {
        //Arrange
        using OggAudioStream ogg = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg"));
        IReadOnlyList<OggAudioPacket> fromOgg = ogg.ReadAllPackets();

        using Containers.Matroska.MatroskaReader matroska =
            new Containers.Matroska.MatroskaReader(new FileMediaSource(TestAssets.Path("av1-opus.webm")));

        int audioTrackId = -1;
        foreach (Containers.MediaTrackInfo track in matroska.Tracks)
        {
            if (track.Kind == Containers.MediaTrackKind.Audio) audioTrackId = track.Id;
        }

        //Act
        List<byte[]> fromMatroska = new List<byte[]>();
        while (matroska.TryReadPacket(out Containers.MediaPacket packet))
        {
            if (packet.TrackId == audioTrackId) fromMatroska.Add(packet.Data.ToArray());
        }

        //Assert
        fromMatroska.Count.Should().Be(fromOgg.Count);
        for (int i = 0; i < fromMatroska.Count; i++)
        {
            fromMatroska[i].Should().Equal(fromOgg[i].Data.ToArray());
        }
    }

    [Fact]
    public void A_page_whose_checksum_does_not_match_is_refused()
    {
        //Arrange
        byte[] bytes = File.ReadAllBytes(TestAssets.Path("opus-audio.ogg"));
        bytes[bytes.Length - 20] ^= 0xFF;

        //Act
        Action act = () =>
        {
            using OggReader reader = new OggReader(new MemoryMediaSource(bytes, "damaged.ogg"));
            while (reader.TryReadPacket(out OggPacket _))
            {
            }
        };

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*checksum*");
    }

    [Fact]
    public void A_file_that_is_not_ogg_is_refused_by_name()
    {
        //Arrange
        byte[] bytes = new byte[64];

        //Act
        Action act = () =>
        {
            using OggReader reader = new OggReader(new MemoryMediaSource(bytes, "notogg.bin"));
            reader.TryReadPacket(out OggPacket _);
        };

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*OggS*");
    }

    [Fact]
    public void An_ogg_file_carrying_neither_opus_nor_vorbis_is_refused_with_a_reason()
    {
        //Arrange
        byte[] bytes = File.ReadAllBytes(TestAssets.Path("opus-audio.ogg"));
        bytes[28] = (byte)'X';
        RepairPageChecksum(bytes, 0);

        //Act
        Action act = () =>
        {
            using OggAudioStream stream = OggAudioStream.Open(new MemoryMediaSource(bytes, "strange.ogg"));
        };

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*Opus and Vorbis*");
    }

    [Theory]
    [InlineData(0x00, 480)]
    [InlineData(0x08, 960)]
    [InlineData(0x10, 1920)]
    [InlineData(0x18, 2880)]
    [InlineData(0x80, 120)]
    [InlineData(0xF8, 960)]
    public void Opus_frame_sizes_come_out_of_the_table_of_contents_byte(int toc, int expected)
    {
        //Arrange
        byte[] packet = new byte[] { (byte)toc, 0 };

        //Act
        int samples = OpusPacketDuration.GetSampleCount(packet);

        //Assert
        samples.Should().Be(expected);
    }

    [Fact]
    public void An_opus_packet_that_says_two_frames_lasts_twice_as_long()
    {
        //Arrange
        byte[] one = new byte[] { 0x08, 0 };
        byte[] two = new byte[] { 0x09, 0 };

        //Act
        int single = OpusPacketDuration.GetSampleCount(one);
        int pair = OpusPacketDuration.GetSampleCount(two);

        //Assert
        pair.Should().Be(single * 2);
    }

    [Fact]
    public void Splitting_a_xiph_block_undoes_building_it_exactly()
    {
        //Arrange
        byte[] identification = Fill(30, 1);
        byte[] comment = Fill(47, 3);
        byte[] setup = Fill(900, 5);

        //Act
        byte[] packed = OggAudioStream.BuildXiphCodecPrivate(identification, comment, setup);
        OggAudioStream.SplitXiphCodecPrivate(packed, out byte[] a, out byte[] b, out byte[] c);

        //Assert
        a.Should().Equal(identification);
        b.Should().Equal(comment);
        c.Should().Equal(setup);
    }

    [Theory]
    [InlineData(255, 1)]
    [InlineData(1, 255)]
    [InlineData(510, 765)]
    [InlineData(256, 254)]
    public void Splitting_undoes_building_when_a_header_lands_on_a_lacing_boundary(int first, int second)
    {
        //Arrange - a length that is an exact multiple of 255 is written as a run of 255s and a final zero,
        // which is the one place a hand-rolled length decoder normally goes wrong.
        byte[] identification = Fill(first, 1);
        byte[] comment = Fill(second, 3);
        byte[] setup = Fill(64, 5);

        //Act
        byte[] packed = OggAudioStream.BuildXiphCodecPrivate(identification, comment, setup);
        OggAudioStream.SplitXiphCodecPrivate(packed, out byte[] a, out byte[] b, out byte[] c);

        //Assert
        a.Length.Should().Be(first);
        b.Length.Should().Be(second);
        a.Should().Equal(identification);
        b.Should().Equal(comment);
        c.Should().Equal(setup);
    }

    [Fact]
    public void A_real_tracks_codec_private_splits_into_headers_typed_one_three_and_five()
    {
        //Arrange
        using OggAudioStream stream = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg"));
        byte[] codecPrivate = stream.CodecPrivate.ToArray();

        //Act
        OggAudioStream.SplitXiphCodecPrivate(
            codecPrivate, out byte[] identification, out byte[] comment, out byte[] setup);

        //Assert
        identification[0].Should().Be((byte)1);
        comment[0].Should().Be((byte)3);
        setup[0].Should().Be((byte)5);

        System.Text.Encoding.ASCII.GetString(identification, 1, 6).Should().Be("vorbis");
        System.Text.Encoding.ASCII.GetString(comment, 1, 6).Should().Be("vorbis");
        System.Text.Encoding.ASCII.GetString(setup, 1, 6).Should().Be("vorbis");

        // And packing them again produces the very block the container stored.
        OggAudioStream.BuildXiphCodecPrivate(identification, comment, setup).Should().Equal(codecPrivate);
    }

    [Fact]
    public void A_xiph_block_too_short_to_hold_anything_is_refused_by_name()
    {
        //Act
        Action act = () => OggAudioStream.SplitXiphCodecPrivate(
            new byte[] { 2, 10 }, out byte[] _, out byte[] _, out byte[] _);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*too short*");
    }

    [Fact]
    public void A_xiph_block_that_does_not_declare_three_packets_is_refused_by_name()
    {
        //Act
        Action act = () => OggAudioStream.SplitXiphCodecPrivate(
            new byte[] { 0, 1, 1, 9 }, out byte[] _, out byte[] _, out byte[] _);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*exactly three*");
    }

    [Fact]
    public void A_xiph_block_that_ends_inside_a_laced_length_is_refused_by_name()
    {
        //Act
        Action act = () => OggAudioStream.SplitXiphCodecPrivate(
            new byte[] { 2, 255, 255, 255 }, out byte[] _, out byte[] _, out byte[] _);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*laced header lengths*");
    }

    [Fact]
    public void A_xiph_block_claiming_more_than_it_carries_is_refused_by_name()
    {
        //Act
        Action act = () => OggAudioStream.SplitXiphCodecPrivate(
            new byte[] { 2, 200, 200, 1, 2, 3 }, out byte[] _, out byte[] _, out byte[] _);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*carries only*");
    }

    [Fact]
    public void A_xiph_block_with_no_setup_header_left_over_is_refused_by_name()
    {
        //Act
        Action act = () => OggAudioStream.SplitXiphCodecPrivate(
            new byte[] { 2, 1, 1, 0xAA, 0xBB }, out byte[] _, out byte[] _, out byte[] _);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*no setup header*");
    }

    private static byte[] Fill(int length, byte first)
    {
        byte[] data = new byte[length];
        data[0] = first;
        for (int i = 1; i < length; i++) data[i] = (byte)(i * 3);
        return data;
    }

    private static void RepairPageChecksum(byte[] bytes, int pageOffset)
    {
        int segments = bytes[pageOffset + 26];
        int payload = 0;
        for (int i = 0; i < segments; i++) payload += bytes[pageOffset + 27 + i];

        int total = 27 + segments + payload;
        for (int i = 0; i < 4; i++) bytes[pageOffset + 22 + i] = 0;

        uint crc = 0;
        for (int i = 0; i < total; i++)
        {
            crc = (crc << 8) ^ OggTable[((crc >> 24) & 0xFF) ^ bytes[pageOffset + i]];
        }

        bytes[pageOffset + 22] = (byte)(crc & 0xFF);
        bytes[pageOffset + 23] = (byte)((crc >> 8) & 0xFF);
        bytes[pageOffset + 24] = (byte)((crc >> 16) & 0xFF);
        bytes[pageOffset + 25] = (byte)((crc >> 24) & 0xFF);
    }

    private static readonly uint[] OggTable = BuildOggTable();

    private static uint[] BuildOggTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000u) != 0 ? (value << 1) ^ 0x04C11DB7u : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}
