using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the bespoke container end to end: what the muxer writes, the reader reads back exactly, including
/// the caption cues and chapters that live in the header region and the checksums that guard both.
/// </summary>
public class CbvContainerTests
{
    [Fact]
    public void A_written_file_reads_back_with_the_same_packets()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-roundtrip", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 24, frameRate: 12, keyFrameInterval: 4);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        List<MediaPacket> packets = new List<MediaPacket>();
        List<byte[]> payloads = new List<byte[]>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            packets.Add(packet);
            payloads.Add(packet.Data.ToArray());
        }

        //Assert
        packets.Count.Should().Be(24);
        reader.Duration.Should().Be(TimeSpan.FromTicks(TimeSpan.FromSeconds(1.0 / 12).Ticks * 24));

        long frameTicks = TimeSpan.FromSeconds(1.0 / 12).Ticks;
        for (int i = 0; i < packets.Count; i++)
        {
            payloads[i].Should().Equal(SyntheticMedia.MakeFrame(i));
            packets[i].Timestamp.Should().Be(TimeSpan.FromTicks(frameTicks * i));
            packets[i].IsKeyFrame.Should().Be(i % 4 == 0);
        }
    }

    [Fact]
    public void A_written_file_carries_both_checksums_and_they_verify()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-checksums", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 8, keyFrameInterval: 4);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.HeaderChecksumVerified.Should().BeTrue();
        reader.IndexChecksumVerified.Should().BeTrue();
        reader.Version.Should().Be(CbvFormat.Version);
        reader.Timescale.Should().Be(CbvFormat.DefaultTimescale);
        (reader.Flags & CbvHeaderFlags.HasIndex).Should().Be(CbvHeaderFlags.HasIndex);
    }

    [Fact]
    public void A_damaged_header_is_refused_rather_than_played()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-damaged", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 4, keyFrameInterval: 4);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[60] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        //Act
        Action act = () => new CbvReader(new FileMediaSource(path));

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*header checksum*");
    }

    [Fact]
    public void The_index_points_at_every_chunk_and_names_the_key_frames()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-index", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 20, keyFrameInterval: 5);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        int keyFrames = 0;
        foreach (CbvIndexEntry entry in reader.Index)
        {
            if (entry.IsKeyFrame) keyFrames++;
        }

        //Assert
        reader.Index.Count.Should().Be(20);
        keyFrames.Should().Be(4);
        reader.Index[0].Offset.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Seek_lands_on_the_key_frame_at_or_before_the_moment_asked_for()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-seek", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 40, frameRate: 20, keyFrameInterval: 10);
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromSeconds(1.2), 1);
        reader.TryReadPacket(out MediaPacket packet);

        //Assert
        landed.Should().Be(TimeSpan.FromSeconds(1.0));
        packet.Timestamp.Should().Be(TimeSpan.FromSeconds(1.0));
        packet.IsKeyFrame.Should().BeTrue();
    }

    [Fact]
    public void Caption_tracks_are_complete_the_moment_the_file_is_open()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-captions", "clip.cbv");
        CaptionTrack english = SyntheticMedia.MakeCaptionTrack(
            0,
            "en",
            CaptionTrackFlags.Default,
            (0.0, 1.0, "First line"),
            (1.5, 2.5, "Second line"));

        CaptionTrack forced = SyntheticMedia.MakeCaptionTrack(
            1,
            "en",
            CaptionTrackFlags.Forced,
            (0.5, 1.0, "A sign"));

        SyntheticMedia.WriteRawCbv(path, frameCount: 40, frameRate: 20, captions: new[] { english, forced });

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.CaptionTracks.Count.Should().Be(2);
        reader.CaptionTracks[0].AreCuesComplete.Should().BeTrue();
        reader.CaptionTracks[0].CueCount.Should().Be(2);
        reader.CaptionTracks[0].IsDefault.Should().BeTrue();
        reader.CaptionTracks[0].Cues[0].Text.Should().Be("First line");
        reader.CaptionTracks[1].IsForced.Should().BeTrue();
        reader.CaptionTracks[1].Cues[0].Text.Should().Be("A sign");
    }

    [Fact]
    public void Caption_cue_settings_and_identifiers_survive_the_round_trip()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-cue-detail", "clip.cbv");
        CaptionTrack track = CaptionFiles.ParseWebVtt(
            "WEBVTT\n\nintro\n00:00:00.000 --> 00:00:01.000 line:90% align:center\nHello\n",
            0,
            "en",
            "English",
            CaptionTrackFlags.Default);

        SyntheticMedia.WriteRawCbv(path, frameCount: 8, captions: new[] { track });

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        CaptionCue cue = reader.CaptionTracks[0].Cues[0];

        //Assert
        cue.Identifier.Should().Be("intro");
        cue.Settings.Should().Be("line:90% align:center");
        cue.Text.Should().Be("Hello");
    }

    [Fact]
    public void Chapters_survive_the_round_trip_with_every_language()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-chapters", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 60, chapters: SyntheticMedia.MakeChapters(0, 1, 2));

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Chapters.Count.Should().Be(3);
        reader.Chapters[1].Start.Should().Be(TimeSpan.FromSeconds(1));
        reader.Chapters[1].Title.Should().Be("Chapter 2");
        reader.Chapters[1].TitleFor(new[] { "fr" }).Should().Be("Chapitre 2");
        reader.Chapters[2].End.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_video_track_carries_its_colour_description()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-colour", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 4);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo video = reader.Tracks[0];

        //Assert
        video.Kind.Should().Be(MediaTrackKind.Video);
        video.CodecId.Should().Be(VideoCodecIds.Raw);
        video.Width.Should().Be(64);
        video.Height.Should().Be(36);
        video.BitDepth.Should().Be(8);
        video.Layout.Should().Be(VideoPixelLayout.I420);
        video.Color.Matrix.Should().Be(VideoMatrixCoefficients.Bt709);
        video.Color.Range.Should().Be(VideoColorRange.Limited);
        video.Color.ChromaSiting.Should().Be(VideoChromaSiting.Vertical);
        video.Language.Should().Be("en");
        video.Name.Should().Be("synthetic video");
    }

    [Fact]
    public void An_audio_track_from_an_ogg_file_round_trips_with_its_setup_headers()
    {
        //Arrange
        string ogg = TestAssets.Path("vorbis-audio.ogg");
        string path = SyntheticMedia.ScratchPath("cbv-audio", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 12, frameRate: 12, audioOggPath: ogg);

        using OggAudioStream expected = OggAudioStream.Open(ogg);
        IReadOnlyList<OggAudioPacket> expectedPackets = expected.ReadAllPackets();

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo audio = null;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Audio) audio = track;
        }

        List<byte[]> readBack = new List<byte[]>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == audio.Id) readBack.Add(packet.Data.ToArray());
        }

        //Assert
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);
        audio.SampleRate.Should().Be(expected.SampleRate);
        audio.Channels.Should().Be(expected.Channels);
        audio.CodecPrivate.Span[0].Should().Be(2);
        readBack.Count.Should().Be(expectedPackets.Count);

        for (int i = 0; i < readBack.Count; i++)
        {
            readBack[i].Should().Equal(expectedPackets[i].Data.ToArray());
        }
    }

    [Fact]
    public void The_muxer_refuses_a_track_declared_after_the_first_chunk()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-order", "clip.cbv");
        using CbvMuxer muxer = CbvMuxer.Create(path);
        int track = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            64,
            36);

        muxer.WriteChunk(track, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.FromSeconds(1), true);

        //Act
        Action act = () => muxer.AddAudioTrack(VideoCodecIds.Vorbis, ReadOnlyMemory<byte>.Empty, 48000, 2);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*before the first chunk*");
    }

    [Fact]
    public void The_muxer_refuses_a_chunk_on_a_caption_track()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-caption-chunk", "clip.cbv");
        using CbvMuxer muxer = CbvMuxer.Create(path);
        int captions = muxer.AddCaptionTrack(
            SyntheticMedia.MakeCaptionTrack(0, "en", CaptionTrackFlags.None, (0.0, 1.0, "text")));

        //Act
        Action act = () => muxer.WriteChunk(captions, new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, true);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*caption track*");
    }

    [Fact]
    public void The_muxer_refuses_a_file_with_no_tracks()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-empty", "clip.cbv");
        using CbvMuxer muxer = CbvMuxer.Create(path);

        //Act
        Action act = () => muxer.Complete();

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*at least one track*");
    }

    [Fact]
    public void The_reader_refuses_a_file_that_is_not_a_bespoke_container()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-wrong", "clip.cbv");
        byte[] pretendMatroska = new byte[128];
        pretendMatroska[0] = 0x1A;
        pretendMatroska[1] = 0x45;
        pretendMatroska[2] = 0xDF;
        pretendMatroska[3] = 0xA3;
        File.WriteAllBytes(path, pretendMatroska);

        //Act
        Action act = () => new CbvReader(new FileMediaSource(path));

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*Matroska*");
    }

    [Fact]
    public void IsCbv_recognises_the_magic_and_nothing_else()
    {
        //Arrange & Act
        bool cbv = CbvReader.IsCbv("CBVF"u8);
        bool ebml = CbvReader.IsCbv(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
        bool tooShort = CbvReader.IsCbv("CBV"u8);

        //Assert
        cbv.Should().BeTrue();
        ebml.Should().BeFalse();
        tooShort.Should().BeFalse();
    }

    [Fact]
    public void A_file_read_from_memory_matches_a_file_read_from_disk()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-memory", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 10, keyFrameInterval: 5);
        byte[] bytes = File.ReadAllBytes(path);

        //Act
        List<byte[]> fromDisk = ReadPayloads(new FileMediaSource(path));
        List<byte[]> fromMemory = ReadPayloads(new MemoryMediaSource(bytes, "in memory"));

        //Assert
        fromMemory.Count.Should().Be(fromDisk.Count);
        for (int i = 0; i < fromDisk.Count; i++) fromMemory[i].Should().Equal(fromDisk[i]);
    }

    [Fact]
    public void The_committed_av1_sample_reads_back_everything_the_muxer_put_in_it()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.cbv");

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        int video = 0;
        int audio = 0;
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (reader.Tracks[packet.TrackId - 1].Kind == MediaTrackKind.Video) video++;
            else audio++;
        }

        //Assert
        reader.Tracks.Count.Should().Be(4);
        reader.Tracks[0].CodecId.Should().Be(VideoCodecIds.Av1);
        reader.Tracks[1].CodecId.Should().Be(VideoCodecIds.Opus);
        reader.CaptionTracks.Count.Should().Be(2);
        reader.Chapters.Count.Should().Be(3);
        reader.HeaderChecksumVerified.Should().BeTrue();
        reader.IndexChecksumVerified.Should().BeTrue();
        video.Should().Be(12);
        audio.Should().Be(51);
    }

    [Fact]
    public void The_committed_av1_sample_carries_the_same_codec_data_as_the_webm_file()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.cbv");
        byte[] fromWebM;
        using (Containers.Matroska.MatroskaReader webm =
            new Containers.Matroska.MatroskaReader(new FileMediaSource(TestAssets.Path("av1-opus.webm"))))
        {
            fromWebM = webm.Tracks[0].CodecPrivate.ToArray();
        }

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        byte[] fromCbv = reader.Tracks[0].CodecPrivate.ToArray();

        //Assert
        fromCbv.Should().Equal(fromWebM);
    }

    [Fact]
    public void The_committed_uncompressed_sample_holds_sixty_decodable_frames()
    {
        //Arrange
        string path = TestAssets.Path("raw-synthetic.cbv");

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo track = reader.Tracks[0];
        RawVideoFormat.TryParseDescriptor(track.CodecPrivate.Span, out RawVideoDescriptor descriptor);

        int packets = 0;
        int keyFrames = 0;
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            packets++;
            if (packet.IsKeyFrame) keyFrames++;
            packet.Data.Length.Should().Be((int)RawVideoFormat.GetFrameByteCount(descriptor));
        }

        //Assert
        track.CodecId.Should().Be(VideoCodecIds.Raw);
        descriptor.Width.Should().Be(64);
        descriptor.Height.Should().Be(36);
        packets.Should().Be(60);
        keyFrames.Should().Be(6);
    }

    private static List<byte[]> ReadPayloads(IMediaSource source)
    {
        using CbvReader reader = new CbvReader(source);
        List<byte[]> payloads = new List<byte[]>();
        while (reader.TryReadPacket(out MediaPacket packet)) payloads.Add(packet.Data.ToArray());
        return payloads;
    }
}
