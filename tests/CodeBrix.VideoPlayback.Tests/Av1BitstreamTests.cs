using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the AV1 bitstream reader that the muxer needs: the units inside a temporal unit, the sequence
/// header's fields, which temporal units are key frames, and the codec configuration record synthesised from
/// an elementary stream - compared byte for byte with the one the encoder itself wrote into the WebM file.
/// </summary>
public class Av1BitstreamTests
{
    [Fact]
    public void The_synthesised_configuration_record_matches_the_one_in_the_webm_file()
    {
        //Arrange
        byte[] expected = ReadWebMCodecPrivate("av1-opus.webm");
        byte[] firstTemporalUnit = ReadFirstIvfFrame("av1-video-only.ivf");

        //Act
        byte[] built = Av1Bitstream.BuildCodecConfigurationRecord(firstTemporalUnit);

        //Assert
        built.Should().Equal(expected);
    }

    [Fact]
    public void The_configuration_record_starts_with_the_marker_and_version_byte()
    {
        //Arrange
        byte[] record = ReadWebMCodecPrivate("av1-opus.webm");

        //Act
        byte first = record[0];

        //Assert
        first.Should().Be(0x81);
    }

    [Fact]
    public void The_sequence_header_reports_the_size_the_container_agrees_with()
    {
        //Arrange
        byte[] record = ReadWebMCodecPrivate("av1-opus.webm");

        //Act
        Av1SequenceHeader header = Av1Bitstream.ParseCodecConfigurationRecord(record);

        //Assert
        header.MaxFrameWidth.Should().Be(96);
        header.MaxFrameHeight.Should().Be(54);
        header.SeqProfile.Should().Be(0);
        header.BitDepth.Should().Be(8);
        header.Layout.Should().Be(VideoPixelLayout.I420);
        header.Monochrome.Should().BeFalse();
    }

    [Fact]
    public void The_units_of_a_temporal_unit_are_walked_in_order()
    {
        //Arrange
        byte[] temporalUnit = ReadFirstIvfFrame("av1-video-only.ivf");

        //Act
        IReadOnlyList<Av1Bitstream.ObuSpan> units = Av1Bitstream.ReadUnits(temporalUnit);

        //Assert
        units.Count.Should().BeGreaterThan(2);
        units[0].Type.Should().Be(Av1ObuType.TemporalDelimiter);
        units[1].Type.Should().Be(Av1ObuType.SequenceHeader);
        units[units.Count - 1].Start.Should().BeLessThan(temporalUnit.Length);
    }

    [Fact]
    public void Key_frames_are_recognised_and_the_others_are_not()
    {
        //Arrange
        List<byte[]> frames = ReadAllIvfFrames("av1-video-only.ivf");
        Av1Bitstream.TryReadSequenceHeader(frames[0], out Av1SequenceHeader header, out _, out _);

        //Act
        List<int> keyFrameIndexes = new List<int>();
        for (int i = 0; i < frames.Count; i++)
        {
            if (Av1Bitstream.IsKeyFrame(frames[i], header)) keyFrameIndexes.Add(i);
        }

        //Assert
        keyFrameIndexes.Should().Equal(new List<int> { 0, 4, 8 });
    }

    [Fact]
    public void The_key_frames_are_the_ones_the_matroska_file_marks()
    {
        //Arrange
        List<byte[]> ivfFrames = ReadAllIvfFrames("av1-video-only.ivf");
        Av1Bitstream.TryReadSequenceHeader(ivfFrames[0], out Av1SequenceHeader header, out _, out _);

        List<bool> fromContainer = new List<bool>();
        using (MatroskaReader reader = new MatroskaReader(new FileMediaSource(TestAssets.Path("av1-opus.webm"))))
        {
            int videoTrack = -1;
            foreach (MediaTrackInfo track in reader.Tracks)
            {
                if (track.Kind == MediaTrackKind.Video) videoTrack = track.Id;
            }

            while (reader.TryReadPacket(out MediaPacket packet))
            {
                if (packet.TrackId == videoTrack) fromContainer.Add(packet.IsKeyFrame);
            }
        }

        //Act
        List<bool> fromBitstream = new List<bool>();
        foreach (byte[] frame in ivfFrames) fromBitstream.Add(Av1Bitstream.IsKeyFrame(frame, header));

        //Assert
        fromBitstream.Count.Should().Be(fromContainer.Count);
        fromBitstream.Should().Equal(fromContainer);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(1234567UL)]
    public void A_variable_length_integer_round_trips(ulong value)
    {
        //Arrange
        byte[] encoded = Av1Bitstream.WriteLeb128(value);
        int offset = 0;

        //Act
        ulong decoded = Av1Bitstream.ReadLeb128(encoded, ref offset, "a test value");

        //Assert
        decoded.Should().Be(value);
        offset.Should().Be(encoded.Length);
    }

