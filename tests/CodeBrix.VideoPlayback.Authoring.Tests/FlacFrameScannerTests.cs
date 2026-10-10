using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The managed FLAC frame scanner that turns FFmpeg's <c>.flac</c> output into a container track: the stream
/// header and one packet per frame, byte for byte - and the byte-exact round trip of those packets through the
/// bespoke container.
/// </summary>
public class FlacFrameScannerTests
{
    [Fact]
    public void A_sixteen_bit_stream_splits_into_frames_that_are_the_file_byte_for_byte()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-scan-16");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 16, 1.0);
        byte[] file = File.ReadAllBytes(path);

        //Act
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(path);

        //Assert
        scanner.SampleRate.Should().Be(48000);
        scanner.Channels.Should().Be(2);
        scanner.BitsPerSample.Should().Be(16);
        AssertFramesTileTheFile(scanner, file);
    }

    [Fact]
    public void A_twenty_four_bit_stream_splits_the_same_way()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-scan-24");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 24, 1.0);
        byte[] file = File.ReadAllBytes(path);

        //Act
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(path);

        //Assert
        scanner.BitsPerSample.Should().Be(24);
        AssertFramesTileTheFile(scanner, file);
    }

    [Fact]
    public void Frame_timestamps_count_samples_and_the_last_frame_is_shorter()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-scan-times");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 16, 1.0);

        //Act
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(path);
        IReadOnlyList<FlacFrame> frames = scanner.Frames;

        //Assert
        long total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            frames[i].FirstSample.Should().Be(total);
            scanner.TimestampOf(frames[i]).Should().Be(TimeSpan.FromTicks(total * TimeSpan.TicksPerSecond / 48000));
            total += frames[i].BlockSize;
        }

        total.Should().Be(48000);
        frames[frames.Count - 1].BlockSize.Should().BeLessThan(frames[0].BlockSize);
    }

    [Fact]
    public void The_codec_private_data_keeps_streaminfo_and_the_comment_and_drops_the_padding()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-scan-header");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 16, 0.5);

        //Act
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(path);
        byte[] header = scanner.CodecPrivate;

        //Assert
        header.AsSpan(0, 4).ToArray().Should().Equal("fLaC"u8.ToArray());
        (header[4] & 0x7F).Should().Be(0);
        List<int> types = new List<int>();
        int position = 4;
        bool last = false;
        while (!last)
        {
            last = (header[position] & 0x80) != 0;
            types.Add(header[position] & 0x7F);
            position += 4 + ((header[position + 1] << 16) | (header[position + 2] << 8) | header[position + 3]);
        }

        position.Should().Be(header.Length);
        types.Should().Equal(new[] { 0, 4 });
        (header.Length < scanner.FirstFrameOffset).Should().BeTrue();
    }

    [Fact]
    public void A_damaged_frame_is_refused_with_its_offset()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-scan-damaged");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 16, 0.5);
        byte[] file = File.ReadAllBytes(path);
        FlacFrameScanner clean = FlacFrameScanner.ScanFile(path);
        FlacFrame last = clean.Frames[clean.Frames.Count - 1];
        file[last.Offset + last.Length - 5] ^= 0x5A;

        //Act
        Action scan = () => FlacFrameScanner.Scan(file, "damaged.flac");

        //Assert
        scan.Should().Throw<VideoAuthoringException>().WithMessage("*'damaged.flac'*CRC-16 does not match*");
    }

    [Fact]
    public void Something_that_is_not_flac_is_refused()
    {
        //Arrange
        byte[] bytes = new byte[64];
        "OggS"u8.CopyTo(bytes);

        //Act
        Action scan = () => FlacFrameScanner.Scan(bytes, "not.flac");

        //Assert
        scan.Should().Throw<VideoAuthoringException>().WithMessage("*'not.flac'*'fLaC'*");
    }

    [Fact]
    public void The_frames_round_trip_through_a_master_file_byte_exact()
    {
        //Arrange
        SkipWithoutFlac();
        using WorkFolder work = new WorkFolder("flac-round-trip");
        string path = SyntheticAlphaSource.WriteFlac(work.File("tone.flac"), 16, 1.0);
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(path);

        CbvPacketAudioInput audio = new CbvPacketAudioInput(
            VideoCodecIds.Flac, scanner.CodecPrivate, scanner.SampleRate, scanner.Channels);
        foreach (FlacFrame frame in scanner.Frames)
        {
            audio.Packets.Add(new CbvAudioPacket(scanner.CopyFrame(frame), scanner.TimestampOf(frame), scanner.DurationOf(frame)));
        }

        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = work.File("sound.cbvmaster"),
            PacketAudio = audio,
        };

        //Act
        CbvAuthoringResult result = CbvAuthoring.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.Path));
        List<byte[]> packets = new List<byte[]>();
        while (reader.TryReadPacket(out MediaPacket packet)) packets.Add(packet.Data.ToArray());

        //Assert
        result.FormatVersion.Should().Be(CbvFormat.MasterVersion);
        reader.Tracks[0].CodecId.Should().Be(VideoCodecIds.Flac);
        reader.Tracks[0].CodecPrivate.ToArray().Should().Equal(scanner.CodecPrivate);
        packets.Count.Should().Be(scanner.Frames.Count);
        for (int i = 0; i < packets.Count; i++) packets[i].Should().Equal(scanner.CopyFrame(scanner.Frames[i]));
    }

    private static void AssertFramesTileTheFile(FlacFrameScanner scanner, byte[] file)
    {
        scanner.Frames.Count.Should().BeGreaterThan(1);
        scanner.Frames[0].Offset.Should().Be(scanner.FirstFrameOffset);

        using MemoryStream joined = new MemoryStream();
        foreach (FlacFrame frame in scanner.Frames)
        {
            byte[] bytes = scanner.CopyFrame(frame);
            bytes[0].Should().Be((byte)0xFF);
            joined.Write(bytes, 0, bytes.Length);
        }

        joined.ToArray().Should().Equal(file.AsSpan(scanner.FirstFrameOffset).ToArray());
    }

    private static void SkipWithoutFlac()
    {
        bool available = CbvAuthor.TryVerifyTools(out string problem);
        Assert.SkipUnless(available, problem);
    }
}
