using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;
using Chapter = CodeBrix.VideoPlayback.Chapters.Chapter;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The WebM-profile flavour (Mode1) on its in-process path: the picture encoded by the AV1 encoder package, the
/// sound by the Opus encoder package, and the file written by the core's managed WebM muxer - either because the
/// request asked for <see cref="AuthoringVideoEncoder.Av1Package" /> or as the fallback when the ffmpeg path is
/// not available. What it lists, what it writes, and what that plays as.
/// </summary>
/// <remarks>
/// Nothing here registers the Opus package process-wide: other tests assert that it is NOT registered. The
/// Opus track is decoded with a packet decoder made directly from the package's factory instead.
/// </remarks>
public class WebMInProcessAuthoringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    #region Dry run and the choice of path

    [Fact]
    public void A_webm_dry_run_with_the_package_encoder_lists_video_audio_and_mux_passes()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(3);
        commands[0].Label.Should().Be("video pass");
        commands[0].RunsFFmpeg.Should().BeFalse();
        commands[0].Arguments.Should().Be(
            "av1 i420 8-bit size decoded-cropped-to-even good-quality cq-level 30 cpu-used 6 keyint 48 "
            + "<- /nowhere/source.mov -> " + Path.Combine("/nowhere/work", "clip.video.ivf"));
        commands[1].Label.Should().Be("audio pass");
        commands[1].RunsFFmpeg.Should().BeFalse();
        commands[1].Arguments.Should().Be(
            "opus 128 kbit/s 48000 Hz 2 channel(s) vbr complexity 10 <- /nowhere/source.mov -> packets");
        commands[2].Label.Should().Be("mux pass");
        commands[2].RunsFFmpeg.Should().BeFalse();
        commands[2].Arguments.Should().Be(
            "mux webm cues-to-front 0 caption track(s) no chapters <- "
            + Path.Combine("/nowhere/work", "clip.video.ivf") + " + packets -> /nowhere/clip.webm");
        commands[2].ToString().Should().StartWith("in process: mux webm");
    }

    [Fact]
    public void The_mux_line_names_the_document_the_index_the_captions_and_the_chapters()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Container = AuthoringContainerFormat.Matroska;
        request.CuesToFront = false;
        request.ChaptersPath = "/nowhere/chapters.ffmeta";
        request.Captions.Add(new AuthoringCaptionInput("/nowhere/en.vtt", "en"));
        request.Audio.Include = false;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
        commands[1].Arguments.Should().Be(
            "mux matroska cues-at-end 1 caption track(s) chapters /nowhere/chapters.ffmeta <- "
            + Path.Combine("/nowhere/work", "clip.video.ivf") + " -> /nowhere/clip.webm");
    }

    [Fact]
    public void A_matroska_document_with_vorbis_lists_the_vorbis_pass_and_its_ogg_file()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Container = AuthoringContainerFormat.Matroska;
        request.Audio.Codec = AuthoringAudioCodec.LibVorbis;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        string ogg = Path.Combine("/nowhere/work", "clip.audio.ogg");
        commands[1].Arguments.Should().Be("vorbis 128 kbit/s 48000 Hz 2 channel(s) <- /nowhere/source.mov -> " + ogg);
        commands[2].Arguments.Should().EndWith(" + " + ogg + " -> /nowhere/clip.webm");
        commands[2].Arguments.Should().StartWith("mux matroska");
    }

    [Fact]
    public void A_frame_size_adds_the_prepare_pass_in_front()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(4);
        commands[0].Label.Should().Be("prepare pass");
        commands[0].RunsFFmpeg.Should().BeTrue();
        commands[0].Arguments.Should().Contain("-pix_fmt yuv420p");
        commands[1].Arguments.Should().Contain("size 640x360");
        commands[1].Arguments.Should().Contain("clip.prepared.mkv");
        commands[3].Label.Should().Be("mux pass");
    }

    [Fact]
    public void An_ffmpeg_encoder_keeps_the_one_ffmpeg_pass_when_the_ffmpeg_path_is_there()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = true;
        try
        {

            //Act
            IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

            //Assert
            commands.Count.Should().Be(1);
            commands[0].Label.Should().Be("one pass");
            commands[0].RunsFFmpeg.Should().BeTrue();
            commands[0].Arguments.Should().Contain("-c:v libsvtav1");
            commands[0].Arguments.Should().Contain("-c:a libopus");
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    [Fact]
    public void An_ffmpeg_encoder_falls_back_to_the_in_process_path_when_the_ffmpeg_path_is_not_there()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Encoder = AuthoringVideoEncoder.LibAomAv1;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = false;
        try
        {

            //Act
            bool ffmpeg = InProcessAuthoring.UsesFFmpegPath(request, true, out string reason);
            IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

            //Assert
            ffmpeg.Should().BeFalse();
            reason.Should().Contain("asked for libaom-av1 through ffmpeg");
            reason.Should().Contain("Opus by the in-process Opus encoder");
            reason.Should().Contain("muxed by the managed WebM muxer");
            commands.Count.Should().Be(3);
            commands[0].RunsFFmpeg.Should().BeFalse();
            commands[2].Label.Should().Be("mux pass");
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    [Fact]
    public void The_package_encoder_takes_the_in_process_path_even_when_ffmpeg_is_there()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = true;
        try
        {

            //Act
            bool ffmpeg = InProcessAuthoring.UsesFFmpegPath(request, true, out string reason);

            //Assert
            ffmpeg.Should().BeFalse();
            reason.Should().BeNull();
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    [Fact]
    public void Vorbis_in_a_webm_document_is_refused_in_process_and_flac_is_refused_as_before()
    {
        //Arrange
        VideoAuthoringRequest vorbis = DryRequest();
        vorbis.Audio.Codec = AuthoringAudioCodec.LibVorbis;
        VideoAuthoringRequest flac = DryRequest();
        flac.Audio.Codec = AuthoringAudioCodec.Flac;

        //Act
        Action renderVorbis = () => CbvAuthor.RenderCommands(vorbis);
        Action renderFlac = () => CbvAuthor.RenderCommands(flac);

        //Assert
        renderVorbis.Should().Throw<VideoAuthoringException>()
            .WithMessage("*Vorbis audio in a WebM-profile (Mode1) file encoded in process*");
        renderFlac.Should().Throw<VideoAuthoringException>().WithMessage("*FLAC audio in a WebM-profile (Mode1) file*");
    }

    [Fact]
    public void The_ffmpeg_path_still_writes_vorbis_into_a_webm_document()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        request.Audio.Codec = AuthoringAudioCodec.LibVorbis;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = true;
        try
        {

            //Act
            IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

            //Assert
            commands.Count.Should().Be(1);
            commands[0].Arguments.Should().Contain("-c:a libvorbis");
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    [Fact]
    public void Surround_opus_is_refused_in_process()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.Channels = 6;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*Opus audio in 6 channels encoded in process*");
    }

    [Fact]
    public void Opus_with_the_no_extra_packages_rule_is_refused_as_before()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.RequireNoExtraPlaybackPackages = true;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*need no extra package to play*");
    }

    #endregion

    #region The Opus header and settings

    [Fact]
    public void The_opus_head_built_here_is_byte_for_byte_the_one_ffmpeg_writes()
    {
        //Arrange
        string path = AuthoringTestAssets.Path_("opus-audio.ogg");
        Assert.SkipUnless(File.Exists(path), "The FFmpeg-made Opus fixture is not beside the test assembly.");
        using OggAudioStream reference = OggAudioStream.Open(path);

        //Act
        byte[] built = InProcessAuthoring.BuildOpusHead(reference.Channels, reference.PreSkipSamples, 48000);

        //Assert
        reference.CodecId.Should().Be(VideoCodecIds.Opus);
        built.Length.Should().Be(19);
        Convert.ToHexString(built).Should().Be(Convert.ToHexString(reference.CodecPrivate.ToArray()));
    }

    [Fact]
    public void A_header_that_disagrees_with_the_one_built_is_named()
    {
        //Arrange
        byte[] built = InProcessAuthoring.BuildOpusHead(2, 312, 48000);
        byte[] stated = InProcessAuthoring.BuildOpusHead(2, 120, 48000);

        //Act
        string mismatch = InProcessAuthoring.DescribeOpusHeadProblem(stated, built);
        string agrees = InProcessAuthoring.DescribeOpusHeadProblem(built, built);

        //Assert
        mismatch.Should().Contain("the encoder stated");
        agrees.Should().BeNull();
    }

    [Fact]
    public void The_packet_encoders_identification_header_is_the_one_built_here()
    {
        //Arrange
        AuthoringAudioSettings audio = new AuthoringAudioSettings { BitrateKilobitsPerSecond = 96 };
        using OpusPacketEncoder encoder = new OpusPacketEncoder(2, 48000, InProcessAuthoring.OpusOptionsFor(audio));

        //Act
        byte[] built = InProcessAuthoring.BuildOpusHead(2, encoder.PreSkip, 48000);

        //Assert
        InProcessAuthoring.DescribeOpusHeadProblem(encoder.IdentificationHeader.Span, built).Should().BeNull();
    }

    [Fact]
    public void An_opus_bit_rate_is_the_whole_streams_target_with_libopus_defaults()
    {
        //Arrange
        AuthoringAudioSettings audio = new AuthoringAudioSettings { BitrateKilobitsPerSecond = 96 };

        //Act
        OpusEncoderOptions options = InProcessAuthoring.OpusOptionsFor(audio);

        //Assert
        options.Bitrate.Should().Be(96000);
        options.UseVariableBitrate.Should().BeTrue();
        options.Complexity.Should().Be(10);
        options.Profile.Should().Be(OpusEncodingProfile.Music);
    }

    #endregion

    #region Writing and playing

    [Fact]
    public void The_package_encoder_writes_a_webm_file_with_av1_opus_captions_and_chapters()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process");
        VideoAuthoringRequest request = RequestWithText(work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Flavour.Should().Be(VideoAuthoringFlavour.WebMProfile);
        result.PassesProfile.Should().BeTrue();
        result.Mux.Should().BeNull();
        result.MatroskaMux.Should().NotBeNull();
        result.MatroskaMux.DocType.Should().Be(MatroskaDocType.WebM);
        result.MatroskaMux.CuesPrecedeClusters.Should().BeTrue();
        result.MatroskaMux.ProfileReport.Should().BeSameAs(result.Profile);
        result.Commands.Count.Should().Be(3);
        result.Commands[0].Label.Should().Be("video pass");
        result.Commands[0].Arguments.Should().Contain("size 128x72");
        result.Commands[1].Arguments.Should().StartWith("opus 96 kbit/s 48000 Hz 2 channel(s)");
        result.Commands[2].Arguments.Should().StartWith("mux webm cues-to-front 2 caption track(s) chapters ");
        foreach (AuthoringCommand command in result.Commands) command.RunsFFmpeg.Should().BeFalse();
        string.Join("\n", result.Notes).Should().NotContain("encoded in process instead");
        string.Join("\n", result.Notes).Should().NotContain("DROPPED");
        File.Exists(work.File("clip.video.ivf")).Should().BeFalse();
        File.Exists(work.File("clip.audio.opus")).Should().BeFalse();
        File.Exists(result.OutputPath + ".pass1.tmp").Should().BeFalse();

        reader.DocType.Should().Be("webm");
        reader.Title.Should().Be("In-process clip");
        reader.Tracks.Count.Should().Be(4);
        reader.Tracks[0].Kind.Should().Be(MediaTrackKind.Video);
        reader.Tracks[0].CodecId.Should().Be(VideoCodecIds.Av1);
        reader.Tracks[0].Width.Should().Be(128);
        reader.Tracks[0].Height.Should().Be(72);
        reader.Tracks[0].DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(100));

        MediaTrackInfo audio = reader.Tracks[1];
        audio.Kind.Should().Be(MediaTrackKind.Audio);
        audio.CodecId.Should().Be(VideoCodecIds.Opus);
        audio.SampleRate.Should().Be(48000);
        audio.Channels.Should().Be(2);
        audio.Language.Should().Be("en");
        audio.PreSkipSamples.Should().BeGreaterThan(0);
        audio.CodecDelay.Should().Be(TimeSpan.FromTicks(audio.PreSkipSamples * TimeSpan.TicksPerSecond / 48000));
        audio.SeekPreRoll.Should().Be(TimeSpan.FromMilliseconds(80));

        // Both caption tracks, with their flags - the hearing-impaired one too, which FFmpeg's WebM writer drops.
        IReadOnlyList<CaptionTrack> captions = reader.CaptionTracks;
        captions.Count.Should().Be(2);
        captions[0].Language.Should().Be("en");
        captions[0].Name.Should().Be("English");
        (captions[0].Flags & CaptionTrackFlags.Default).Should().Be(CaptionTrackFlags.Default);
        captions[1].Name.Should().Be("English SDH");
        captions[1].IsHearingImpaired.Should().BeTrue();

        // Every chapter title's translation survives, which FFmpeg's WebM writer cannot do.
        IReadOnlyList<Chapter> chapters = reader.Chapters;
        chapters.Count.Should().Be(2);
        chapters[0].Title.Should().Be("Opening");
        chapters[0].Titles["de"].Should().Be("Anfang");
        chapters[1].Title.Should().Be("Closing");
    }

    [Fact]
    public void The_captions_read_back_with_their_cues_identifiers_and_settings()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-captions");
        VideoAuthoringResult result = CbvAuthor.Write(RequestWithText(work));

        //Act
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));
        while (reader.TryReadPacket(out MediaPacket _))
        {
        }

        //Assert
        CaptionTrack english = reader.CaptionTracks[0];
        english.CueCount.Should().Be(2);
        english.Cues[0].Text.Should().Be("Hello there");
        english.Cues[0].Identifier.Should().Be("opening-cue");
        english.Cues[0].Settings.Should().Contain("position:50%");
        english.Cues[0].Start.Should().Be(TimeSpan.Zero);
        english.Cues[0].End.Should().Be(TimeSpan.FromSeconds(1));
        reader.CaptionTracks[1].Cues[0].Text.Should().Be("[door closes] Hello there");
    }

    [Fact]
    public void A_session_plays_the_file_to_the_end_and_every_opus_packet_decodes()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-play");
        VideoAuthoringResult result = CbvAuthor.Write(RequestWithText(work));

        //Act
        int frames = PlayToTheEnd(result.OutputPath, out bool ended);
        OpusTally sound = DecodeOpus(result.OutputPath);

        //Assert
        ended.Should().BeTrue();
        frames.Should().BeGreaterThanOrEqualTo(15);
        sound.FirstTimestamp.Should().Be(TimeSpan.Zero);
        sound.Packets.Should().BeGreaterThan(50);
        sound.LastDiscardPadding.Should().BeGreaterThan(TimeSpan.Zero);

        // What is HEARD: every decoded sample, less the pre-skip and the end trim the container states, is the
        // source's two seconds of tone.
        double heardSeconds = (sound.FramesDecoded - sound.PreSkip - sound.TrimFrames) / 48000d;
        Math.Abs(heardSeconds - SyntheticSource.DurationSeconds).Should().BeLessThan(0.025);

        // And at the source's level: FFmpeg's sine is 1/8 full scale (RMS 0.0884), and the synthetic source's
        // mono-to-stereo upmix puts it into each channel at -3 dB, so 0.0625 per channel.
        Math.Abs(sound.Rms - 0.0625).Should().BeLessThan(0.01);
    }

    [Fact]
    public void A_matroska_document_carries_vorbis_encoded_in_process()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-mkv");
        VideoAuthoringRequest request = RequestWithText(work);
        request.OutputPath = work.File("clip.mkv");
        request.Container = AuthoringContainerFormat.Matroska;
        request.Audio.Codec = AuthoringAudioCodec.LibVorbis;
        request.FailWhenProfileFails = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Profile.Should().NotBeNull();
        result.MatroskaMux.DocType.Should().Be(MatroskaDocType.Matroska);
        result.Commands[1].Arguments.Should().StartWith("vorbis 96 kbit/s");
        result.Commands[2].Arguments.Should().StartWith("mux matroska cues-to-front");
        File.Exists(work.File("clip.audio.ogg")).Should().BeFalse();
        reader.DocType.Should().Be("matroska");
        reader.Tracks[1].CodecId.Should().Be(VideoCodecIds.Vorbis);
        reader.CaptionTracks.Count.Should().Be(2);
        reader.CaptionTracks[1].IsHearingImpaired.Should().BeTrue();
        PlayToTheEnd(result.OutputPath, out bool ended);
        ended.Should().BeTrue();
    }

    [Fact]
    public void A_matroska_document_takes_opus_by_default_too()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-mkv-opus");
        VideoAuthoringRequest request = RequestWithText(work);
        request.OutputPath = work.File("clip.mkv");
        request.Container = AuthoringContainerFormat.Matroska;
        request.FailWhenProfileFails = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        reader.DocType.Should().Be("matroska");
        reader.Tracks[1].CodecId.Should().Be(VideoCodecIds.Opus);
        result.Profile.Should().NotBeNull();
    }

    [Fact]
    public void An_ffmpeg_encoder_falls_back_in_process_with_a_note_when_the_ffmpeg_path_is_not_there()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-fallback");
        VideoAuthoringRequest request = RequestWithText(work);
        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = false;
        try
        {

            //Act
            VideoAuthoringResult result = CbvAuthor.Write(request);

            //Assert
            result.PassesProfile.Should().BeTrue();
            result.MatroskaMux.Should().NotBeNull();
            result.Commands[0].RunsFFmpeg.Should().BeFalse();
            string.Join("\n", result.Notes).Should().Contain("asked for libsvtav1 through ffmpeg");
            string.Join("\n", result.Notes).Should().Contain("Opus by the in-process Opus encoder");
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    [Fact]
    public void Settings_with_no_in_process_meaning_are_named_in_the_notes()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-notes");
        VideoAuthoringRequest request = RequestWithText(work);
        request.CopySourceMetadata = true;
        request.SelectStreamsExplicitly = false;
        request.Audio.SampleRateHz = 24000;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        string notes = string.Join("\n", result.Notes);
        notes.Should().Contain("CopySourceMetadata has no in-process meaning");
        notes.Should().Contain("SelectStreamsExplicitly = false has no in-process meaning");
        notes.Should().Contain("Audio.SampleRateHz = 24000 has no in-process meaning for Opus");
        reader.Tracks[1].SampleRate.Should().Be(48000);
    }

    [Fact]
    public void Without_a_chapter_file_the_file_has_no_title()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-untitled");
        VideoAuthoringRequest request = RequestWithText(work);
        request.ChaptersPath = null;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        reader.Title.Should().Be(string.Empty);
        reader.Chapters.Count.Should().Be(0);
        result.MatroskaMux.ChapterCount.Should().Be(0);
    }

    [Fact]
    public void An_explicit_title_wins_over_the_chapter_files_title()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-title");
        VideoAuthoringRequest request = RequestWithText(work);
        request.Title = "Explicit title";

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        reader.Title.Should().Be("Explicit title");
        string.Join("\n", result.Notes).Should().NotContain("Title has no field");
    }

    [Fact]
    public void With_the_index_at_the_end_the_profile_fails_and_the_request_is_refused_unless_it_allows_that()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-cues-at-end");
        VideoAuthoringRequest strict = RequestWithText(work);
        strict.CuesToFront = false;
        strict.OutputPath = work.File("strict.webm");
        VideoAuthoringRequest lenient = RequestWithText(work);
        lenient.CuesToFront = false;
        lenient.FailWhenProfileFails = false;
        lenient.OutputPath = work.File("lenient.webm");

        //Act
        Action writeStrict = () => CbvAuthor.Write(strict);
        VideoAuthoringResult result = CbvAuthor.Write(lenient);

        //Assert
        writeStrict.Should().Throw<VideoAuthoringException>().WithMessage("*does not pass the streamable profile*");
        result.PassesProfile.Should().BeFalse();
        result.MatroskaMux.CuesPrecedeClusters.Should().BeFalse();
        result.Commands[2].Arguments.Should().StartWith("mux webm cues-at-end");
    }

    [Fact]
    public void An_odd_sized_source_is_cropped_to_even_and_says_so()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-in-process-odd");
        VideoAuthoringRequest request = RequestWithText(work);
        request.SourcePath = SyntheticAlphaSource.WriteClip(work.File("odd.mkv"), 129, 73);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.Commands.Count.Should().Be(2);
        string.Join("\n", result.Notes).Should().Contain("cropped away (not resampled) and it was encoded at 128x72");
        reader.Tracks[0].Width.Should().Be(128);
        reader.Tracks[0].Height.Should().Be(72);
        reader.Tracks.Count.Should().Be(3);
    }

    #endregion

    private static VideoAuthoringRequest DryRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.WebMProfile,
            SourcePath = "/nowhere/source.mov",
            OutputPath = "/nowhere/clip.webm",
            TemporaryFolder = "/nowhere/work",
        };

        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        request.Video.KeyframeIntervalFrames = 48;
        return request;
    }

    private static VideoAuthoringRequest RequestWithText(WorkFolder work)
    {
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        string full = SyntheticSource.WriteCaptions(work.File("en.vtt"), "Hello there");
        string sdh = SyntheticSource.WriteCaptions(work.File("en-sdh.vtt"), "[door closes] Hello there");
        string chapters = SyntheticSource.WriteMultilingualChapters(work.File("chapters.ffmeta"));

        // A file-level title in front of the chapters: the in-process path takes it as the file's title, as
        // FFmpeg's pass does through -map_metadata.
        File.WriteAllText(chapters, File.ReadAllText(chapters).Replace(";FFMETADATA1\n", ";FFMETADATA1\ntitle=In-process clip\n"));

        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.WebMProfile,
            SourcePath = source,
            OutputPath = work.File("clip.webm"),
            TemporaryFolder = work.Path,
            ChaptersPath = chapters,
        };

        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        request.Video.SpeedPreset = 8;
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;
        request.Audio.Language = "en";
        request.Captions.Add(new AuthoringCaptionInput(full, "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new AuthoringCaptionInput(sdh, "en", "English SDH", CaptionTrackFlags.HearingImpaired));
        return request;
    }

    private static int PlayToTheEnd(string path, out bool ended)
    {
        int frames = 0;
        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
        string failure = null;
        session.MediaFailed += (s, e) => failure = e.Exception == null ? "media failed" : e.Exception.Message;
        int endedCount = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref endedCount);
        session.Open(path);
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout && failure == null)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame) frames++;
            }

            if (Volatile.Read(ref endedCount) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        failure.Should().BeNull();
        ended = Volatile.Read(ref endedCount) > 0;
        return frames;
    }

    private static OpusTally DecodeOpus(string path)
    {
        OpusTally tally = new OpusTally();
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(path));
        MediaTrackInfo audio = reader.Tracks[1];
        tally.PreSkip = audio.PreSkipSamples;

        using IPacketSoundDecoder decoder = new OpusPacketCodecFactory().CreateDecoder(
            "opus", audio.CodecPrivate, null);
        float[] output = new float[decoder.MaxSamplesPerPacket];
        double sumOfSquares = 0;
        long samples = 0;
        bool first = true;

        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != audio.Id) continue;
            if (first)
            {
                tally.FirstTimestamp = packet.Timestamp;
                first = false;
            }

            int written = decoder.DecodePacket(packet.Data.Span, output);
            for (int i = 0; i < written; i++)
            {
                sumOfSquares += output[i] * (double)output[i];
            }

            samples += written;
            tally.Packets++;
            tally.LastDiscardPadding = packet.DiscardPadding;
        }

        tally.FramesDecoded = samples / decoder.Channels;
        tally.TrimFrames = (long)Math.Round(tally.LastDiscardPadding.TotalSeconds * 48000d);
        tally.Rms = samples == 0 ? 0 : Math.Sqrt(sumOfSquares / samples);
        return tally;
    }

    private sealed class OpusTally
    {
        public TimeSpan FirstTimestamp = TimeSpan.MinValue;
        public int Packets;
        public long FramesDecoded;
        public int PreSkip;
        public long TrimFrames;
        public TimeSpan LastDiscardPadding;
        public double Rms;
    }
}
