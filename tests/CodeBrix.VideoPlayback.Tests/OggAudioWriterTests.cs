using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Writes Ogg audio files out of real, committed audio and reads them back with this library's OWN
/// <see cref="OggAudioStream" /> and <see cref="OggReader" />.
/// </summary>
/// <remarks>
/// The corpus already holds an Ogg Vorbis file and an Ogg Opus file that an encoder wrote, so the round trip
/// that matters - take a real stream apart, write it out again, read it back - needs no encoder here. Nothing
/// in this class starts a process.
/// </remarks>
public class OggAudioWriterTests
{
    [Fact]
    public void A_vorbis_stream_round_trips_through_the_writer_and_back()
    {
        //Arrange
        byte[] codecPrivate;
        int sampleRate;
        int channels;
        List<byte[]> originalPackets = new List<byte[]>();
        List<TimeSpan> endTimestamps = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            sampleRate = source.SampleRate;
            channels = source.Channels;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                originalPackets.Add(packet.Data.ToArray());
                endTimestamps.Add(packet.Timestamp + packet.Duration);
            }
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(buffer, codecPrivate, sampleRate, true))
        {
            for (int i = 0; i < originalPackets.Count; i++)
            {
                writer.WritePacket(originalPackets[i], endTimestamps[i]);
            }

            writer.Complete();
            writer.CodecId.Should().Be(VideoCodecIds.Vorbis);
            ((int)writer.PacketsWritten).Should().Be(originalPackets.Count);
        }

        byte[] written = buffer.ToArray();

        //Assert
        using OggAudioStream readBack = OggAudioStream.Open(new MemoryMediaSource(written, "rewritten.ogg"));
        readBack.CodecId.Should().Be(VideoCodecIds.Vorbis);
        readBack.SampleRate.Should().Be(sampleRate);
        readBack.Channels.Should().Be(channels);
        readBack.CodecPrivate.ToArray().Should().Equal(codecPrivate);

        IReadOnlyList<OggAudioPacket> rereadPackets = readBack.ReadAllPackets();
        rereadPackets.Count.Should().Be(originalPackets.Count);
        for (int i = 0; i < originalPackets.Count; i++)
        {
            rereadPackets[i].Data.ToArray().Should().Equal(originalPackets[i]);
        }
    }

    [Fact]
    public void A_written_vorbis_stream_has_granule_positions_that_only_ever_go_up()
    {
        //Arrange
        byte[] written = RewriteGoldenVorbis(out TimeSpan sourceDuration, out int sampleRate);

        //Act
        List<long> granules = PageGranules(written);

        //Assert
        granules.Count.Should().BeGreaterThan(2);
        for (int i = 1; i < granules.Count; i++)
        {
            (granules[i] >= granules[i - 1]).Should().BeTrue();
        }

        long last = granules[granules.Count - 1];
        long expected = (sourceDuration.Ticks * sampleRate) / TimeSpan.TicksPerSecond;
        (last > 0).Should().BeTrue();
        (Math.Abs(last - expected) <= 4).Should().BeTrue();
    }

    [Fact]
    public void An_opus_stream_round_trips_through_the_writer_and_back()
    {
        //Arrange
        byte[] codecPrivate;
        int preSkip;
        int channels;
        List<byte[]> originalPackets = new List<byte[]>();
        List<TimeSpan> endTimestamps = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            preSkip = source.PreSkipSamples;
            channels = source.Channels;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                originalPackets.Add(packet.Data.ToArray());
                endTimestamps.Add(packet.Timestamp + packet.Duration);
            }
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, codecPrivate, 0, true))
        {
            for (int i = 0; i < originalPackets.Count; i++)
            {
                writer.WritePacket(originalPackets[i], endTimestamps[i]);
            }

            writer.Complete();
            writer.CodecId.Should().Be(VideoCodecIds.Opus);
            writer.GranuleSampleRate.Should().Be(48000);
        }

        byte[] written = buffer.ToArray();

        //Assert
        using OggAudioStream readBack = OggAudioStream.Open(new MemoryMediaSource(written, "rewritten.opus"));
        readBack.CodecId.Should().Be(VideoCodecIds.Opus);
        readBack.SampleRate.Should().Be(48000);
        readBack.Channels.Should().Be(channels);
        readBack.PreSkipSamples.Should().Be(preSkip);
        readBack.CodecPrivate.ToArray().Should().Equal(codecPrivate);

        IReadOnlyList<OggAudioPacket> rereadPackets = readBack.ReadAllPackets();
        rereadPackets.Count.Should().Be(originalPackets.Count);
        for (int i = 0; i < originalPackets.Count; i++)
        {
            rereadPackets[i].Data.ToArray().Should().Equal(originalPackets[i]);

            // Opus states every packet's duration in its own first byte, so the timing survives exactly.
            rereadPackets[i].Timestamp.Should().Be(endTimestamps[i] - rereadPackets[i].Duration);
        }
    }

    [Fact]
    public void An_opus_granule_counts_at_forty_eight_kilohertz_from_the_pre_skip()
    {
        //Arrange
        byte[] head = BuildOpusHead(2, 312);
        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, head, 0, true))
        {
            writer.WritePacket(new byte[] { 0x08, 1, 2, 3 }, TimeSpan.FromMilliseconds(20));
            writer.WritePacket(new byte[] { 0x08, 4, 5, 6 }, TimeSpan.FromMilliseconds(40));
            writer.Complete();
        }

        List<long> granules = PageGranules(buffer.ToArray());

        //Assert - 20 ms at 48 kHz is 960 samples, and every granule carries the 312-sample pre-skip.
        granules[granules.Count - 1].Should().Be(2232L);
    }

    [Fact]
    public void Every_page_the_writer_emits_carries_a_checksum_the_reader_accepts()
    {
        //Arrange
        byte[] written = RewriteGoldenVorbis(out TimeSpan _, out int _);

        //Act - the reader verifies every page's checksum and refuses the file if one does not match.
        using OggReader reader = new OggReader(new MemoryMediaSource(written, "checked.ogg"));
        reader.VerifyChecksums.Should().BeTrue();

        int packets = 0;
        while (reader.TryReadPacket(out OggPacket _)) packets++;

        //Assert
        packets.Should().BeGreaterThan(3);
        (reader.PagesRead > 2).Should().BeTrue();
    }

    [Fact]
    public void The_last_page_is_the_one_that_says_it_is_the_last()
    {
        //Arrange
        byte[] written = RewriteGoldenVorbis(out TimeSpan _, out int _);

        //Act
        List<bool> lastFlags = new List<bool>();
        using OggReader reader = new OggReader(new MemoryMediaSource(written, "eos.ogg"));
        while (reader.TryReadPacket(out OggPacket packet))
        {
            if (packet.EndsPage) lastFlags.Add(packet.IsLastOfStream);
        }

        //Assert
        lastFlags[lastFlags.Count - 1].Should().BeTrue();
        for (int i = 0; i < lastFlags.Count - 1; i++) lastFlags[i].Should().BeFalse();
    }

    [Fact]
    public void A_packet_longer_than_one_page_is_split_and_reassembled_unchanged()
    {
        //Arrange - 255 segments hold 255 x 255 bytes, so this one cannot fit on a single page.
        byte[] huge = new byte[70_000];
        for (int i = 0; i < huge.Length; i++) huge[i] = (byte)(i * 7);

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggStreamWriter writer = new OggStreamWriter(buffer, 12345, true))
        {
            writer.WritePacket(huge, 1000);
            writer.WritePacket(new byte[] { 1, 2, 3 }, 2000);
            writer.Complete();
            (writer.PagesWritten > 1).Should().BeTrue();
        }

        //Assert
        List<byte[]> readBack = new List<byte[]>();
        using OggReader reader = new OggReader(new MemoryMediaSource(buffer.ToArray(), "split.ogg"));
        while (reader.TryReadPacket(out OggPacket packet)) readBack.Add(packet.Data.ToArray());

        readBack.Count.Should().Be(2);
        readBack[0].Should().Equal(huge);
        readBack[1].Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void A_packet_that_is_an_exact_multiple_of_the_segment_size_keeps_its_length()
    {
        //Arrange - 255 bytes needs TWO segments, 255 and 0, or the reader sees an unfinished packet.
        byte[] exact = new byte[255];
        for (int i = 0; i < exact.Length; i++) exact[i] = (byte)i;

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggStreamWriter writer = new OggStreamWriter(buffer, 7, true))
        {
            writer.WritePacket(exact, 255);
            writer.Complete();
        }

        //Assert
        using OggReader reader = new OggReader(new MemoryMediaSource(buffer.ToArray(), "exact.ogg"));
        reader.TryReadPacket(out OggPacket packet).Should().BeTrue();
        packet.Data.ToArray().Should().Equal(exact);
        reader.TryReadPacket(out OggPacket _).Should().BeFalse();
    }

    [Fact]
    public void A_zero_length_packet_survives_the_round_trip()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggStreamWriter writer = new OggStreamWriter(buffer, 3, true))
        {
            writer.WritePacket(ReadOnlySpan<byte>.Empty, 0);
            writer.WritePacket(new byte[] { 42 }, 1);
            writer.Complete();
        }

        //Assert
        List<byte[]> readBack = new List<byte[]>();
        using OggReader reader = new OggReader(new MemoryMediaSource(buffer.ToArray(), "empty.ogg"));
        while (reader.TryReadPacket(out OggPacket packet)) readBack.Add(packet.Data.ToArray());

        readBack.Count.Should().Be(2);
        readBack[0].Length.Should().Be(0);
        readBack[1].Should().Equal(new byte[] { 42 });
    }

    [Fact]
    public void Codec_private_data_that_is_not_an_opus_head_is_refused_with_a_reason()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        Action act = () => OggAudioWriter.CreateOpus(buffer, new byte[] { 1, 2, 3 }, 0, true);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*OpusHead*");
    }

    [Fact]
    public void Codec_private_data_that_is_not_three_xiph_headers_is_refused_with_a_reason()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        Action act = () => OggAudioWriter.CreateVorbis(buffer, new byte[] { 5, 1, 1, 9 }, 48000, true);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*three*");
    }

    [Fact]
    public void A_sample_rate_that_is_not_positive_is_refused()
    {
        //Arrange
        byte[] codecPrivate;
        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        Action act = () => OggAudioWriter.CreateVorbis(buffer, codecPrivate, 0, true);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Nothing_more_goes_into_a_completed_ogg_file()
    {
        //Arrange
        byte[] codecPrivate;
        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
        }

        MemoryStream buffer = new MemoryStream();
        using OggAudioWriter writer = OggAudioWriter.CreateVorbis(buffer, codecPrivate, 48000, true);
        writer.Complete();

        //Act
        Action writeAgain = () => writer.WritePacket(new byte[] { 1 }, TimeSpan.FromSeconds(1));
        Action completeAgain = () => writer.Complete();

        //Assert
        writeAgain.Should().Throw<InvalidOperationException>();
        completeAgain.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_file_created_by_path_reads_back_as_the_same_vorbis_stream()
    {
        //Arrange
        string folder = TestAssets.CreateTemporaryDirectory("ogg-writer");
        string path = Path.Combine(folder, "written.ogg");

        byte[] codecPrivate;
        List<byte[]> packets = new List<byte[]>();
        List<TimeSpan> ends = new List<TimeSpan>();
        int sampleRate;

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            sampleRate = source.SampleRate;
            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                packets.Add(packet.Data.ToArray());
                ends.Add(packet.Timestamp + packet.Duration);
            }
        }

        try
        {
            //Act
            using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(path, codecPrivate, sampleRate))
            {
                for (int i = 0; i < packets.Count; i++) writer.WritePacket(packets[i], ends[i]);
                writer.Complete();
            }

            //Assert
            using OggAudioStream readBack = OggAudioStream.Open(path);
            readBack.CodecPrivate.ToArray().Should().Equal(codecPrivate);
            readBack.ReadAllPackets().Count.Should().Be(packets.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_stated_final_granule_carries_an_opus_streams_trailing_trim_through_the_round_trip()
    {
        //Arrange - the golden Opus file ends inside its last packet, which only its final granule says.
        byte[] codecPrivate;
        int preSkip;
        int channels;
        int sourceTrim;
        TimeSpan sourceDuration;
        long sourceFinalGranule = 0;
        List<byte[]> originalPackets = new List<byte[]>();
        List<TimeSpan> endTimestamps = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            preSkip = source.PreSkipSamples;
            channels = source.Channels;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                originalPackets.Add(packet.Data.ToArray());
                endTimestamps.Add(packet.Timestamp + packet.Duration);
                sourceFinalGranule += packet.SampleCount;
            }

            sourceTrim = source.TrailingTrimSamples;
            sourceDuration = source.Duration;

            // The granule the source's own last page carried: everything its packets decode to, less the
            // tail the file says to throw away.
            sourceFinalGranule -= sourceTrim;
        }

        (sourceTrim > 0).Should().BeTrue();

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, codecPrivate, 0, true))
        {
            for (int i = 0; i < originalPackets.Count; i++)
            {
                writer.WritePacket(originalPackets[i], endTimestamps[i]);
            }

            writer.Complete(sourceFinalGranule);
        }

        //Assert - every field the source stated, back again, including the one timestamps cannot say.
        using OggAudioStream readBack = OggAudioStream.Open(
            new MemoryMediaSource(buffer.ToArray(), "trimmed.opus"));

        readBack.CodecId.Should().Be(VideoCodecIds.Opus);
        readBack.SampleRate.Should().Be(48000);
        readBack.Channels.Should().Be(channels);
        readBack.PreSkipSamples.Should().Be(preSkip);
        readBack.CodecPrivate.ToArray().Should().Equal(codecPrivate);

        IReadOnlyList<OggAudioPacket> rereadPackets = readBack.ReadAllPackets();
        rereadPackets.Count.Should().Be(originalPackets.Count);
        for (int i = 0; i < originalPackets.Count; i++)
        {
            rereadPackets[i].Data.ToArray().Should().Equal(originalPackets[i]);
            rereadPackets[i].Timestamp.Should().Be(endTimestamps[i] - rereadPackets[i].Duration);
        }

        readBack.TrailingTrimSamples.Should().Be(sourceTrim);
        readBack.Duration.Should().Be(sourceDuration);
        PageGranules(buffer.ToArray())[^1].Should().Be(sourceFinalGranule);
    }

    [Fact]
    public void An_opus_round_trip_that_states_no_final_granule_declares_no_trailing_trim()
    {
        //Arrange - the same stream, completed the plain way, to show what the overload is for.
        byte[] codecPrivate;
        int sourceTrim;
        List<byte[]> originalPackets = new List<byte[]>();
        List<TimeSpan> endTimestamps = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                originalPackets.Add(packet.Data.ToArray());
                endTimestamps.Add(packet.Timestamp + packet.Duration);
            }

            sourceTrim = source.TrailingTrimSamples;
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, codecPrivate, 0, true))
        {
            for (int i = 0; i < originalPackets.Count; i++)
            {
                writer.WritePacket(originalPackets[i], endTimestamps[i]);
            }

            writer.Complete();
        }

        //Assert - the packets are all there, the padding declaration is not.
        using OggAudioStream readBack = OggAudioStream.Open(
            new MemoryMediaSource(buffer.ToArray(), "untrimmed.opus"));

        readBack.ReadAllPackets().Count.Should().Be(originalPackets.Count);
        (sourceTrim > 0).Should().BeTrue();
        readBack.TrailingTrimSamples.Should().Be(0);
    }

    [Fact]
    public void A_vorbis_stream_that_states_its_own_final_granule_keeps_its_duration()
    {
        //Arrange
        byte[] codecPrivate;
        int sampleRate;
        TimeSpan sourceDuration;
        long totalSamples = 0;
        List<byte[]> packets = new List<byte[]>();
        List<TimeSpan> ends = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            sampleRate = source.SampleRate;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                packets.Add(packet.Data.ToArray());
                ends.Add(packet.Timestamp + packet.Duration);
                totalSamples += packet.SampleCount;
            }

            sourceDuration = source.Duration;
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(buffer, codecPrivate, sampleRate, true))
        {
            for (int i = 0; i < packets.Count; i++) writer.WritePacket(packets[i], ends[i]);
            writer.Complete(totalSamples);
        }

        //Assert - a Vorbis stream states where it ends in its final granule and nowhere else.
        using OggAudioStream readBack = OggAudioStream.Open(
            new MemoryMediaSource(buffer.ToArray(), "stated.ogg"));

        readBack.ReadAllPackets().Count.Should().Be(packets.Count);
        readBack.Duration.Should().Be(sourceDuration);
        PageGranules(buffer.ToArray())[^1].Should().Be(totalSamples);
    }

    [Fact]
    public void A_stated_final_granule_ends_a_vorbis_stream_where_it_says_and_not_where_its_packets_do()
    {
        //Arrange - the same audio, declared to stop a tenth of a second early.
        byte[] codecPrivate;
        int sampleRate;
        long totalSamples = 0;
        List<byte[]> packets = new List<byte[]>();
        List<TimeSpan> ends = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            sampleRate = source.SampleRate;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                packets.Add(packet.Data.ToArray());
                ends.Add(packet.Timestamp + packet.Duration);
                totalSamples += packet.SampleCount;
            }
        }

        long trimmed = totalSamples - (sampleRate / 10);
        MemoryStream buffer = new MemoryStream();

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(buffer, codecPrivate, sampleRate, true))
        {
            for (int i = 0; i < packets.Count; i++) writer.WritePacket(packets[i], ends[i]);
            writer.Complete(trimmed);
        }

        //Assert - every packet is still there; the stream just says it stops sooner.
        using OggAudioStream readBack = OggAudioStream.Open(
            new MemoryMediaSource(buffer.ToArray(), "trimmed.ogg"));

        readBack.ReadAllPackets().Count.Should().Be(packets.Count);
        readBack.Duration.Should().Be(TimeSpan.FromTicks(trimmed * TimeSpan.TicksPerSecond / sampleRate));
        PageGranules(buffer.ToArray())[^1].Should().Be(trimmed);
    }

    [Fact]
    public void A_negative_final_granule_is_refused()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();
        using OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, BuildOpusHead(2, 312), 0, true);
        writer.WritePacket(new byte[] { 0x08, 1, 2, 3 }, TimeSpan.FromMilliseconds(20));

        //Act
        Action act = () => writer.Complete(-1);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*negative*");
    }

    [Fact]
    public void A_final_granule_beyond_what_the_packets_carry_is_refused_and_the_file_can_still_be_completed()
    {
        //Arrange - 312 pre-skip plus two 20 ms packets is granule 2232, and not one sample more.
        MemoryStream buffer = new MemoryStream();
        using OggAudioWriter writer = OggAudioWriter.CreateOpus(buffer, BuildOpusHead(2, 312), 0, true);
        writer.WritePacket(new byte[] { 0x08, 1, 2, 3 }, TimeSpan.FromMilliseconds(20));
        writer.WritePacket(new byte[] { 0x08, 4, 5, 6 }, TimeSpan.FromMilliseconds(40));

        //Act
        Action tooFar = () => writer.Complete(2233);

        //Assert
        tooFar.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*2232*");

        // Nothing was written by the refusal, so the file is still there to be finished properly.
        writer.Complete(2231);
        PageGranules(buffer.ToArray())[^1].Should().Be(2231L);
    }

    [Fact]
    public void A_final_granule_that_goes_back_behind_a_page_already_written_is_refused()
    {
        //Arrange - the framing writer, where a page can be closed by hand.
        MemoryStream buffer = new MemoryStream();
        using OggStreamWriter writer = new OggStreamWriter(buffer, 9, true);
        writer.WritePacket(new byte[] { 1, 2, 3 }, 1000);
        writer.FlushPage();
        writer.WritePacket(new byte[] { 4, 5, 6 }, 2000);

        //Act
        Action act = () => writer.Complete(999);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*backwards*");

        writer.Complete(1000);
        PageGranules(buffer.ToArray())[^1].Should().Be(1000L);
    }

    [Fact]
    public void A_completed_ogg_file_refuses_a_stated_final_granule_as_well()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();
        MemoryStream pages = new MemoryStream();

        using OggAudioWriter audio = OggAudioWriter.CreateOpus(buffer, BuildOpusHead(1, 0), 0, true);
        audio.WritePacket(new byte[] { 0x08, 1 }, TimeSpan.FromMilliseconds(20));
        audio.Complete();

        using OggStreamWriter framing = new OggStreamWriter(pages, 4, true);
        framing.WritePacket(new byte[] { 1 }, 10);
        framing.Complete(10);

        //Act
        Action audioAgain = () => audio.Complete(1);
        Action framingAgain = () => framing.Complete(10);

        //Assert
        audioAgain.Should().Throw<InvalidOperationException>();
        framingAgain.Should().Throw<InvalidOperationException>();
    }

    private static byte[] RewriteGoldenVorbis(out TimeSpan sourceDuration, out int sampleRate)
    {
        byte[] codecPrivate;
        List<byte[]> packets = new List<byte[]>();
        List<TimeSpan> ends = new List<TimeSpan>();

        using (OggAudioStream source = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            codecPrivate = source.CodecPrivate.ToArray();
            sampleRate = source.SampleRate;

            foreach (OggAudioPacket packet in source.ReadAllPackets())
            {
                packets.Add(packet.Data.ToArray());
                ends.Add(packet.Timestamp + packet.Duration);
            }

            sourceDuration = source.Duration;
        }

        MemoryStream buffer = new MemoryStream();
        using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(buffer, codecPrivate, sampleRate, true))
        {
            for (int i = 0; i < packets.Count; i++) writer.WritePacket(packets[i], ends[i]);
            writer.Complete();
        }

        return buffer.ToArray();
    }

    private static List<long> PageGranules(byte[] file)
    {
        List<long> granules = new List<long>();

        using OggReader reader = new OggReader(new MemoryMediaSource(file, "granules.ogg"));
        while (reader.TryReadPacket(out OggPacket packet))
        {
            if (packet.EndsPage && packet.GranulePosition >= 0) granules.Add(packet.GranulePosition);
        }

        return granules;
    }

    private static byte[] BuildOpusHead(int channels, int preSkip)
    {
        byte[] head = new byte[19];
        System.Text.Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1;
        head[9] = (byte)channels;
        head[10] = (byte)(preSkip & 0xFF);
        head[11] = (byte)((preSkip >> 8) & 0xFF);
        head[12] = 0x80;
        head[13] = 0xBB;
        head[14] = 0;
        head[15] = 0;
        return head;
    }
}
