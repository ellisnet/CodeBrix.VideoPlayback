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
/// The master flavour (<c>.cbvmaster</c>, Mode3): what it would run, what it refuses, what it writes with and
/// without alpha - decoded through the ffmpeg engine and encoded in process - and what that PLAYS as with a
/// real AV1 decoder.
/// </summary>
public class MasterAuthoringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    #region Dry run and refusals

    [Fact]
    public void A_master_dry_run_lists_a_video_an_alpha_and_an_audio_pass_that_run_in_process()
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
        commands[0].RunsFFmpeg.Should().BeFalse();
        commands[1].RunsFFmpeg.Should().BeFalse();
        commands[2].RunsFFmpeg.Should().BeFalse();
        commands[0].Arguments.Should().Be(
            "av1 i420 8-bit size decoded-cropped-to-even good-quality cq-level 30 cpu-used 6 keyint 48 fill-transparent-pixels "
            + "<- /nowhere/source.mov -> " + Path.Combine("/nowhere/work", "clip.video.ivf"));
        commands[1].Arguments.Should().Be(
            "av1 gray 8-bit size decoded-cropped-to-even full-range realtime cq-level 24 cpu-used 6 key-frames-of-video-pass "
            + "<- /nowhere/source.mov -> " + Path.Combine("/nowhere/work", "clip.alpha.ivf"));
        commands[2].Arguments.Should().Be(
            "flac 16-bit 48000 Hz 2 channel(s) compression-level 5 <- /nowhere/source.mov -> packets");
        commands[0].ToString().Should().StartWith("in process: av1 i420");
    }

    [Fact]
    public void Excluding_alpha_drops_the_alpha_pass_and_the_transparent_pixel_fill()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Alpha = AuthoringAlphaMode.Exclude;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
        commands[0].Arguments.Should().NotContain("fill-transparent-pixels");
        commands[1].Label.Should().Be("audio pass");
    }

    [Fact]
    public void Turning_the_fill_off_drops_it_from_the_video_pass()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FillTransparentPixels = false;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Arguments.Should().NotContain("fill-transparent-pixels");
    }

    [Fact]
    public void Twenty_four_bit_flac_is_listed_as_such()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.FlacBitDepth = 24;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[2].Arguments.Should().StartWith("flac 24-bit 48000 Hz 2 channel(s)");
    }

    [Fact]
    public void A_master_without_a_key_frame_interval_leaves_it_to_the_encoder()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.KeyframeIntervalFrames = 0;
        request.Video.SpeedPreset = 13;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Arguments.Should().Contain("cpu-used 8 keyint auto");
    }

    [Fact]
    public void A_frame_size_adds_an_ffmpeg_prepare_pass_whose_lossless_output_the_video_pass_reads()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        string prepared = Path.Combine("/nowhere/work", "clip.prepared.mkv");
        commands.Count.Should().Be(4);
        commands[0].Label.Should().Be("prepare pass");
        commands[0].RunsFFmpeg.Should().BeTrue();
        commands[0].Arguments.Should().Contain("-c:v ffv1");
        commands[0].Arguments.Should().Contain("scale=w=640:h=360");
        commands[0].Arguments.Should().Contain("format=yuva420p");
        commands[0].Arguments.Should().Contain("-pix_fmt yuva420p");
        commands[0].Arguments.Should().Contain("-an");
        commands[0].Arguments.Should().Contain("-f matroska");
        commands[0].Arguments.Should().Contain(prepared);
        commands[1].Arguments.Should().Contain("<- " + prepared + " ->");
        commands[2].Arguments.Should().Contain("<- " + prepared + " ->");
        commands[3].Arguments.Should().Contain("<- /nowhere/source.mov ->");
    }

    [Fact]
    public void A_prepare_pass_without_alpha_keeps_four_two_zero_without_an_alpha_channel()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.Alpha = AuthoringAlphaMode.Exclude;
        request.Video.FrameRate = 24;
        request.Video.FrameRateMode = AuthoringFrameRateMode.Filter;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].Arguments.Should().Contain("fps=24");
        commands[0].Arguments.Should().Contain("-pix_fmt yuv420p");
        commands[0].Arguments.Should().NotContain("yuva420p");
    }

    [Fact]
    public void A_prepare_pass_scales_with_exact_rounding_and_full_chroma_interpolation()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);

        VideoAuthoringRequest named = DryRequest();
        named.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);
        named.Video.ScalerFlags = "bicubic+accurate_rnd";

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);
        IReadOnlyList<AuthoringCommand> namedCommands = CbvAuthor.RenderCommands(named);

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].Arguments.Should().Contain("scale=w=640:h=360:flags=lanczos+accurate_rnd+full_chroma_int, format=");
        namedCommands[0].Arguments.Should().Contain("flags=bicubic+accurate_rnd+full_chroma_int, format=");
    }

    [Fact]
    public void A_prepare_pass_in_encoder_mode_converts_the_rate_with_the_fps_filter_at_the_end_of_the_chain()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameRate = 24;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].Arguments.Should().Contain("-vf \"fps=24, format=yuva420p\"");
        commands[0].Arguments.Should().NotContain("-r 24");
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
    public void A_build_without_libaom_or_flac_earns_no_master_warning()
    {
        //Arrange
        Func<string, bool> neither = name => name != AuthoringEncoderNames.LibAomAv1 && name != AuthoringEncoderNames.Flac;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeEncoderWarnings(neither);

        //Assert
        foreach (string warning in warnings)
        {
            warning.Should().NotContain("cbvmaster");
            warning.Should().NotContain("(flac)");
        }
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
        result.Commands[0].Label.Should().Be("video pass");
        result.Commands[0].Arguments.Should().Contain("fill-transparent-pixels");
        result.Commands[1].Label.Should().Be("alpha pass");
        result.Commands[1].Arguments.Should().Contain("<- " + source + " ->");
        result.Commands[2].Label.Should().Be("audio pass");
        result.Commands[2].RunsFFmpeg.Should().BeFalse();
        result.Profile.Passes.Should().BeTrue();
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.Tracks.Count.Should().Be(3);
        reader.Tracks[0].Width.Should().Be(SyntheticSource.Width);
        reader.Tracks[0].DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(100));
        reader.AlphaPlaneTrack.Should().NotBeNull();
        reader.AlphaPlaneTrack.Layout.Should().Be(VideoPixelLayout.Gray);
        reader.AlphaPlaneTrack.Color.Range.Should().Be(VideoColorRange.Full);
        reader.AlphaPlaneTrack.DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(100));
        reader.Tracks[2].CodecId.Should().Be(VideoCodecIds.Flac);
        File.Exists(work.File("clip.video.ivf")).Should().BeFalse();
        File.Exists(work.File("clip.alpha.ivf")).Should().BeFalse();
    }

    [Fact]
    public void The_master_sound_is_the_sources_sound_bit_for_bit()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-flac-exact");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringResult result = CbvAuthor.Write(MasterRequest(source, work.File("clip.cbvmaster"), work));
        string flac = work.File("extracted.flac");

        //Act
        using (CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath)))
        using (FileStream file = File.Create(flac))
        {
            MediaTrackInfo audio = reader.Tracks[2];
            file.Write(audio.CodecPrivate.Span);
            while (reader.TryReadPacket(out MediaPacket packet))
            {
                if (packet.TrackId == audio.Id) file.Write(packet.Data.Span);
            }
        }

        byte[] authored = SyntheticAlphaSource.DecodeSound(flac, work.File("authored.raw"));
        byte[] original = SyntheticAlphaSource.DecodeSound(source, work.File("original.raw"));

        //Assert
        authored.Length.Should().Be(original.Length);
        authored.Should().Equal(original);
    }

    [Fact]
    public void A_frame_size_runs_the_prepare_pass_and_the_file_comes_out_at_that_size()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-prepare");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Video.FrameSize = AuthoringFrameSize.Exact(96, 54);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Commands.Count.Should().Be(3);
        result.Commands[0].Label.Should().Be("prepare pass");
        result.Commands[0].RunsFFmpeg.Should().BeTrue();
        result.Commands[1].Arguments.Should().Contain("clip.prepared.mkv");
        reader.Tracks[0].Width.Should().Be(96);
        reader.Tracks[0].Height.Should().Be(54);
        reader.AlphaPlaneTrack.Width.Should().Be(96);
        File.Exists(work.File("clip.prepared.mkv")).Should().BeFalse();
    }

    [Fact]
    public void Transparent_pixels_take_the_colour_of_the_visible_picture_beside_them()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-edge-fill");
        string source = SyntheticAlphaSource.WriteEdgeClip(work.File("edge.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;
        request.Video.ConstantRateFactor = 20;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        (int frames, int leftLuma) = DecodeLeftQuarterLuma(result.OutputPath);

        //Assert
        frames.Should().BeGreaterThan(0);
        leftLuma.Should().BeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public void With_the_fill_off_transparent_pixels_keep_the_sources_colour()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-edge-nofill");
        string source = SyntheticAlphaSource.WriteEdgeClip(work.File("edge.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;
        request.Video.ConstantRateFactor = 20;
        request.Video.FillTransparentPixels = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        (int frames, int leftLuma) = DecodeLeftQuarterLuma(result.OutputPath);

        //Assert
        frames.Should().BeGreaterThan(0);
        leftLuma.Should().BeLessThanOrEqualTo(60);
        result.Commands[0].Arguments.Should().NotContain("fill-transparent-pixels");
    }

    [Fact]
    public void A_rotated_source_is_turned_the_right_way_up_by_the_prepare_pass()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-rotated");
        string source = SyntheticAlphaSource.WriteRotatedClip(work.File("rotated.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Commands[0].Label.Should().Be("prepare pass");
        reader.Tracks[0].Width.Should().Be(SyntheticSource.Height);
        reader.Tracks[0].Height.Should().Be(SyntheticSource.Width);
        reader.AlphaPlaneTrack.Width.Should().Be(SyntheticSource.Height);
    }

    [Fact]
    public void A_rotated_source_without_auto_rotate_is_encoded_as_stored_with_no_prepare_pass()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-rotated-stored");
        string source = SyntheticAlphaSource.WriteRotatedClip(work.File("rotated.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;
        request.Video.AutoRotate = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Commands[0].Label.Should().Be("video pass");
        reader.Tracks[0].Width.Should().Be(SyntheticSource.Width);
    }

    [Theory]
    [InlineData(129, 72, 128, 72, "the last column was cropped away")]
    [InlineData(128, 73, 128, 72, "the last row was cropped away")]
    [InlineData(129, 73, 128, 72, "the last column and the last row were cropped away")]
    public void An_odd_sized_source_is_cropped_to_even_in_both_planes_and_says_so(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight,
        string expectedNote)
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-odd-" + width + "x" + height);
        string source = SyntheticAlphaSource.WriteClip(work.File("odd.mkv"), width, height);
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        AlphaTally tally = PlayThrough(result.OutputPath);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        string notes = string.Join("\n", result.Notes);
        notes.Should().Contain(expectedNote);
        notes.Should().Contain("is " + width + "x" + height + ", and 4:2:0 AV1 needs even dimensions");
        notes.Should().Contain("encoded at " + expectedWidth + "x" + expectedHeight);
        result.Commands[0].Arguments.Should().Contain(
            "size " + expectedWidth + "x" + expectedHeight + " cropped-from " + width + "x" + height);
        result.Commands[1].Arguments.Should().Contain("size " + expectedWidth + "x" + expectedHeight);
        reader.Tracks[0].Width.Should().Be(expectedWidth);
        reader.Tracks[0].Height.Should().Be(expectedHeight);
        reader.AlphaPlaneTrack.Width.Should().Be(expectedWidth);
        reader.AlphaPlaneTrack.Height.Should().Be(expectedHeight);
        tally.Failure.Should().BeNull();
        tally.FramesWithoutAlpha.Should().Be(0);
        tally.Frames.Should().BeGreaterThanOrEqualTo(15);
    }

    [Fact]
    public void An_even_sized_source_is_neither_cropped_nor_noted()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-even");
        string source = SyntheticAlphaSource.WriteClip(work.File("even.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Audio.Include = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        string.Join("\n", result.Notes).Should().NotContain("cropped");
        result.Commands[0].Arguments.Should().Contain("size 128x72 good-quality");
    }

    [Fact]
    public void A_dry_run_with_an_exact_frame_size_lists_that_size()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[1].Arguments.Should().StartWith("av1 i420 8-bit size 640x360 ");
        commands[2].Arguments.Should().StartWith("av1 gray 8-bit size 640x360 ");
    }

    [Fact]
    public void A_cancelled_master_run_leaves_neither_the_output_nor_its_intermediates()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-cancel");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        using CancellationTokenSource cancel = new CancellationTokenSource();
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.CancellationToken = cancel.Token;
        request.ProgressCallback = progress =>
        {
            if (progress.Label == "video pass") cancel.Cancel();
        };

        //Act
        Action write = () => CbvAuthor.Write(request);

        //Assert
        write.Should().Throw<OperationCanceledException>();
        File.Exists(work.File("clip.cbvmaster")).Should().BeFalse();
        File.Exists(work.File("clip.video.ivf")).Should().BeFalse();
        File.Exists(work.File("clip.alpha.ivf")).Should().BeFalse();
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

    #region Silent

    [Fact]
    public void A_silent_master_dry_run_lists_silent_where_the_audio_pass_would_be()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Audio.Silent = true;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(3);
        commands[2].Label.Should().Be("audio pass");
        commands[2].Arguments.Should().StartWith("silent");
        commands[2].RunsFFmpeg.Should().BeFalse();
    }

    [Fact]
    public void A_silent_master_is_not_held_to_the_flac_codec_rule()
    {
        //Arrange - the codec names a sound that is never encoded, so it is not judged
        VideoAuthoringRequest request = DryRequest();
        request.Audio.Silent = true;
        request.Audio.Codec = AuthoringAudioCodec.LibOpus;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[2].Arguments.Should().StartWith("silent");
    }

    [Theory]
    [InlineData(VideoAuthoringFlavour.Bespoke, "bespoke (Mode2)")]
    [InlineData(VideoAuthoringFlavour.WebMProfile, "WebM-profile (Mode1)")]
    public void Silent_on_a_flavour_other_than_master_is_refused_up_front(VideoAuthoringFlavour flavour, string named)
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Flavour = flavour;
        request.OutputPath = "/nowhere/clip.cbv";
        request.Audio.Silent = true;

        //Act
        Action render = () => CbvAuthor.RenderCommands(request);
        Action write = () => CbvAuthor.Write(request);

        //Assert
        render.Should().Throw<VideoAuthoringException>().WithMessage("*Silent is a master-file flag*" + named + "*");
        write.Should().Throw<VideoAuthoringException>().WithMessage("*Silent is a master-file flag*");
    }

    [Fact]
    public void A_silent_master_has_no_audio_track_carries_the_flag_and_plays_silent()
    {
        //Arrange
        SkipWithoutMasterTools();
        using WorkFolder work = new WorkFolder("master-silent");
        string source = SyntheticAlphaSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = MasterRequest(source, work.File("clip.cbvmaster"), work);
        request.Video.Alpha = AuthoringAlphaMode.Exclude;
        request.Audio.Silent = true;
        request.Title = "Silent master";

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        using VideoPlaybackSession session = NewSession();
        session.Open(result.OutputPath);

        //Assert
        result.Commands.Count.Should().Be(2);
        result.Commands[1].Label.Should().Be("audio pass");
        result.Commands[1].Arguments.Should().StartWith("silent");
        result.Mux.AudioTrackId.Should().Be(0);
        result.Mux.FormatVersion.Should().Be(CbvFormat.MasterVersion);
        reader.IsSilent.Should().BeTrue();
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.Tracks.Count.Should().Be(1);
        reader.Tracks[0].Kind.Should().Be(MediaTrackKind.Video);
        session.IsSilent.Should().BeTrue();
        session.AudioTrack.Should().BeNull();
        string.Join("\n", result.Notes).Should().Contain(
            "Title has no field in the bespoke container: the master (Mode3) file");
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

    [Fact]
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
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        return request;
    }

    // The master flavour itself needs only the engine - ffmpeg and ffprobe here - and encodes in process. The
    // opaque synthetic source is written with an AV1 encoder, which SkipWithoutFFmpeg also checks for.
    private static void SkipWithoutMasterTools() => SyntheticSource.SkipWithoutFFmpeg();

    private static (int Frames, int Luma) DecodeLeftQuarterLuma(string path)
    {
        int frames = 0;
        long total = 0;
        long samples = 0;

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
                    frames++;
                    for (int y = 0; y < frame.Height; y++)
                    {
                        ReadOnlySpan<byte> row = frame.Y.GetRowBytes(y);
                        for (int x = 0; x < frame.Width / 4; x++)
                        {
                            total += row[x];
                            samples++;
                        }
                    }
                }
            }

            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(5);
        }

        return (frames, samples == 0 ? 0 : (int)(total / samples));
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
