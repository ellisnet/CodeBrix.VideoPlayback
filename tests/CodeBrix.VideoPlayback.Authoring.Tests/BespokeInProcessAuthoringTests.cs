using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
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
/// The bespoke flavour (<c>.cbv</c>, Mode2) on its in-process path: the picture encoded by the AV1 encoder
/// package and the sound by the Vorbis encoder package, either because the request asked for
/// <see cref="AuthoringVideoEncoder.Av1Package" /> or as the fallback when the ffmpeg path is not available -
/// what it lists, what it writes, and what that plays as.
/// </summary>
public class BespokeInProcessAuthoringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    #region Dry run and the choice of path

    [Fact]
    public void A_bespoke_dry_run_with_the_package_encoder_lists_two_in_process_passes()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
        commands[0].Label.Should().Be("video pass");
        commands[0].RunsFFmpeg.Should().BeFalse();
        commands[0].Arguments.Should().Be(
            "av1 i420 8-bit size decoded-cropped-to-even good-quality cq-level 30 cpu-used 6 keyint 48 "
            + "<- /nowhere/source.mov -> " + Path.Combine("/nowhere/work", "clip.video.ivf"));
        commands[1].Label.Should().Be("audio pass");
        commands[1].RunsFFmpeg.Should().BeFalse();
        commands[1].Arguments.Should().Be(
            "vorbis 128 kbit/s 48000 Hz 2 channel(s) <- /nowhere/source.mov -> "
            + Path.Combine("/nowhere/work", "clip.audio.ogg"));
    }

    [Fact]
    public void A_vorbis_quality_is_listed_instead_of_a_bit_rate()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.VorbisQuality = 5;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[1].Arguments.Should().StartWith("vorbis quality 5 48000 Hz");
    }

    [Fact]
    public void A_frame_size_adds_the_prepare_pass_without_an_alpha_channel()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(3);
        commands[0].Label.Should().Be("prepare pass");
        commands[0].RunsFFmpeg.Should().BeTrue();
        commands[0].Arguments.Should().Contain("-pix_fmt yuv420p");
        commands[0].Arguments.Should().NotContain("yuva420p");
        commands[1].Arguments.Should().Contain("clip.prepared.mkv");
    }

    [Fact]
    public void An_ffmpeg_encoder_keeps_the_ffmpeg_command_lines_when_the_ffmpeg_path_is_there()
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
            commands.Count.Should().Be(2);
            commands[0].RunsFFmpeg.Should().BeTrue();
            commands[0].Arguments.Should().Contain("-c:v libsvtav1");
            commands[1].Arguments.Should().Contain("-c:a libvorbis");
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
        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = false;
        try
        {

            //Act
            bool ffmpeg = InProcessAuthoring.UsesFFmpegPath(request, true, out string reason);
            IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

            //Assert
            ffmpeg.Should().BeFalse();
            reason.Should().Contain("asked for libsvtav1 through ffmpeg");
            reason.Should().Contain("encoded in process instead");
            commands[0].RunsFFmpeg.Should().BeFalse();
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
    public void Opus_and_flac_are_still_refused_in_a_bespoke_file_on_the_in_process_path()
    {
        //Arrange
        VideoAuthoringRequest opus = DryRequest();
        opus.Audio.Codec = AuthoringAudioCodec.LibOpus;
        VideoAuthoringRequest flac = DryRequest();
        flac.Audio.Codec = AuthoringAudioCodec.Flac;

        //Act
        Action renderOpus = () => CbvAuthor.RenderCommands(opus);
        Action renderFlac = () => CbvAuthor.RenderCommands(flac);

        //Assert
        renderOpus.Should().Throw<VideoAuthoringException>().WithMessage("*Opus audio in a bespoke*");
        renderFlac.Should().Throw<VideoAuthoringException>().WithMessage("*FLAC audio in a bespoke (Mode2) file*");
    }

    [Theory]
    [InlineData(5.0, 0.5f)]
    [InlineData(-1.0, -0.1f)]
    [InlineData(10.0, 1.0f)]
    public void A_vorbis_quality_number_means_what_libvorbis_makes_of_it(double asked, float expected)
    {
        //Arrange
        AuthoringAudioSettings audio = new AuthoringAudioSettings { VorbisQuality = asked };

        //Act
        CodeBrix.Audio.Vorbis.VorbisEncoderOptions options = InProcessAuthoring.VorbisOptionsFor(audio);

        //Assert
        options.Mode.Should().Be(CodeBrix.Audio.Vorbis.VorbisEncoderMode.Quality);
        Math.Abs(options.Quality - expected).Should().BeLessThan(0.0001f);
    }

    [Fact]
    public void A_vorbis_bit_rate_is_the_nominal_bit_rate()
    {
        //Arrange
        AuthoringAudioSettings audio = new AuthoringAudioSettings { BitrateKilobitsPerSecond = 96 };

        //Act
        CodeBrix.Audio.Vorbis.VorbisEncoderOptions options = InProcessAuthoring.VorbisOptionsFor(audio);

        //Assert
        options.Mode.Should().Be(CodeBrix.Audio.Vorbis.VorbisEncoderMode.Bitrate);
        options.NominalBitrate.Should().Be(96000);
    }

    #endregion

    #region Writing and playing

    [Fact]
    public void The_package_encoder_writes_a_bespoke_file_with_captions_chapters_and_vorbis_that_plays()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-in-process");
        VideoAuthoringRequest request = RequestWithText(work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        int frames = CountFrames(result.OutputPath);

        //Assert
        result.Flavour.Should().Be(VideoAuthoringFlavour.Bespoke);
        result.PassesProfile.Should().BeTrue();
        result.Commands.Count.Should().Be(2);
        result.Commands[0].RunsFFmpeg.Should().BeFalse();
        result.Commands[1].Arguments.Should().StartWith("vorbis 96 kbit/s");
        string.Join("\n", result.Notes).Should().NotContain("encoded in process instead");
        frames.Should().BeGreaterThanOrEqualTo(15);
        File.Exists(work.File("clip.video.ivf")).Should().BeFalse();
        File.Exists(work.File("clip.audio.ogg")).Should().BeFalse();

        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        reader.Version.Should().NotBe(CbvFormat.MasterVersion);
        reader.Tracks[0].DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(100));
        MediaTrackInfo audio = reader.Tracks[1];
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);
        audio.SampleRate.Should().Be(48000);
        audio.Channels.Should().Be(2);
        AudioDecoders.IsCodecSupported(audio.CodecId).Should().BeTrue();

        IReadOnlyList<CaptionTrack> captions = reader.CaptionTracks;
        captions.Count.Should().Be(2);
        captions[0].Cues[0].Identifier.Should().Be("opening-cue");
        (captions[1].Flags & CaptionTrackFlags.HearingImpaired).Should().Be(CaptionTrackFlags.HearingImpaired);

        IReadOnlyList<Chapter> chapters = reader.Chapters;
        chapters.Count.Should().Be(2);
        chapters[0].Titles["de"].Should().Be("Anfang");
    }

    [Fact]
    public void An_odd_sized_source_is_cropped_to_even_in_process_and_says_so()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-in-process-odd");
        VideoAuthoringRequest request = RequestWithText(work);
        request.SourcePath = SyntheticAlphaSource.WriteClip(work.File("odd.mkv"), 129, 73);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.PassesProfile.Should().BeTrue();
        string.Join("\n", result.Notes).Should().Contain(
            "the last column and the last row were cropped away (not resampled) and it was encoded at 128x72");
        result.Commands[0].Arguments.Should().Contain("size 128x72 cropped-from 129x73");
        reader.Tracks[0].Width.Should().Be(128);
        reader.Tracks[0].Height.Should().Be(72);
    }

    [Fact]
    public void The_sound_lasts_as_long_as_the_source()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-in-process-sound");
        VideoAuthoringRequest request = RequestWithText(work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        int audioId = reader.Tracks[1].Id;
        TimeSpan? end = null;
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == audioId) end = packet.Timestamp + packet.Duration;
        }

        //Assert
        end.HasValue.Should().BeTrue();
        Math.Abs((end.Value - SyntheticSource.Duration).TotalMilliseconds).Should().BeLessThan(60);
    }

    [Fact]
    public void An_ffmpeg_encoder_falls_back_in_process_with_a_note_when_the_ffmpeg_path_is_not_there()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-fallback");
        VideoAuthoringRequest request = RequestWithText(work);
        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        InProcessAuthoring.FFmpegPathAvailableOverride.Value = false;
        try
        {

            //Act
            VideoAuthoringResult result = CbvAuthor.Write(request);

            //Assert
            result.PassesProfile.Should().BeTrue();
            result.Commands[0].RunsFFmpeg.Should().BeFalse();
            string.Join("\n", result.Notes).Should().Contain("asked for libsvtav1 through ffmpeg");
        }
        finally
        {
            InProcessAuthoring.FFmpegPathAvailableOverride.Value = null;
        }
    }

    #endregion

    private static VideoAuthoringRequest DryRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = "/nowhere/source.mov",
            OutputPath = "/nowhere/clip.cbv",
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

        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = source,
            OutputPath = work.File("clip.cbv"),
            TemporaryFolder = work.Path,
            ChaptersPath = chapters,
        };

        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        request.Video.SpeedPreset = 8;
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;
        request.Captions.Add(new AuthoringCaptionInput(full, "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new AuthoringCaptionInput(sdh, "en", "English SDH", CaptionTrackFlags.HearingImpaired));
        return request;
    }

    private static int CountFrames(string path)
    {
        int frames = 0;
        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
        string failure = null;
        session.MediaFailed += (s, e) => failure = e.Exception == null ? "media failed" : e.Exception.Message;
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);
        session.Open(path);
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout && failure == null)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame) frames++;
            }

            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        failure.Should().BeNull();
        return frames;
    }
}
