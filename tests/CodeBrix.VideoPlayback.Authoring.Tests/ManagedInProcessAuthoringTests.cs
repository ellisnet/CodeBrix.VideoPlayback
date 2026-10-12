using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The in-process flavours on an engine WITHOUT FFmpeg - a <see cref="SyntheticEngine" /> standing in for
/// Android's - asked for a frame size, a frame rate, a colour grade and a rotated source turned upright: what used
/// to be refused is now served in process by the core's managed processing primitives, in every flavour that has
/// an in-process path (master, Mode2, Mode1). Nothing here needs FFmpeg, except the last comparison, which
/// authors the same request both ways.
/// </summary>
public class ManagedInProcessAuthoringTests
{
    #region Dry runs

    [Theory]
    [InlineData(VideoAuthoringFlavour.Master)]
    [InlineData(VideoAuthoringFlavour.Bespoke)]
    [InlineData(VideoAuthoringFlavour.WebMProfile)]
    public void A_dry_run_on_an_engine_without_ffmpeg_lists_the_in_process_steps_and_no_ffmpeg_command(
        VideoAuthoringFlavour flavour)
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(flavour);
        request.Video.FrameSize = AuthoringFrameSize.Exact(96, 54);
        request.Video.FrameRate = 5;
        request.Video.Luts.Add(new AuthoringLutInput(AuthoringTestAssets.Lut("warm_33.cube")));
        IReadOnlyList<AuthoringCommand> commands;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            commands = CbvAuthor.RenderCommands(request);
        }

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].Arguments.Should().Be("scale 96x54 lanczos3");
        commands[1].Arguments.Should().Be("fps 5/1 drop/dup");
        commands[2].Arguments.Should().Be("grade \"warm_33.cube at 100%\" tetrahedral");
        commands.Take(3).All(c => !c.RunsFFmpeg).Should().BeTrue();
        commands[3].Label.Should().Be("video pass");
        commands[3].Arguments.Should().Contain("<- /nowhere/source.mov ->");
        commands.Any(c => c.RunsFFmpeg).Should().BeFalse();
    }

    [Fact]
    public void An_aspect_preserving_size_is_listed_by_its_rule_until_the_source_is_read()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.FrameSize = AuthoringFrameSize.LongSide(720);
        request.Video.ScalerFlags = "bilinear";
        IReadOnlyList<AuthoringCommand> commands;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            commands = CbvAuthor.RenderCommands(request);
        }

        //Assert
        commands[0].Arguments.Should().Be("scale long-side-720 bilinear");
        commands[1].Label.Should().Be("video pass");
    }

    [Fact]
    public void The_ffmpeg_engine_keeps_its_prepare_pass_as_the_default()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.FrameSize = AuthoringFrameSize.Exact(96, 54);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].RunsFFmpeg.Should().BeTrue();
        commands[0].Arguments.Should().Contain("scale=w=96:h=54:flags=lanczos");
    }

    #endregion

    #region Writing without FFmpeg

    [Fact]
    public void A_master_on_an_engine_without_ffmpeg_is_turned_scaled_rate_converted_and_graded_in_process()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-master");
        SyntheticClip clip = new SyntheticClip(128, 72, alpha: true, rotationDegrees: 90);
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        request.Video.FrameSize = AuthoringFrameSize.LongSide(96);
        request.Video.FrameRate = 5;
        request.Video.FrameRateMode = AuthoringFrameRateMode.Filter;
        request.Video.Luts.Add(new AuthoringLutInput(AuthoringTestAssets.Lut("warm_33.cube")));
        SyntheticEngine engine = new SyntheticEngine(clip);
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(engine))
        {
            result = CbvAuthor.Write(request);
        }

        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        result.Commands.Select(c => c.Label + ": " + c.Arguments).Take(4).Should().Equal(
            "prepare pass: rotate 90",
            "prepare pass: scale 54x96 lanczos3",
            "prepare pass: fps 5/1 drop/dup",
            "prepare pass: grade \"warm_33.cube at 100%\" 33-node tetrahedral");
        result.Commands.Any(c => c.RunsFFmpeg).Should().BeFalse();
        result.Commands[4].Arguments.Should().StartWith("av1 i420 8-bit size 54x96");
        reader.Tracks[0].Width.Should().Be(54);
        reader.Tracks[0].Height.Should().Be(96);
        reader.Tracks[0].DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(200));
        reader.AlphaPlaneTrack.Width.Should().Be(54);
        reader.AlphaPlaneTrack.Height.Should().Be(96);
        MasterFrameDecoder.Decode(result.OutputPath, out List<ShapedFrames.Snapshot> alpha).Count.Should().Be(10);
        alpha.Count.Should().Be(10);
        engine.VideoOpens.Should().Be(2);
    }

    [Fact]
    public void A_bespoke_file_on_an_engine_without_ffmpeg_is_shaped_in_process_instead_of_refused()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-bespoke");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Bespoke, work, "clip.cbv");
        request.Video.FrameSize = AuthoringFrameSize.Exact(96, 54);
        request.Video.FrameRate = 20;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            result = CbvAuthor.Write(request);
        }

        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);

        //Assert
        result.Commands[0].Arguments.Should().Be("scale 96x54 lanczos3");
        result.Commands[1].Arguments.Should().Be("fps 20/1 drop/dup");
        reader.Tracks[0].Width.Should().Be(96);
        reader.Tracks[0].Height.Should().Be(54);
        reader.Tracks[0].DefaultDuration.Should().Be(TimeSpan.FromMilliseconds(50));
        result.Notes.Should().Contain(note => note.StartsWith("FrameRateMode = Encoder has no separate in-process meaning"));
    }

    [Fact]
    public void A_webm_profile_file_on_an_engine_without_ffmpeg_is_shaped_in_process_instead_of_refused()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-webm");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.WebMProfile, work, "clip.webm");
        request.Video.FrameSize = AuthoringFrameSize.ShortSide(48);
        request.Video.Luts.Add(new AuthoringLutInput(AuthoringTestAssets.Lut("cool_33.cube"), 50));
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72, rotationDegrees: 180))))
        {
            result = CbvAuthor.Write(request);
        }

        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);

        //Assert
        result.Commands[0].Arguments.Should().Be("rotate 180");
        result.Commands[1].Arguments.Should().Be("scale 86x48 lanczos3");
        result.Commands[2].Arguments.Should().StartWith("grade \"cool_33.cube at 50%\" ");
        reader.Tracks.First(t => t.Kind == MediaTrackKind.Video).Width.Should().Be(86);
        result.Profile.Passes.Should().BeTrue();
    }

    [Theory]
    [InlineData("bicubic", "names no resampler the in-process scaler has")]
    [InlineData("lanczos+accurate_rnd", "'accurate_rnd' steer FFmpeg's scaler only and were ignored in process")]
    public void Scaler_flags_with_no_in_process_meaning_are_said_to_be_ignored(string flags, string expected)
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-flags");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        request.Video.FrameSize = AuthoringFrameSize.Exact(64, 36);
        request.Video.ScalerFlags = flags;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        result.Notes.Should().Contain(note => note.Contains(expected));
    }

    [Fact]
    public void An_odd_sized_rotated_source_is_refused_with_the_rotators_reason()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-odd-rotated");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        Action act;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(127, 72, rotationDegrees: 270))))
        {
            act = () => CbvAuthor.Write(request);

            //Assert
            act.Should().Throw<VideoAuthoringException>().WithMessage("*width 127 is odd*");
        }

        System.IO.File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void A_rotated_source_without_auto_rotate_needs_no_shaping_at_all()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-no-autorotate");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        request.Video.AutoRotate = false;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72, rotationDegrees: 90))))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        result.Commands[0].Label.Should().Be("video pass");
        result.Commands[0].Arguments.Should().Contain("size 128x72");
    }

    #endregion

    #region Master notes

    [Fact]
    public void A_master_says_that_copy_source_metadata_and_implicit_stream_selection_have_no_meaning_for_it()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-master-notes");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        request.CopySourceMetadata = true;
        request.SelectStreamsExplicitly = false;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        result.Notes.Should().Contain(note => note.StartsWith("CopySourceMetadata has no in-process meaning")
            && note.Contains("The bespoke container has no file-level metadata"));
        result.Notes.Should().Contain(note => note.StartsWith("SelectStreamsExplicitly = false has no in-process meaning"));
    }

    [Fact]
    public void A_master_with_the_default_stream_settings_earns_neither_note()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-master-no-notes");
        VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, "clip.cbvmaster");
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72))))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        result.Notes.Should().NotContain(note => note.StartsWith("CopySourceMetadata"));
        result.Notes.Should().NotContain(note => note.StartsWith("SelectStreamsExplicitly"));
    }

    #endregion

    #region Against the ffmpeg path

    [Fact]
    public void The_managed_master_decodes_within_the_shaping_bounds_of_the_ffmpeg_master_of_the_same_request()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("managed-vs-ffmpeg-master");
        SyntheticClip clip = new SyntheticClip(128, 72, alpha: true, rotationDegrees: 90);
        string source = clip.WriteForFFmpeg(work.File("clip.mkv"));

        VideoAuthoringRequest Request(string output)
        {
            VideoAuthoringRequest request = WriteRequest(VideoAuthoringFlavour.Master, work, output);
            request.SourcePath = source;
            request.Audio.Include = false;
            request.Video.ConstantRateFactor = 0;
            request.Video.FillTransparentPixels = false;
            request.Video.FrameSize = AuthoringFrameSize.LongSide(96);
            request.Video.FrameRate = 15;
            request.Video.FrameRateMode = AuthoringFrameRateMode.Filter;
            request.Video.Luts.Add(new AuthoringLutInput(AuthoringTestAssets.Lut("warm_33.cube")));
            return request;
        }

        //Act
        VideoAuthoringResult ffmpeg = CbvAuthor.Write(Request("ffmpeg.cbvmaster"));
        VideoAuthoringResult managed;
        using (EngineScope.Use(new SyntheticEngine(clip, hasSound: false)))
        {
            managed = CbvAuthor.Write(Request("managed.cbvmaster"));
        }

        List<ShapedFrames.Snapshot> ffmpegFrames = MasterFrameDecoder.Decode(ffmpeg.OutputPath, out List<ShapedFrames.Snapshot> ffmpegAlpha);
        List<ShapedFrames.Snapshot> managedFrames = MasterFrameDecoder.Decode(managed.OutputPath, out List<ShapedFrames.Snapshot> managedAlpha);
        ShapedFrames.Difference picture = ShapedFrames.Compare(managedFrames, ffmpegFrames);
        ShapedFrames.Difference alpha = ShapedFrames.Compare(managedAlpha, ffmpegAlpha);

        //Assert
        ffmpeg.Commands[0].RunsFFmpeg.Should().BeTrue();
        managed.Commands.Any(c => c.RunsFFmpeg).Should().BeFalse();
        managedFrames.Count.Should().Be(30);
        managedFrames.Count.Should().Be(ffmpegFrames.Count);
        for (int i = 0; i < managedFrames.Count; i++) managedFrames[i].Timestamp.Should().Be(ffmpegFrames[i].Timestamp);
        picture.LumaWorst.Should().BeLessThanOrEqualTo(6, picture.ToString());
        picture.ChromaWorst.Should().BeLessThanOrEqualTo(6, picture.ToString());
        picture.LumaMean.Should().BeLessThanOrEqualTo(1.8d, picture.ToString());
        alpha.LumaWorst.Should().BeLessThanOrEqualTo(3, alpha.ToString());
    }

    #endregion

    #region Units

    [Theory]
    [InlineData(30d, 30, 1)]
    [InlineData(29.97d, 2997, 100)]
    [InlineData(23.976d, 2997, 125)]
    [InlineData(12.5d, 25, 2)]
    [InlineData(59.94d, 2997, 50)]
    public void A_decimal_frame_rate_becomes_the_fraction_ffmpeg_makes_of_it(double rate, int numerator, int denominator)
    {
        //Arrange
        double value = rate;

        //Act
        ManagedFrameShaping.ToRational(value, out int actualNumerator, out int actualDenominator);

        //Assert
        actualNumerator.Should().Be(numerator);
        actualDenominator.Should().Be(denominator);
    }

    [Theory]
    [InlineData(AuthoringFrameSizeKind.LongSide, 720, 1920, 1080, 720, 406)]
    [InlineData(AuthoringFrameSizeKind.LongSide, 720, 1080, 1920, 406, 720)]
    [InlineData(AuthoringFrameSizeKind.ShortSide, 48, 128, 72, 86, 48)]
    [InlineData(AuthoringFrameSizeKind.ShortSide, 48, 72, 128, 48, 86)]
    [InlineData(AuthoringFrameSizeKind.LongSide, 64, 128, 72, 64, 36)]
    public void An_aspect_preserving_size_rounds_the_free_side_to_the_nearest_pair_as_ffmpeg_does(
        AuthoringFrameSizeKind kind,
        int pixels,
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.FrameSize = kind == AuthoringFrameSizeKind.LongSide
            ? AuthoringFrameSize.LongSide(pixels)
            : AuthoringFrameSize.ShortSide(pixels);
        ManagedFrameShaping shaping = ManagedFrameShaping.Create(request, ResolvedLutChain.None, false);

        //Act
        shaping.TargetSize(width, height, out int targetWidth, out int targetHeight);

        //Assert
        targetWidth.Should().Be(expectedWidth);
        targetHeight.Should().Be(expectedHeight);
    }

    [Theory]
    [InlineData(90, RotationAngle.Clockwise90)]
    [InlineData(180, RotationAngle.Rotate180)]
    [InlineData(270, RotationAngle.Clockwise270)]
    [InlineData(0, RotationAngle.None)]
    public void The_engines_clockwise_rotation_is_the_turn_applied(int degrees, RotationAngle expected)
    {
        //Arrange
        ManagedFrameShaping shaping = ManagedFrameShaping.Create(DryRequest(VideoAuthoringFlavour.Master), ResolvedLutChain.None, false);

        //Act
        RotationAngle angle = shaping.RotationFor(degrees);

        //Assert
        angle.Should().Be(expected);
    }

    [Theory]
    [InlineData(VideoProcessing.Core.Media.VideoColorMatrix.Bt709, Decoding.VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoProcessing.Core.Media.VideoColorMatrix.Bt601, Decoding.VideoMatrixCoefficients.Smpte170M)]
    [InlineData(VideoProcessing.Core.Media.VideoColorMatrix.Unspecified, Decoding.VideoMatrixCoefficients.Smpte170M)]
    public void The_grade_converts_with_the_engines_matrix_and_reads_an_untagged_picture_as_bt601(
        VideoProcessing.Core.Media.VideoColorMatrix matrix,
        Decoding.VideoMatrixCoefficients expected)
    {
        //Arrange
        VideoProcessing.Core.Media.VideoColorMatrix delivered = matrix;

        //Act
        Decoding.VideoMatrixCoefficients chosen = ShapedVideoFrameSource.GradeMatrixFor(delivered);

        //Assert
        chosen.Should().Be(expected);
    }

    [Fact]
    public void A_bt2020_picture_cannot_be_graded_in_process_and_says_where_it_can()
    {
        //Arrange
        Action act = () => ShapedVideoFrameSource.GradeMatrixFor(VideoProcessing.Core.Media.VideoColorMatrix.Bt2020NonConstant);

        //Act
        //Assert
        act.Should().Throw<VideoAuthoringException>().WithMessage("*BT.709 and BT.601*ffmpeg engine*");
    }

    #endregion

    private static VideoAuthoringRequest DryRequest(VideoAuthoringFlavour flavour)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = "/nowhere/source.mov",
            OutputPath = flavour == VideoAuthoringFlavour.Master ? "/nowhere/clip.cbvmaster" : "/nowhere/clip.cbv",
            TemporaryFolder = "/nowhere/work",
        };

        if (flavour != VideoAuthoringFlavour.Master) request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        return request;
    }

    // The synthetic engine ignores the path, but the library insists that a source file exists before it runs.
    private static VideoAuthoringRequest WriteRequest(VideoAuthoringFlavour flavour, WorkFolder work, string output)
    {
        string source = work.File("synthetic-source.bin");
        System.IO.File.WriteAllBytes(source, new byte[] { 0 });

        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = work.File(output),
            TemporaryFolder = work.Path,
        };

        if (flavour != VideoAuthoringFlavour.Master) request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        return request;
    }
}
