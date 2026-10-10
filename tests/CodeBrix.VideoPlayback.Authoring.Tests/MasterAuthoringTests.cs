using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The master flavour (<c>.cbvmaster</c>, Mode3) through the FFmpeg bridge: what it would run, what it refuses,
/// what it writes with and without alpha, and what that PLAYS as with a real AV1 decoder.
/// </summary>
public class MasterAuthoringTests
{
    private const string FlacDecodeSkipReason =
        "needs CodeBrix.Audio.Core with FlacPacketCodecFactory - unskip after the Core pin is raised";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    #region Dry run and refusals

    [Fact]
    public void A_master_dry_run_lists_a_video_an_alpha_and_an_audio_pass()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(3);
        commands[0].Label.Should().Be("video pass");
        commands[1].Label.Should().Be("alpha pass");
        commands[2].Label.Should().Be("audio pass");
        commands[0].Arguments.Should().Contain("-c:v libsvtav1");
        commands[1].Arguments.Should().Contain("-c:v libaom-av1");
        commands[1].Arguments.Should().Contain("-usage realtime");
        commands[1].Arguments.Should().Contain("-pix_fmt gray");
        commands[1].Arguments.Should().Contain("-color_range pc");
        commands[1].Arguments.Should().Contain("-crf 24");
        commands[1].Arguments.Should().Contain("alphaextract");
        commands[1].Arguments.Should().Contain("-force_key_frames " + AuthoringCommandFactory.KeyFramesOfVideoPass);
        commands[1].Arguments.Should().Contain("-f ivf");
        commands[2].Arguments.Should().Contain("-c:a flac");
        commands[2].Arguments.Should().Contain("-sample_fmt s16");
        commands[2].Arguments.Should().Contain("-f flac");
    }

    [Fact]
    public void Excluding_alpha_drops_the_alpha_pass()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Alpha = AuthoringAlphaMode.Exclude;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
        commands[1].Label.Should().Be("audio pass");
    }

    [Fact]
    public void Twenty_four_bit_flac_is_asked_for_as_s32_with_a_raw_sample_size()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.FlacBitDepth = 24;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[2].Arguments.Should().Contain("-sample_fmt s32 -bits_per_raw_sample 24");
    }

    [Fact]
    public void The_alpha_rate_factor_defaults_to_six_below_the_picture_and_can_be_set()
    {
        //Arrange
        AuthoringVideoSettings video = new AuthoringVideoSettings { ConstantRateFactor = 3 };

        //Act
        int clamped = video.ResolvedAlphaConstantRateFactor;
        video.AlphaConstantRateFactor = 12;
        int set = video.ResolvedAlphaConstantRateFactor;

        //Assert
        clamped.Should().Be(0);
        set.Should().Be(12);
    }

    [Fact]
    public void Opus_in_a_master_file_is_refused()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.Codec = AuthoringAudioCodec.LibOpus;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*libopus audio in a master*FLAC is the only codec*");
    }

    [Fact]
    public void Flac_in_a_bespoke_file_is_refused()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Flavour = VideoAuthoringFlavour.Bespoke;
        request.Audio.Codec = AuthoringAudioCodec.Flac;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*FLAC audio in a bespoke (Mode2) file*");
    }

    [Fact]
    public void Flac_in_a_webm_profile_file_is_refused()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Flavour = VideoAuthoringFlavour.WebMProfile;
        request.Audio.Codec = AuthoringAudioCodec.Flac;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*FLAC audio in a WebM-profile (Mode1) file*");
    }

    [Fact]
    public void A_flac_bit_depth_other_than_16_or_24_is_refused()
    {
        //Arrange
        AuthoringAudioSettings audio = new AuthoringAudioSettings();

        //Act
        Action set = () => audio.FlacBitDepth = 20;

        //Assert
        set.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("yuva420p", true)]
    [InlineData("yuva444p10le", true)]
    [InlineData("rgba", true)]
    [InlineData("bgra", true)]
    [InlineData("argb", true)]
    [InlineData("gbrap", true)]
    [InlineData("ya8", true)]
    [InlineData("yuv420p", false)]
    [InlineData("rgb24", false)]
    [InlineData("gray", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pixel_formats_with_an_alpha_channel_are_recognised(string pixelFormat, bool expected)
    {
        //Arrange
        string format = pixelFormat;

        //Act
        bool hasAlpha = CbvAuthor.PixelFormatHasAlpha(format);

        //Assert
        hasAlpha.Should().Be(expected);
    }

    [Fact]
    public void A_build_without_libaom_or_flac_is_warned_about_for_the_master_flavour()
    {
        //Arrange
        Func<string, bool> neither = name => name != AuthoringEncoderNames.LibAomAv1 && name != AuthoringEncoderNames.Flac;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeMasterEncoderWarnings(neither);

        //Assert
        warnings.Count.Should().Be(2);
        warnings[0].Should().Contain("(libaom-av1)");
        warnings[1].Should().Contain("(flac)");
    }

    [Fact]
    public void A_build_with_every_encoder_earns_no_master_warning()
    {
        //Arrange
        Func<string, bool> everything = _ => true;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeMasterEncoderWarnings(everything);

        //Assert
        warnings.Count.Should().Be(0);
    }

    [Fact]
    public void This_machines_libaom_writes_monochrome_av1()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        Assert.SkipUnless(AuthoringEncoders.HasAomAv1Encoder, "This FFmpeg has no libaom-av1.");

        //Act
        bool monochrome = AuthoringTools.CanEncodeMonochromeAv1(out string reason);

        //Assert
        monochrome.Should().BeTrue(reason);
    }

    #endregion

    #region Writing

    [Fact]
    public void Master_authoring_with_alpha_writes_a_version_1_file_with_three_tracks_in_lock_step()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-alpha");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Flavour.Should().Be(VideoAuthoringFlavour.Master);
        result.Commands.Count.Should().Be(3);
        result.Commands[1].Label.Should().Be("alpha pass");
        result.Commands[1].Arguments.Should().NotContain(AuthoringCommandFactory.KeyFramesOfVideoPass);
        result.Profile.Passes.Should().BeTrue();
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.Tracks.Count.Should().Be(3);
        reader.AlphaPlaneTrack.Should().NotBeNull();
        reader.AlphaPlaneTrack.Layout.Should().Be(VideoPixelLayout.Gray);
        reader.AlphaPlaneTrack.Color.Range.Should().Be(VideoColorRange.Full);
        reader.Tracks[2].CodecId.Should().Be(VideoCodecIds.Flac);
        File.Exists(work.File("clip.alpha.ivf")).Should().BeFalse();
        File.Exists(work.File("clip.audio.flac")).Should().BeFalse();
    }

    [Fact]
    public void Master_authoring_of_an_opaque_source_leaves_the_alpha_out_and_says_why()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-opaque");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.AlphaPlaneTrack.Should().BeNull();
        reader.Tracks.Count.Should().Be(2);
        string.Join("\n", result.Notes).Should().Contain("has no alpha channel");
    }

    [Fact]
    public void Including_alpha_for_an_opaque_source_writes_a_fully_opaque_plane()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-opaque-include");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Video.Alpha = AuthoringAlphaMode.Include;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        (int frames, int minimum, int maximum) = DecodeAlphaRange(result.OutputPath);

        //Assert
        frames.Should().BeGreaterThan(0);
        minimum.Should().BeGreaterThanOrEqualTo(250);
        maximum.Should().Be(255);
    }

    [Fact]
    public void A_name_without_the_master_extension_earns_a_note()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-extension");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbv"), work);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        string.Join("\n", result.Notes).Should().Contain(CbvFormat.MasterFileExtension);
    }

    #endregion

    #region Playing

    [Fact]
    public void An_authored_master_file_plays_with_its_alpha_plane_paired_to_every_frame()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-play");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringResult result = CbvAuthor.Write(MasterRequest(source, work.File("clip.cbvmaster"), work));

        //Act
        AlphaTally tally = PlayThrough(result.OutputPath);

        //Assert
        tally.Failure.Should().BeNull();
        tally.HasAlpha.Should().BeTrue();
        tally.Frames.Should().BeGreaterThanOrEqualTo(15);
        tally.FramesWithoutAlpha.Should().Be(0);
        tally.WorstLeftColumn.Should().BeLessThanOrEqualTo(12);
        tally.WorstRightColumn.Should().BeGreaterThanOrEqualTo(243);
    }

    [Fact]
    public void The_committed_alpha_fixture_plays_with_its_alpha_plane()
    {
        //Arrange
        string path = AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster");
        Assert.SkipUnless(File.Exists(path), "The master fixture is not beside the test assembly.");

        //Act
        AlphaTally tally = PlayThrough(path);

        //Assert
        tally.Failure.Should().BeNull();
        tally.HasAlpha.Should().BeTrue();
        tally.Frames.Should().BeGreaterThanOrEqualTo(6);
        tally.FramesWithoutAlpha.Should().Be(0);
        tally.WorstLeftColumn.Should().BeLessThanOrEqualTo(12);
        tally.WorstRightColumn.Should().BeGreaterThanOrEqualTo(243);
    }

    [Fact]
    public void A_seek_in_the_committed_fixture_lands_on_the_same_frame_in_both_tracks()
    {
        //Arrange
        string path = AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster");
        Assert.SkipUnless(File.Exists(path), "The master fixture is not beside the test assembly.");
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        session.Presenter.Clear();

        //Act
        session.Seek(TimeSpan.FromSeconds(0.5));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        session.Presenter.TryTakeLatest(out VideoFrame frame);

        //Assert
        arrived.Should().BeTrue();
        using (frame)
        {
            // Frame 6 of a 12-per-second clip; the IVF time base makes it 0.5 s to within a tick or so.
            Math.Abs((frame.Timestamp - TimeSpan.FromSeconds(0.5)).TotalMilliseconds).Should().BeLessThan(1.0);
            frame.HasAlpha.Should().BeTrue();
            frame.A.GetRowBytes(0)[0].Should().BeLessThanOrEqualTo((byte)12);
        }
    }

    [Fact]
    public void The_committed_no_alpha_fixture_plays_opaque()
    {
        //Arrange
        string path = AuthoringTestAssets.Path_("av1-flac.cbvmaster");
        Assert.SkipUnless(File.Exists(path), "The master fixture is not beside the test assembly.");

        //Act
        AlphaTally tally = PlayThrough(path);

        //Assert
        tally.Failure.Should().BeNull();
        tally.HasAlpha.Should().BeFalse();
        tally.Frames.Should().BeGreaterThanOrEqualTo(6);
        tally.FramesWithoutAlpha.Should().Be(tally.Frames);
    }

    [Fact(Skip = FlacDecodeSkipReason)]
    public void A_session_opens_a_master_file_with_its_flac_sound()
    {
        //Arrange
        string path = AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster");
        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = true });
        CodeBrixVideoPlaybackDav1d.Register(session);

        //Act
        session.Open(path);

        //Assert
        session.AudioTrack.CodecId.Should().Be(VideoCodecIds.Flac);
        session.HasAlpha.Should().BeTrue();
    }

    #endregion

    private static VideoAuthoringRequest DryRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = "/nowhere/source.mov",
            OutputPath = "/nowhere/clip.cbvmaster",
            TemporaryFolder = "/nowhere/work",
        };

        request.Video.KeyframeIntervalFrames = 48;
        return request;
    }

    private static VideoAuthoringRequest MasterRequest(string source, string output, WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work.Path,
            AllowAv1EncoderFallback = true,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        return request;
    }

    private static void SkipWithoutMasterTools()
    {
        SyntheticSource.SkipWithoutFFmpeg();
        Assert.SkipUnless(AuthoringEncoders.HasAomAv1Encoder, "The master flavour's alpha pass needs libaom-av1.");
    }

    private static VideoPlaybackSession NewSession()
    {
        VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
        return session;
    }

    private static (int Frames, int Minimum, int Maximum) DecodeAlphaRange(string path)
    {
        int frames = 0;
        int minimum = 255;
        int maximum = 0;

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);
        session.Open(path);
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame)
                {
                    if (!frame.HasAlpha) continue;
                    frames++;
                    for (int y = 0; y < frame.Height; y += 7)
                    {
                        foreach (byte sample in frame.A.GetRowBytes(y))
                        {
                            minimum = Math.Min(minimum, sample);
                            maximum = Math.Max(maximum, sample);
                        }
                    }
                }
            }

            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(5);
        }

        return (frames, minimum, maximum);
    }

    private static AlphaTally PlayThrough(string path)
    {
        AlphaTally tally = new AlphaTally { WorstLeftColumn = 0, WorstRightColumn = 255 };

        using VideoPlaybackSession session = NewSession();
        session.MediaFailed += (s, e) => tally.Failure = e.Exception == null ? "media failed" : e.Exception.Message;
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        session.Open(path);
        tally.HasAlpha = session.HasAlpha;
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame)
                {
                    tally.Frames++;
                    if (!frame.HasAlpha)
                    {
                        tally.FramesWithoutAlpha++;
                        continue;
                    }

                    // The source's alpha is 0 in the left column and 255 in the right one, in every row.
                    for (int y = 0; y < frame.Height; y++)
                    {
                        ReadOnlySpan<byte> row = frame.A.GetRowBytes(y);
                        tally.WorstLeftColumn = Math.Max(tally.WorstLeftColumn, row[0]);
                        tally.WorstRightColumn = Math.Min(tally.WorstRightColumn, row[frame.Width - 1]);
                    }
                }
            }

            if (tally.Failure != null) break;
            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        return tally;
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

    private sealed class AlphaTally
    {
        public int Frames;
        public int FramesWithoutAlpha;
        public bool HasAlpha;
        public int WorstLeftColumn;
        public int WorstRightColumn;
        public string Failure;
    }
}