    [Fact]
    public void A_unit_with_its_forbidden_bit_set_is_refused()
    {
        //Arrange
        byte[] data = new byte[] { 0x92, 0x00 };

        //Act
        Action act = () => Av1Bitstream.ReadUnits(data);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*forbidden bit*");
    }

    [Fact]
    public void A_configuration_record_that_is_too_short_is_refused()
    {
        //Arrange
        byte[] data = new byte[] { 0x81, 0x00 };

        //Act
        Action act = () => Av1Bitstream.ParseCodecConfigurationRecord(data);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*at least 4 bytes*");
    }

    [Fact]
    public void A_temporal_unit_with_no_sequence_header_cannot_produce_a_record()
    {
        //Arrange
        List<byte[]> frames = ReadAllIvfFrames("av1-video-only.ivf");

        //Act
        Action act = () => Av1Bitstream.BuildCodecConfigurationRecord(frames[1]);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*no sequence header*");
    }

    [Fact]
    public void The_ivf_header_states_the_codec_the_size_and_the_time_base()
    {
        //Arrange
        using IvfReader reader = new IvfReader(new FileMediaSource(TestAssets.Path("av1-video-only.ivf")));

        //Act
        string fourCharacterCode = reader.FourCharacterCode;

        //Assert
        fourCharacterCode.Should().Be("AV01");
        reader.Width.Should().Be(96);
        reader.Height.Should().Be(54);
        reader.TimeBase.Should().Be(TimeSpan.FromSeconds(1.0 / 12));
    }

    [Fact]
    public void Ivf_frame_timestamps_are_converted_from_the_files_own_time_base()
    {
        //Arrange
        using IvfReader reader = new IvfReader(new FileMediaSource(TestAssets.Path("av1-video-only.ivf")));

        //Act
        List<TimeSpan> timestamps = new List<TimeSpan>();
        while (reader.TryReadFrame(out ReadOnlyMemory<byte> _, out TimeSpan timestamp, out long _))
        {
            timestamps.Add(timestamp);
        }

        //Assert
        timestamps.Count.Should().Be(12);
        timestamps[0].Should().Be(TimeSpan.Zero);
        (timestamps[11] - TimeSpan.FromSeconds(11.0 / 12)).Duration()
            .Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void A_file_that_is_not_ivf_is_refused_by_name()
    {
        //Arrange
        byte[] bytes = new byte[64];

        //Act
        Action act = () => new IvfReader(new MemoryMediaSource(bytes, "notivf.bin"));

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*DKIF*");
    }

    private static byte[] ReadWebMCodecPrivate(string asset)
    {
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(TestAssets.Path(asset)));
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Video) return track.CodecPrivate.ToArray();
        }

        throw new InvalidOperationException($"'{asset}' carries no video track.");
    }

    private static byte[] ReadFirstIvfFrame(string asset)
    {
        using IvfReader reader = new IvfReader(new FileMediaSource(TestAssets.Path(asset)));
        reader.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan _, out long _);
        return data.ToArray();
    }

    private static List<byte[]> ReadAllIvfFrames(string asset)
    {
        using IvfReader reader = new IvfReader(new FileMediaSource(TestAssets.Path(asset)));
        List<byte[]> frames = new List<byte[]>();
        while (reader.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan _, out long _))
        {
            frames.Add(data.ToArray());
        }

        return frames;
    }
}
