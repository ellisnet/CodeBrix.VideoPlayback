using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the whole authoring path: an encoder's IVF and Ogg output, plus caption and chapter files, go in
/// and a bespoke container comes out with the same packets, the same timing and the same metadata.
/// </summary>
public class CbvAuthoringTests
{
    [Fact]
    public void An_ivf_and_an_ogg_file_become_a_playable_container()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring", "authored.cbv");
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
            AudioLanguage = "en",
            VideoName = "picture",
            AudioName = "sound",
        };

        //Act
        CbvAuthoringResult result = CbvAuthoring.Write(request);

        //Assert
        File.Exists(output).Should().BeTrue();
        result.VideoFrameCount.Should().Be(12);
        result.AudioPacketCount.Should().BeGreaterThan(10);
        result.SizeInBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Every_video_packet_comes_back_byte_for_byte()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-video", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        });

        List<byte[]> expected = new List<byte[]>();
        using (IvfReader ivf = new IvfReader(new FileMediaSource(TestAssets.Path("av1-video-only.ivf"))))
        {
            while (ivf.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan _, out long _))
            {
                expected.Add(data.ToArray());
            }
        }

        //Act
        List<byte[]> actual = new List<byte[]>();
        using CbvReader reader = new CbvReader(new FileMediaSource(output));
        while (reader.TryReadPacket(out MediaPacket packet)) actual.Add(packet.Data.ToArray());

        //Assert
        actual.Count.Should().Be(expected.Count);
        for (int i = 0; i < expected.Count; i++) actual[i].Should().Equal(expected[i]);
    }

    [Fact]
    public void Every_audio_packet_comes_back_byte_for_byte_with_its_timestamp()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-audio", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            AudioOggPath = TestAssets.Path("vorbis-audio.ogg"),
        });

        List<OggAudioPacket> expected = new List<OggAudioPacket>();
        using (OggAudioStream ogg = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            foreach (OggAudioPacket packet in ogg.ReadAllPackets())
            {
                expected.Add(new OggAudioPacket(packet.Data.ToArray(), packet.Timestamp, packet.Duration, packet.SampleCount));
            }
        }

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(output));
        List<MediaPacket> actual = new List<MediaPacket>();
        List<byte[]> payloads = new List<byte[]>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            actual.Add(packet);
            payloads.Add(packet.Data.ToArray());
        }

        //Assert
        actual.Count.Should().Be(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            payloads[i].Should().Equal(expected[i].Data.ToArray());
            actual[i].Timestamp.Should().Be(expected[i].Timestamp);
        }
    }

    [Fact]
    public void The_authored_video_track_describes_itself_from_the_sequence_header()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-track", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        });

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(output));
        MediaTrackInfo video = reader.Tracks[0];
        Av1SequenceHeader header = Av1Bitstream.ParseCodecConfigurationRecord(video.CodecPrivate.Span);

        //Assert
        video.CodecId.Should().Be(VideoCodecIds.Av1);
        video.Width.Should().Be(96);
        video.Height.Should().Be(54);
        video.BitDepth.Should().Be(8);
        video.Layout.Should().Be(VideoPixelLayout.I420);
        header.MaxFrameWidth.Should().Be(96);
    }

    [Fact]
    public void The_authored_codec_private_matches_what_the_encoder_wrote_into_the_webm_file()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-av1c", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        });

        byte[] fromWebM;
        using (Containers.Matroska.MatroskaReader webm =
            new Containers.Matroska.MatroskaReader(new FileMediaSource(TestAssets.Path("av1-opus.webm"))))
        {
            fromWebM = webm.Tracks[0].CodecPrivate.ToArray();
        }

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(output));
        byte[] authored = reader.Tracks[0].CodecPrivate.ToArray();

        //Assert
        authored.Should().Equal(fromWebM);
    }

    [Fact]
    public void Caption_files_become_caption_tracks_in_the_header()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-captions", "authored.cbv");
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        };

        request.Captions.Add(new CbvCaptionInput(
            TestAssets.Path("captions-en.vtt"),
            "en",
            "English",
            CaptionTrackFlags.Default));

        request.Captions.Add(new CbvCaptionInput(
            TestAssets.Path("srt-captions.srt"),
            "fr",
            "Francais",
            CaptionTrackFlags.HearingImpaired));

        //Act
        CbvAuthoringResult result = CbvAuthoring.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(output));

        //Assert
        result.CaptionTrackCount.Should().Be(2);
        reader.CaptionTracks.Count.Should().Be(2);
        reader.CaptionTracks[0].Format.Should().Be(CaptionFormat.WebVtt);
        reader.CaptionTracks[0].Language.Should().Be("en");
        reader.CaptionTracks[0].IsDefault.Should().BeTrue();
        reader.CaptionTracks[0].CueCount.Should().Be(4);
        reader.CaptionTracks[1].Format.Should().Be(CaptionFormat.SubRip);
        reader.CaptionTracks[1].Language.Should().Be("fr");
        reader.CaptionTracks[1].IsHearingImpaired.Should().BeTrue();
        reader.CaptionTracks[1].CueCount.Should().Be(4);
    }

    [Fact]
    public void A_chapter_file_becomes_the_chapter_table()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-chapters", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            ChaptersPath = TestAssets.Path("chapters.ffmeta"),
        });

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(output));

        //Assert
        reader.Chapters.Count.Should().Be(3);
        reader.Chapters[0].Start.Should().Be(TimeSpan.Zero);
        reader.Chapters[0].Title.Length.Should().BeGreaterThan(0);
        reader.Chapters[0].TitleFor(new[] { "fr" }).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_two_streams_are_interleaved_in_presentation_order()
    {
        //Arrange
        string output = SyntheticMedia.ScratchPath("authoring-order", "authored.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = output,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        });

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(output));
        TimeSpan previous = TimeSpan.MinValue;
        bool ascending = true;
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.Timestamp < previous) ascending = false;
            previous = packet.Timestamp;
        }

        //Assert
        ascending.Should().BeTrue();
        (reader.Flags & CbvHeaderFlags.ChunksInPresentationOrder).Should().Be(CbvHeaderFlags.ChunksInPresentationOrder);
    }

    [Fact]
    public void A_request_with_no_inputs_is_refused()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("authoring-empty", "authored.cbv"),
        };

        //Act
        Action act = () => CbvAuthoring.Write(request);

        //Assert
        act.Should().Throw<ArgumentException>();
    }
}
