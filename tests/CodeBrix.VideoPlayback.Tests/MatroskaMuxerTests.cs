using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The managed WebM / Matroska muxer, measured against this library's own reader and profile checker, against
/// FFmpeg's output for the same elementary streams, and against FFmpeg itself as a reader.
/// </summary>
/// <remarks>
/// Every input is a committed golden asset: <c>av1-video-only.ivf</c>, <c>opus-audio.ogg</c>,
/// <c>vorbis-audio.ogg</c>, <c>captions-en.vtt</c>, <c>srt-captions.srt</c> and <c>chapters.ffmeta</c> (see
/// tests/assets/ASSETS.txt). The FFmpeg-muxed files made from the same inputs - <c>av1-opus.webm</c>,
/// <c>av1-vorbis.webm</c> and <c>av1-opus-captions-chapters.mkv</c> - are the byte-level references.
/// </remarks>
[Collection("Process-wide registries")]
public class MatroskaMuxerTests
{
    private const string FfprobePath = "/usr/bin/ffprobe";
    private const string FfmpegPath = "/usr/bin/ffmpeg";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void A_mode1_file_reads_back_with_every_track_name_language_and_codec_private()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("round-trip");

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        reader.DocType.Should().Be("webm");
        reader.DocTypeVersion.Should().Be(4);
        reader.DocTypeReadVersion.Should().Be(2);
        reader.Title.Should().Be("Synthetic clip");
        reader.MuxingApp.Should().Be("CodeBrix.VideoPlayback");
        reader.WritingApp.Should().Be("CodeBrix.VideoPlayback");
        reader.TimestampScale.Should().Be(1_000_000);
        reader.Duration.Should().Be(TimeSpan.FromSeconds(1));
        reader.Tracks.Count.Should().Be(4);

        MediaTrackInfo video = reader.Tracks[0];
        video.Kind.Should().Be(MediaTrackKind.Video);
        video.CodecId.Should().Be(VideoCodecIds.Av1);
        video.Name.Should().Be("picture");
        video.Language.Should().Be(string.Empty);
        video.Width.Should().Be(96);
        video.Height.Should().Be(54);
        video.BitDepth.Should().Be(8);
        video.Layout.Should().Be(VideoPixelLayout.I420);
        video.Color.Range.Should().Be(VideoColorRange.Limited);
        video.DefaultDuration.Should().Be(TimeSpan.FromTicks(833_333));
        video.CodecPrivate.ToArray().Should().Equal(ReferenceCodecPrivate("av1-opus.webm", MediaTrackKind.Video));

        MediaTrackInfo audio = reader.Tracks[1];
        audio.Kind.Should().Be(MediaTrackKind.Audio);
        audio.CodecId.Should().Be(VideoCodecIds.Opus);
        audio.Name.Should().Be("sound");
        audio.Language.Should().Be("en");
        audio.SampleRate.Should().Be(48000);
        audio.Channels.Should().Be(1);
        audio.PreSkipSamples.Should().Be(312);
        audio.CodecDelay.Should().Be(TimeSpan.FromTicks(65_000));
        audio.SeekPreRoll.Should().Be(TimeSpan.FromMilliseconds(80));
        audio.CodecPrivate.ToArray().Should().Equal(ReferenceCodecPrivate("av1-opus.webm", MediaTrackKind.Audio));

