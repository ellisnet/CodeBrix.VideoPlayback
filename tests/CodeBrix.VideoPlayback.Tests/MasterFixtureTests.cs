using System;
using System.Collections.Generic;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The two committed master (Mode3) fixtures, made by the authoring library's Master flavour from synthetic
/// sources (see tests/assets/ASSETS.txt): what the container says about them, and - once CodeBrix.Audio.Core
/// can decode FLAC packets - what their sound decodes to.
/// </summary>
[Collection("Process-wide registries")]
public class MasterFixtureTests
{
    private const string FlacDecodeSkipReason =
        "needs CodeBrix.Audio.Core with FlacPacketCodecFactory - unskip after the Core pin is raised";

    [Fact]
    public void The_alpha_fixture_carries_a_picture_an_alpha_plane_and_flac_in_lock_step()
    {
        //Arrange
        string path = TestAssets.Path("av1-alpha-flac.cbvmaster");

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        Dictionary<int, int> chunks = new Dictionary<int, int>();
        Dictionary<int, int> keys = new Dictionary<int, int>();
        foreach (CbvIndexEntry entry in reader.Index)
        {
            chunks[entry.TrackId] = chunks.TryGetValue(entry.TrackId, out int c) ? c + 1 : 1;
            if (entry.IsKeyFrame) keys[entry.TrackId] = keys.TryGetValue(entry.TrackId, out int k) ? k + 1 : 1;
        }

        //Assert
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.FormatName.Should().Be("CodeBrix Video master (.cbvmaster)");
        reader.Tracks.Count.Should().Be(3);
        reader.Tracks[0].CodecId.Should().Be(VideoCodecIds.Av1);
        reader.Tracks[0].VideoRole.Should().Be(VideoTrackRole.Picture);
        reader.Tracks[1].CodecId.Should().Be(VideoCodecIds.Av1);
        reader.Tracks[1].VideoRole.Should().Be(VideoTrackRole.AlphaPlane);
        reader.Tracks[1].Layout.Should().Be(VideoPixelLayout.Gray);
        reader.Tracks[1].Color.Range.Should().Be(VideoColorRange.Full);
        reader.Tracks[1].Width.Should().Be(reader.Tracks[0].Width);
        reader.Tracks[2].CodecId.Should().Be(VideoCodecIds.Flac);
        reader.Tracks[2].SampleRate.Should().Be(48000);
        reader.Tracks[2].Channels.Should().Be(2);
        chunks[1].Should().Be(12);
        chunks[2].Should().Be(12);
        keys[1].Should().Be(3);
        keys[2].Should().Be(3);
    }

    [Fact]
    public void The_alpha_fixtures_flac_header_is_a_stream_header_with_streaminfo_first()
    {
        //Arrange
        string path = TestAssets.Path("av1-alpha-flac.cbvmaster");
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Act
        byte[] header = reader.Tracks[2].CodecPrivate.ToArray();

        //Assert
        header.AsSpan(0, 4).ToArray().Should().Equal("fLaC"u8.ToArray());
        (header[4] & 0x7F).Should().Be(0);
        header[7].Should().Be((byte)34);
    }

    [Fact]
    public void The_alpha_fixture_seeks_to_the_same_key_frame_in_both_video_tracks()
    {
        //Arrange
        string path = TestAssets.Path("av1-alpha-flac.cbvmaster");
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromSeconds(0.5), 1);
        List<(int Track, TimeSpan Time, bool Key)> next = new List<(int, TimeSpan, bool)>();
        while (next.Count < 3 && reader.TryReadPacket(out MediaPacket packet))
        {
            next.Add((packet.TrackId, packet.Timestamp, packet.IsKeyFrame));
        }

        //Assert
        landed.Should().Be(TimeSpan.FromSeconds(4.0 / 12));
        next.Should().Contain((1, landed, true));
        next.Should().Contain((2, landed, true));
    }

    [Fact]
    public void The_no_alpha_fixture_is_a_master_file_without_an_alpha_plane()
    {
        //Arrange
        string path = TestAssets.Path("av1-flac.cbvmaster");

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.AlphaPlaneTrack.Should().BeNull();
        reader.Tracks.Count.Should().Be(2);
        reader.Tracks[1].CodecId.Should().Be(VideoCodecIds.Flac);
    }

    [Fact]
    public void Both_fixtures_pass_the_streamable_profile()
    {
        //Arrange
        string withAlpha = TestAssets.Path("av1-alpha-flac.cbvmaster");
        string withoutAlpha = TestAssets.Path("av1-flac.cbvmaster");

        //Act
        StreamableProfileReport first = StreamableProfile.EvaluateFile(withAlpha);
        StreamableProfileReport second = StreamableProfile.EvaluateFile(withoutAlpha);

        //Assert
        first.Passes.Should().BeTrue();
        second.Passes.Should().BeTrue();
    }

    [Fact(Skip = FlacDecodeSkipReason)]
    public void The_shared_audio_output_serves_flac_packets()
    {
        //Arrange
        const string codec = VideoCodecIds.Flac;

        //Act
        bool supported = AudioDecoders.IsCodecSupported(codec);

        //Assert
        supported.Should().BeTrue();
    }

    [Fact(Skip = FlacDecodeSkipReason)]
    public void The_alpha_fixtures_flac_track_decodes_to_one_second_of_stereo()
    {
        //Arrange
        string path = TestAssets.Path("av1-alpha-flac.cbvmaster");
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo audio = reader.Tracks[2];
        IPacketSoundDecoder decoder = SharedAudioOutput.CreatePacketDecoder(audio.CodecId, audio.CodecPrivate, null);
        float[] output = new float[decoder.MaxSamplesPerPacket];
        long samples = 0;

        //Act
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != audio.Id) continue;
            samples += decoder.DecodePacket(packet.Data.Span, output);
        }

        //Assert
        decoder.SampleRate.Should().Be(48000);
        decoder.Channels.Should().Be(2);
        (samples / 2).Should().Be(48000);
    }
}