        reader.CaptionTracks.Count.Should().Be(2);
        reader.CaptionTracks[0].Name.Should().Be("English");
        reader.CaptionTracks[0].Language.Should().Be("en");
        reader.CaptionTracks[0].IsDefault.Should().BeTrue();
        reader.CaptionTracks[0].IsForced.Should().BeFalse();
        reader.CaptionTracks[1].Name.Should().Be("Francais");
        reader.CaptionTracks[1].Language.Should().Be("fr");
        reader.CaptionTracks[1].IsDefault.Should().BeFalse();
        reader.CaptionTracks[1].IsForced.Should().BeTrue();
    }

    [Fact]
    public void Video_packets_are_byte_identical_to_the_ones_ffmpeg_muxes_from_the_same_stream()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("video-bytes", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        };

        //Act
        MatroskaMuxer.Write(request);
        List<MediaPacket> ours = ReadPackets(request.OutputPath, MediaTrackKind.Video, out _);
        List<MediaPacket> ffmpeg = ReadPackets(TestAssets.Path("av1-opus.webm"), MediaTrackKind.Video, out _);

        //Assert - FFmpeg drops the temporal delimiters, as the AV1 mapping asks, and so does this muxer.
        ours.Count.Should().Be(12);
        ours.Count.Should().Be(ffmpeg.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            ours[i].Data.ToArray().Should().Equal(ffmpeg[i].Data.ToArray());
            ours[i].Timestamp.Should().Be(ffmpeg[i].Timestamp);
            ours[i].IsKeyFrame.Should().Be(ffmpeg[i].IsKeyFrame);
        }

        KeyFrameIndexes(ours).Should().Equal(0, 4, 8);
    }

    [Fact]
    public void Opus_packets_and_timestamps_come_through_exactly_and_the_last_block_carries_the_trim()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("opus-bytes", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        };

        List<OggAudioPacket> source;
        int trailingTrim;
        using (OggAudioStream ogg = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg")))
        {
            source = new List<OggAudioPacket>(ogg.ReadAllPackets());
            trailingTrim = ogg.TrailingTrimSamples;
        }

        //Act
        MatroskaMuxer.Write(request);
        List<MediaPacket> ours = ReadPackets(request.OutputPath, MediaTrackKind.Audio, out _);

        //Assert - Matroska block timestamps keep the pre-skip in, as an Ogg granule does; CodecDelay states it.
        ours.Count.Should().Be(source.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            ours[i].Data.ToArray().Should().Equal(source[i].Data.ToArray());
            ours[i].Timestamp.Should().Be(TimeSpan.FromMilliseconds(Math.Round(source[i].Timestamp.TotalMilliseconds)));
            ours[i].IsKeyFrame.Should().BeTrue();
        }

        trailingTrim.Should().BeGreaterThan(0);
        ours[ours.Count - 1].DiscardPadding.Should().Be(TimeSpan.FromTicks(trailingTrim * TimeSpan.TicksPerSecond / 48000));
        ours[0].Timestamp.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_vorbis_track_carries_its_three_setup_headers_xiph_laced()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("vorbis", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("vorbis-audio.ogg"),
            EvaluateProfile = true,
        };

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        MediaTrackInfo audio = reader.Tracks[1];
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);
        audio.CodecDelay.Should().Be(TimeSpan.Zero);
        audio.SeekPreRoll.Should().Be(TimeSpan.Zero);
        using (OggAudioStream ogg = OggAudioStream.Open(TestAssets.Path("vorbis-audio.ogg")))
        {
            audio.CodecPrivate.ToArray().Should().Equal(ogg.CodecPrivate.ToArray());
        }

        // The same shape FFmpeg writes: lead byte 2, then the identification header first. (The comment header
        // itself differs from av1-vorbis.webm's, because that file's sound was encoded in a separate run.)
        OggAudioStream.SplitXiphCodecPrivate(audio.CodecPrivate.Span, out byte[] identification, out _, out byte[] setup);
        byte[] reference = ReferenceCodecPrivate("av1-vorbis.webm", MediaTrackKind.Audio);
        OggAudioStream.SplitXiphCodecPrivate(reference, out byte[] referenceIdentification, out _, out _);
        audio.CodecPrivate.Span[0].Should().Be(2);
        identification.Should().Equal(referenceIdentification);
        setup[0].Should().Be(5);
        result.FindTrack(MediaTrackKind.Audio).CodecId.Should().Be("A_VORBIS");
        result.PassesProfile.Should().BeTrue();
    }

    [Fact]
    public void Caption_blocks_are_laid_out_byte_for_byte_as_ffmpeg_lays_them_out()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("caption-bytes", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        };
        request.Captions.Add(new MatroskaCaptionInput(TestAssets.Path("captions-en.vtt"), "en", "English", CaptionTrackFlags.Default));

        //Act
        MatroskaMuxer.Write(request);
        List<MediaPacket> ours = ReadPackets(request.OutputPath, MediaTrackKind.Caption, out _);
        List<MediaPacket> ffmpeg = ReadPackets(TestAssets.Path("av1-opus-captions-chapters.mkv"), MediaTrackKind.Caption, out _);

        //Assert - identifier LF settings LF text, both lines always present.
        ours.Count.Should().Be(4);
        ours.Count.Should().Be(ffmpeg.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            ours[i].Data.ToArray().Should().Equal(ffmpeg[i].Data.ToArray());
            ours[i].Timestamp.Should().Be(ffmpeg[i].Timestamp);
            ours[i].Duration.Should().Be(ffmpeg[i].Duration);
        }

        Encoding.UTF8.GetString(ours[1].Data.Span).Should().StartWith("\n\n");
    }

    [Fact]
    public void Caption_cues_read_back_with_their_text_timing_identifiers_and_settings()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("caption-cues");
        CaptionTrack english = CaptionFiles.ReadFile(TestAssets.Path("captions-en.vtt"), 0, "en");
        CaptionTrack french = CaptionFiles.ReadFile(TestAssets.Path("srt-captions.srt"), 0, "fr");

        //Act
        MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(request.OutputPath);
        while (reader.TryReadPacket(out _))
        {
        }

        //Assert
        CaptionTrack readEnglish = reader.CaptionTracks[0];
        readEnglish.AreCuesComplete.Should().BeTrue();
        readEnglish.CueCount.Should().Be(english.CueCount);
        for (int i = 0; i < english.CueCount; i++)
        {
            readEnglish.Cues[i].Start.Should().Be(english.Cues[i].Start);
            readEnglish.Cues[i].End.Should().Be(english.Cues[i].End);
            readEnglish.Cues[i].Text.Should().Be(english.Cues[i].Text);
            readEnglish.Cues[i].Identifier.Should().Be(english.Cues[i].Identifier);
            readEnglish.Cues[i].Settings.Should().Be(english.Cues[i].Settings);
        }

        CaptionTrack readFrench = reader.CaptionTracks[1];
        readFrench.CueCount.Should().Be(french.CueCount);
        for (int i = 0; i < french.CueCount; i++)
        {
            readFrench.Cues[i].Start.Should().Be(french.Cues[i].Start);
            readFrench.Cues[i].End.Should().Be(french.Cues[i].End);
            readFrench.Cues[i].Text.Should().Be(french.Cues[i].Text);
            readFrench.Cues[i].Identifier.Should().Be(string.Empty);
            readFrench.Cues[i].Settings.Should().Be(string.Empty);
        }
    }

    [Fact]
    public void Chapters_read_back_with_their_times_and_every_titles_language()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("chapters");
        IReadOnlyList<Chapter> expected = FfMetadataChapters.ReadFile(TestAssets.Path("chapters.ffmeta"));

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        result.ChapterCount.Should().Be(3);
        reader.Chapters.Count.Should().Be(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            reader.Chapters[i].Start.Should().Be(expected[i].Start);
            reader.Chapters[i].End.Should().Be(expected[i].End);
            reader.Chapters[i].Title.Should().Be(expected[i].Title);
        }

        reader.Chapters[0].Titles["fr"].Should().Be("Mesures d'ouverture");
        reader.Chapters[0].TitleFor(new[] { "fr" }).Should().Be("Mesures d'ouverture");
    }

    [Fact]
    public void Without_a_title_of_its_own_the_file_takes_the_chapter_files_title()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("title-fallback");
        request.Title = null;

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        reader.Title.Should().Be("CodeBrix VideoPlayback synthetic clip");
    }

    [Fact]
    public void A_title_of_its_own_wins_over_the_chapter_files_title_and_an_empty_one_writes_none()
    {
        //Arrange
        MatroskaAuthoringRequest named = FullRequest("title-named");
        MatroskaAuthoringRequest empty = FullRequest("title-empty");
        empty.Title = string.Empty;
        MatroskaAuthoringRequest noChapters = FullRequest("title-no-chapters");
        noChapters.Title = null;
        noChapters.ChaptersPath = null;

        //Act
        MatroskaMuxer.Write(named);
        MatroskaMuxer.Write(empty);
        MatroskaMuxer.Write(noChapters);
        using MatroskaReader namedReader = Open(named.OutputPath);
        using MatroskaReader emptyReader = Open(empty.OutputPath);
        using MatroskaReader noChaptersReader = Open(noChapters.OutputPath);

        //Assert
        namedReader.Title.Should().Be("Synthetic clip");
        emptyReader.Title.Should().Be(string.Empty);
        noChaptersReader.Title.Should().Be(string.Empty);
    }

    [Fact]
    public void Packet_audio_produces_the_same_bytes_as_the_ogg_file_it_came_from()
    {
        //Arrange
        MatroskaAuthoringRequest fromOgg = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("packets-ogg", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
            AudioLanguage = "en",
        };

        MatroskaPacketAudioInput packets;
        using (OggAudioStream ogg = OggAudioStream.Open(TestAssets.Path("opus-audio.ogg")))
        {
            packets = new MatroskaPacketAudioInput(VideoCodecIds.Opus, ogg.CodecPrivate, 48000, ogg.Channels);
            foreach (OggAudioPacket packet in ogg.ReadAllPackets())
            {
                packets.Packets.Add(new MatroskaAudioPacket(packet.Data.ToArray(), packet.Timestamp, packet.Duration));
            }

            packets.TrailingTrimSamples = ogg.TrailingTrimSamples;
        }

        MatroskaAuthoringRequest fromPackets = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("packets-list", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            PacketAudio = packets,
            AudioLanguage = "en",
        };

        //Act
        MatroskaMuxer.Write(fromOgg);
        MatroskaMuxer.Write(fromPackets);

        //Assert
        File.ReadAllBytes(fromPackets.OutputPath).Should().Equal(File.ReadAllBytes(fromOgg.OutputPath));
    }

    [Fact]
    public void The_same_inputs_always_produce_the_same_bytes()
    {
        //Arrange
        MatroskaAuthoringRequest first = FullRequest("deterministic-a");
        MatroskaAuthoringRequest second = FullRequest("deterministic-b");

        //Act
        MatroskaMuxer.Write(first);
        MatroskaMuxer.Write(second);

        //Assert
        File.ReadAllBytes(second.OutputPath).Should().Equal(File.ReadAllBytes(first.OutputPath));
        File.Exists(first.OutputPath + ".pass1.tmp").Should().BeFalse();
    }

    // ------------------------------------------------------------------ the profile and the layout

    [Fact]
    public void A_mode1_file_passes_the_streamable_profile()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("profile");
        request.EvaluateProfile = true;

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        StreamableProfileReport report = StreamableProfile.EvaluateFile(result.Path);

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.ProfileReport.Warnings.Should().Be(0);
        result.CuesPrecedeClusters.Should().BeTrue();
        report.Passes.Should().BeTrue();
        report.Verdict.Should().Be("passes the profile");
    }

    [Fact]
    public void With_the_cues_at_the_end_the_file_fails_exactly_the_cues_rule()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("cues-at-end");
        request.CuesToFront = false;
        request.EvaluateProfile = true;

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        result.PassesProfile.Should().BeFalse();
        List<string> failures = new List<string>();
        foreach (StreamableProfileRule rule in result.ProfileReport.FailedRules()) failures.Add(rule.Rule);
        failures.Should().Equal("cues sit before the first cluster");

        reader.HasIndex.Should().BeTrue();
        reader.CuesPrecedeFirstCluster.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_seek_head_and_cue_position_lands_exactly_on_the_element_it_names(bool cuesToFront)
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest(cuesToFront ? "positions-front" : "positions-end");
        request.CuesToFront = cuesToFront;

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);
        List<KeyValuePair<uint, long>> seeks = ReadSeekHead(result.Path, reader.SegmentDataOffset);

        //Assert - the seek head names Info, Tracks, Chapters and Cues, and each position holds that element.
        List<uint> named = new List<uint>();
        foreach (KeyValuePair<uint, long> seek in seeks)
        {
            named.Add(seek.Key);
            ElementAt(result.Path, reader.SegmentDataOffset + seek.Value).Id.Should().Be(seek.Key);
        }

        named.Should().Equal(MatroskaIds.Info, MatroskaIds.Tracks, MatroskaIds.Chapters, MatroskaIds.Cues);

        // Every cue points at a cluster, and inside it at a block of the cued track with the cued timestamp.
        reader.Cues.Count.Should().BeGreaterThan(0);
        foreach (MatroskaCuePoint cue in reader.Cues)
        {
            EbmlElementHeader cluster = ElementAt(result.Path, cue.ClusterOffset);
            cluster.Id.Should().Be(MatroskaIds.Cluster);

            BlockAt(result.Path, cluster, cue.RelativeOffset, out int track, out long timestampMs);
            track.Should().Be(cue.TrackId);
            timestampMs.Should().Be((long)Math.Round(cue.Time.TotalMilliseconds));
        }

        result.CuePointCount.Should().Be(6);
    }

    [Fact]
    public void Every_video_key_frame_starts_a_cluster_and_has_a_cue()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("clusters");

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert - three key frames (0, 1/3 and 2/3 s), three clusters, three video cues at the clusters' heads.
        result.ClusterCount.Should().Be(3);

        List<MatroskaCuePoint> videoCues = new List<MatroskaCuePoint>();
        foreach (MatroskaCuePoint cue in reader.Cues)
        {
            if (cue.TrackId == 1) videoCues.Add(cue);
        }

        videoCues.Count.Should().Be(3);
        videoCues[0].Time.Should().Be(TimeSpan.Zero);
        videoCues[1].Time.Should().Be(TimeSpan.FromMilliseconds(333));
        videoCues[2].Time.Should().Be(TimeSpan.FromMilliseconds(667));

        // The cluster timestamp element (E7, size, value) is the only thing in front of the key frame.
        foreach (MatroskaCuePoint cue in videoCues) cue.RelativeOffset.Should().BeLessThan(5);
    }

    [Fact]
    public void Seeking_through_the_index_lands_on_each_key_frame()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("seek");
        MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(request.OutputPath);

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromMilliseconds(500), 1);
        reader.TryReadPacket(out MediaPacket first);

        //Assert
        landed.Should().Be(TimeSpan.FromMilliseconds(333));
        first.TrackId.Should().Be(1);
        first.IsKeyFrame.Should().BeTrue();
        first.Timestamp.Should().Be(TimeSpan.FromMilliseconds(333));
    }

    [Fact]
    public void A_matroska_document_keeps_a_language_tag_a_three_letter_code_cannot_hold()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("matroska-doctype");
        request.DocType = MatroskaDocType.Matroska;

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        reader.DocType.Should().Be("matroska");
        reader.CaptionTracks[1].Language.Should().Be("fr-CA");
        result.Notices.Count.Should().Be(1);
        result.Notices[0].Should().Contain("fr-CA");
    }

    [Fact]
    public void A_webm_document_names_what_it_had_to_shorten()
    {
        //Arrange
        MatroskaAuthoringRequest request = FullRequest("webm-notice");

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        reader.CaptionTracks[1].Language.Should().Be("fr");
        result.Notices.Count.Should().Be(1);
        result.Notices[0].Should().Contain("'fr-CA' is written as 'fre'");
    }

    [Fact]
    public void A_hearing_impaired_track_is_written_as_webm_captions_and_flagged()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("hearing-impaired", "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        };
        request.Captions.Add(new MatroskaCaptionInput(
            TestAssets.Path("captions-en.vtt"),
            "en",
            "English SDH",
            CaptionTrackFlags.HearingImpaired));

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        result.FindTrack(MediaTrackKind.Caption).CodecId.Should().Be("D_WEBVTT/CAPTIONS");
        reader.CaptionTracks[0].IsHearingImpaired.Should().BeTrue();
        reader.CaptionTracks[0].IsDefault.Should().BeFalse();
    }

    [Fact]
    public void A_file_without_video_indexes_its_audio()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("audio-only", "clip.webm"),
            AudioOggPath = TestAssets.Path("vorbis-audio.ogg"),
        };

        //Act
        MatroskaAuthoringResult result = MatroskaMuxer.Write(request);
        using MatroskaReader reader = Open(result.Path);

        //Assert
        result.ClusterCount.Should().Be(1);
        reader.Cues.Count.Should().Be(1);
        reader.Cues[0].TrackId.Should().Be(1);
        reader.CuesPrecedeFirstCluster.Should().BeTrue();
        reader.Duration.Should().Be(TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void An_ivf_that_is_not_av1_is_refused_and_nothing_is_left_behind()
    {
        //Arrange
        string directory = TestAssets.CreateTemporaryDirectory("muxer-not-av1");
        string ivf = Path.Combine(directory, "vp8.ivf");
        byte[] bytes = File.ReadAllBytes(TestAssets.Path("av1-video-only.ivf"));
        Encoding.ASCII.GetBytes("VP80").CopyTo(bytes, 8);
        File.WriteAllBytes(ivf, bytes);

        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Path.Combine(directory, "clip.webm"),
            VideoIvfPath = ivf,
        };

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*carrying 'VP80'*AV1*");
        File.Exists(request.OutputPath).Should().BeFalse();
        File.Exists(request.OutputPath + ".pass1.tmp").Should().BeFalse();
    }

    [Fact]
    public void A_caption_that_starts_after_the_media_ends_is_refused()
    {
        //Arrange
        string directory = TestAssets.CreateTemporaryDirectory("muxer-late-caption");
        string vtt = Path.Combine(directory, "late.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00:00.000 --> 00:00:00.500\nIn time.\n\n00:00:05.000 --> 00:00:06.000\nToo late.\n");

        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Path.Combine(directory, "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        };
        request.Captions.Add(new MatroskaCaptionInput(vtt, "en"));

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*late.vtt' has a cue starting at 00:00:05*after the end of the media*");
        File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void Overlapping_chapters_are_refused()
    {
        //Arrange
        string directory = TestAssets.CreateTemporaryDirectory("muxer-overlap");
        string ffmeta = Path.Combine(directory, "overlap.ffmeta");
        File.WriteAllText(
            ffmeta,
            ";FFMETADATA1\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=600\ntitle=One\n\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=500\nEND=1000\ntitle=Two\n");

        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Path.Combine(directory, "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            ChaptersPath = ffmeta,
        };

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*Chapter 1*ends at 00:00:00.6*after chapter 2 starts*must not overlap*");
        File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void A_chapter_that_starts_after_the_media_ends_is_refused()
    {
        //Arrange
        string directory = TestAssets.CreateTemporaryDirectory("muxer-late-chapter");
        string ffmeta = Path.Combine(directory, "late.ffmeta");
        File.WriteAllText(ffmeta, ";FFMETADATA1\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=500\ntitle=One\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=3000\ntitle=Two\n");

        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Path.Combine(directory, "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            ChaptersPath = ffmeta,
        };

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*Chapter 2*starts at 00:00:02*after the end of the media*");
    }

    [Fact]
    public void A_request_with_audio_twice_or_no_media_at_all_is_refused()
    {
        //Arrange
        MatroskaAuthoringRequest twice = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("twice", "clip.webm"),
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
            PacketAudio = new MatroskaPacketAudioInput(VideoCodecIds.Opus, new byte[19], 48000, 1),
        };

        MatroskaAuthoringRequest empty = new MatroskaAuthoringRequest { OutputPath = Scratch("empty", "clip.webm") };

        //Act
        Action audioTwice = () => MatroskaMuxer.Write(twice);
        Action nothing = () => MatroskaMuxer.Write(empty);
        Action noRequest = () => MatroskaMuxer.Write(null);

        //Assert
        audioTwice.Should().Throw<ArgumentException>().WithMessage("*both as an Ogg file and as packets*");
        nothing.Should().Throw<ArgumentException>().WithMessage("*video, audio, or both*");
        noRequest.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_missing_input_file_is_named()
    {
        //Arrange
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("missing", "clip.webm"),
            VideoIvfPath = Path.Combine(TestAssets.Root, "no-such-file.ivf"),
        };

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<FileNotFoundException>().WithMessage("*no video file at*no-such-file.ivf*");
    }

    [Fact]
    public void Packet_audio_in_a_codec_the_profile_does_not_allow_is_refused()
    {
        //Arrange
        MatroskaPacketAudioInput flac = new MatroskaPacketAudioInput(VideoCodecIds.Flac, new byte[42], 48000, 2);
        flac.Packets.Add(new MatroskaAudioPacket(new byte[10], TimeSpan.Zero, TimeSpan.FromMilliseconds(20)));

        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch("flac", "clip.webm"),
            PacketAudio = flac,
        };

        //Act
        Action act = () => MatroskaMuxer.Write(request);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*'flac'*Opus or Vorbis*");
    }

    // ------------------------------------------------------------------ other readers and players

    [Fact]
    public void Ffprobe_reads_the_tracks_and_chapters_and_ffmpeg_decodes_the_media_without_complaint()
    {
        //Arrange
        Assert.SkipUnless(
            File.Exists(FfprobePath) && File.Exists(FfmpegPath),
            $"FFmpeg is not installed at '{FfmpegPath}' and '{FfprobePath}'; it is the independent reader this test asks.");

        MatroskaAuthoringRequest request = FullRequest("ffprobe");
        MatroskaMuxer.Write(request);

        //Act
        string json = Run(FfprobePath, "-v", "error", "-print_format", "json", "-show_streams", "-show_chapters", "-show_format", request.OutputPath);
        Run(FfmpegPath, "-v", "error", "-nostdin", "-i", request.OutputPath, "-map", "0:v", "-map", "0:a", "-f", "null", "-");

        //Assert
        using JsonDocument probe = JsonDocument.Parse(json);
        JsonElement streams = probe.RootElement.GetProperty("streams");
        streams.GetArrayLength().Should().Be(4);
        streams[0].GetProperty("codec_name").GetString().Should().Be("av1");
        streams[0].GetProperty("width").GetInt32().Should().Be(96);
        streams[1].GetProperty("codec_name").GetString().Should().Be("opus");
        streams[1].GetProperty("tags").GetProperty("language").GetString().Should().Be("eng");
        streams[2].GetProperty("codec_name").GetString().Should().Be("webvtt");
        streams[2].GetProperty("tags").GetProperty("title").GetString().Should().Be("English");
        streams[2].GetProperty("disposition").GetProperty("default").GetInt32().Should().Be(1);
        streams[3].GetProperty("codec_name").GetString().Should().Be("webvtt");
        streams[3].GetProperty("disposition").GetProperty("forced").GetInt32().Should().Be(1);
        JsonElement chapters = probe.RootElement.GetProperty("chapters");
        chapters.GetArrayLength().Should().Be(3);
        chapters[0].GetProperty("tags").GetProperty("title").GetString().Should().Be("Opening bars");
        chapters[2].GetProperty("end_time").GetString().Should().Be("1.000000");
        probe.RootElement.GetProperty("format").GetProperty("format_name").GetString().Should().Be("matroska,webm");
    }

    [Fact]
    public void A_session_opens_the_file_and_plays_it_to_the_end()
    {
        //Arrange - no AV1 decoder package is referenced here, so a stand-in decoder turns each packet into a
        // grey frame; what is being measured is the container feeding the session, not the picture.
        MatroskaAuthoringRequest request = FullRequest("session");
        MatroskaMuxer.Write(request);

        PlaceholderAv1DecoderFactory factory = new PlaceholderAv1DecoderFactory();
        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        session.RegisterDecoderFactory(factory);
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(request.OutputPath);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(1));
        session.VideoTrack.CodecId.Should().Be(VideoCodecIds.Av1);
        session.Tracks.Count.Should().Be(4);
        session.Tracks[1].CodecId.Should().Be(VideoCodecIds.Opus);
        session.Chapters.Count.Should().Be(3);
        session.CaptionTracks.Count.Should().Be(2);
        factory.PacketsDecoded.Should().Be(12);
    }

    [Fact]
    public void A_session_plays_the_opus_sound_of_a_muxed_file_to_the_end()
    {
        //Arrange - opens the audio device, so it is opt-in like every other audible test.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS") == "1",
            "Set CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1 to run the tests that open the audio device.");
        CodeBrixAudioOpus.Register();

        MatroskaAuthoringRequest request = FullRequest("session-audio");
        MatroskaMuxer.Write(request);

        using VideoPlaybackSession session = new VideoPlaybackSession();
        session.RegisterDecoderFactory(new PlaceholderAv1DecoderFactory());
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(request.OutputPath);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        finished.Should().BeTrue();
        session.AudioTrack.PreSkipSamples.Should().Be(312);
        ended.Should().Be(1);
    }

    // ------------------------------------------------------------------ helpers

    private static MatroskaAuthoringRequest FullRequest(string label)
    {
        MatroskaAuthoringRequest request = new MatroskaAuthoringRequest
        {
            OutputPath = Scratch(label, "clip.webm"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            VideoName = "picture",
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
            AudioName = "sound",
            AudioLanguage = "en",
            ChaptersPath = TestAssets.Path("chapters.ffmeta"),
            Title = "Synthetic clip",
        };

        request.Captions.Add(new MatroskaCaptionInput(TestAssets.Path("captions-en.vtt"), "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new MatroskaCaptionInput(TestAssets.Path("srt-captions.srt"), "fr-CA", "Francais", CaptionTrackFlags.Forced));
        return request;
    }

    private static string Scratch(string label, string fileName) =>
        Path.Combine(TestAssets.CreateTemporaryDirectory("muxer-" + label), fileName);

    private static MatroskaReader Open(string path) => new MatroskaReader(new FileMediaSource(path));

    private static byte[] ReferenceCodecPrivate(string asset, MediaTrackKind kind)
    {
        using MatroskaReader reader = Open(TestAssets.Path(asset));
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == kind) return track.CodecPrivate.ToArray();
        }

        throw new InvalidOperationException($"'{asset}' has no {kind} track.");
    }

    private static List<MediaPacket> ReadPackets(string path, MediaTrackKind kind, out int trackId)
    {
        using MatroskaReader reader = Open(path);
        trackId = -1;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind != kind) continue;
            trackId = track.Id;
            break;
        }

        List<MediaPacket> packets = new List<MediaPacket>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != trackId) continue;

            // The reader's buffer is reused, so each packet's bytes are copied before the next read.
            packets.Add(new MediaPacket(
                packet.TrackId,
                packet.Data.ToArray(),
                packet.Timestamp,
                packet.Duration,
                packet.IsKeyFrame,
                packet.DiscardPadding));
        }

        return packets;
    }

    private static List<int> KeyFrameIndexes(List<MediaPacket> packets)
    {
        List<int> indexes = new List<int>();
        for (int i = 0; i < packets.Count; i++)
        {
            if (packets[i].IsKeyFrame) indexes.Add(i);
        }

        return indexes;
    }

    private static EbmlElementHeader ElementAt(string path, long offset)
    {
        using EbmlReader ebml = new EbmlReader(new FileMediaSource(path));
        ebml.Position = offset;
        ebml.TryReadElementHeader(ebml.SourceEnd, out EbmlElementHeader header).Should().BeTrue();
        return header;
    }

    private static List<KeyValuePair<uint, long>> ReadSeekHead(string path, long segmentDataOffset)
    {
        List<KeyValuePair<uint, long>> seeks = new List<KeyValuePair<uint, long>>();

        using EbmlReader ebml = new EbmlReader(new FileMediaSource(path));
        ebml.Position = segmentDataOffset;
        ebml.TryReadElementHeader(ebml.SourceEnd, out EbmlElementHeader seekHead).Should().BeTrue();
        seekHead.Id.Should().Be(MatroskaIds.SeekHead);

        while (ebml.TryReadElementHeader(seekHead.EndOffset, out EbmlElementHeader seek))
        {
            uint id = 0;
            long position = -1;
            while (ebml.TryReadElementHeader(seek.EndOffset, out EbmlElementHeader field))
            {
                if (field.Id == MatroskaIds.SeekId)
                {
                    foreach (byte b in ebml.ReadBinary(field)) id = (id << 8) | b;
                }
                else if (field.Id == MatroskaIds.SeekPosition)
                {
                    position = (long)ebml.ReadUnsignedInteger(field);
                }
                else
                {
                    ebml.SkipElement(field);
                }
            }

            seeks.Add(new KeyValuePair<uint, long>(id, position));
        }

        return seeks;
    }

    private static void BlockAt(string path, EbmlElementHeader cluster, long relative, out int track, out long timestampMs)
    {
        using EbmlReader ebml = new EbmlReader(new FileMediaSource(path));

        long clusterTimestamp = 0;
        ebml.Position = cluster.DataOffset;
        ebml.TryReadElementHeader(cluster.EndOffset, out EbmlElementHeader first).Should().BeTrue();
        first.Id.Should().Be(MatroskaIds.ClusterTimestamp);
        clusterTimestamp = (long)ebml.ReadUnsignedInteger(first);

        ebml.Position = cluster.DataOffset + relative;
        ebml.TryReadElementHeader(cluster.EndOffset, out EbmlElementHeader block).Should().BeTrue();

        if (block.Id == MatroskaIds.BlockGroup)
        {
            ebml.TryReadElementHeader(block.EndOffset, out block).Should().BeTrue();
            block.Id.Should().Be(MatroskaIds.Block);
        }
        else
        {
            block.Id.Should().Be(MatroskaIds.SimpleBlock);
        }

        byte[] bytes = ebml.ReadBinary(block);
        EbmlReader.TryReadVint(bytes, out ulong number, out int length).Should().BeTrue();
        track = (int)number;
        timestampMs = clusterTimestamp + (short)((bytes[length] << 8) | bytes[length + 1]);
    }

    private static string Run(string executable, params string[] arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string diagnostics = errors.GetAwaiter().GetResult();

        process.ExitCode.Should().Be(0, executable + " said: " + diagnostics);
        diagnostics.Trim().Should().BeEmpty();
        return output;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>
    /// Stands in for an AV1 decoder: reads the picture size out of the configuration record and answers every
    /// packet with one grey frame at the packet's timestamp, through the library's own uncompressed decoder.
    /// </summary>
    private sealed class PlaceholderAv1DecoderFactory : IVideoDecoderFactory
    {
        private int packets;

        public int PacketsDecoded => Volatile.Read(ref packets);

        public string FactoryId => "CodeBrix.VideoPlayback.Tests.PlaceholderAv1";

        public IReadOnlyCollection<string> SupportedCodecIds { get; } = new[] { VideoCodecIds.Av1 };

        public int Priority => 100;

        public IVideoDecoder CreateDecoder(string codecId, ReadOnlyMemory<byte> codecPrivate, VideoDecoderOptions options)
        {
            Av1SequenceHeader header = Av1Bitstream.ParseCodecConfigurationRecord(codecPrivate.Span);
            RawVideoDescriptor descriptor = new RawVideoDescriptor(
                header.MaxFrameWidth,
                header.MaxFrameHeight,
                8,
                VideoPixelLayout.I420,
                header.Color);

            return new PlaceholderAv1Decoder(new RawVideoDecoder(descriptor, options.BufferPool), this);
        }

        internal void Count() => Interlocked.Increment(ref packets);
    }

    private sealed class PlaceholderAv1Decoder : IVideoDecoder
    {
        private readonly RawVideoDecoder inner;
        private readonly PlaceholderAv1DecoderFactory factory;
        private readonly byte[] grey;

        internal PlaceholderAv1Decoder(RawVideoDecoder inner, PlaceholderAv1DecoderFactory factory)
        {
            this.inner = inner;
            this.factory = factory;
            grey = new byte[inner.ExpectedPacketBytes];
            Array.Fill(grey, (byte)128);
        }

        public VideoStreamInfo Info => inner.Info;

        public bool SupportsExternalBuffers => inner.SupportsExternalBuffers;

        public string CodecId => VideoCodecIds.Av1;

        public bool SendPacket(VideoPacket packet)
        {
            factory.Count();
            return inner.SendPacket(new VideoPacket(grey, packet.Timestamp, packet.IsKeyFrame));
        }

        public bool TryReceiveFrame(out VideoFrame frame) => inner.TryReceiveFrame(out frame);

        public void Flush() => inner.Flush();

        public void Drain() => inner.Drain();

        public void Dispose() => inner.Dispose();
    }
}
