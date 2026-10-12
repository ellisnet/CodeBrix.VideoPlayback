using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoProcessing.Core.Tools;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The tool check and the ffmpeg-path decision, judged through the REGISTERED engine's external tools - here a fake
/// facet on the synthetic engine, so nothing depends on what this machine has installed - and the refusal every
/// entry point gives when no engine is registered at all.
/// </summary>
public class AuthoringToolsTests
{
    #region The tool check

    [Fact]
    public void Both_tools_found_by_the_engine_pass_the_check()
    {
        //Arrange
        SyntheticEngine engine = EngineWith(new FakeExternalTools());

        //Act
        bool verified;
        string problem;
        using (EngineScope.Use(engine))
        {
            verified = CbvAuthor.TryVerifyTools(out problem);
        }

        //Assert
        verified.Should().BeTrue();
        problem.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_ffmpeg_is_named_with_where_the_engine_looked_for_it()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools
        {
            FFmpegFound = false,
            LookedIn = "in '/opt/ff/x64', then in '/opt/ff', and then on the PATH",
        };
        SyntheticEngine engine = EngineWith(tools);

        //Act
        bool verified;
        string problem;
        using (EngineScope.Use(engine))
        {
            verified = CbvAuthor.TryVerifyTools(out problem);
        }

        //Assert
        verified.Should().BeFalse();
        problem.Should().StartWith(
            "Authoring needs 'ffmpeg' and 'ffprobe', and 'ffmpeg' was not found - the engine looked for it in "
            + "'/opt/ff/x64', then in '/opt/ff', and then on the PATH. They are the ONE external tool");
        problem.Should().NotContain("'ffprobe' was not found");
        problem.Should().Contain("GlobalFFOptions.Configure(o => o.BinaryFolder = ...)");
    }

    [Fact]
    public void Both_tools_missing_are_both_named()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools { FFmpegFound = false, FFprobeFound = false, LookedIn = "on the PATH" };

        //Act
        bool verified = AuthoringTools.TryVerify(EngineWith(tools), out IExternalTools found, out string problem);

        //Assert
        verified.Should().BeFalse();
        found.Should().BeSameAs(tools);
        problem.Should().Contain(
            "'ffmpeg' was not found - the engine looked for it on the PATH; 'ffprobe' was not found - the engine "
            + "looked for it on the PATH.");
    }

    [Fact]
    public void An_engine_without_external_tools_is_told_what_it_lacks()
    {
        //Arrange
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), "phone");

        //Act
        bool verified;
        string problem;
        using (EngineScope.Use(engine))
        {
            verified = CbvAuthor.TryVerifyTools(out problem);
        }

        //Assert
        verified.Should().BeFalse();
        problem.Should().Contain("the 'phone' engine has none");
        problem.Should().Contain("VideoProcessorCapabilities.ExternalTools");
    }

    [Fact]
    public void The_encoder_warnings_come_from_the_engines_encoder_listing()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.Encoders.Remove("libsvtav1");
        SyntheticEngine engine = EngineWith(tools);

        //Act
        bool verified;
        IReadOnlyList<string> warnings;
        using (EngineScope.Use(engine))
        {
            verified = CbvAuthor.TryVerifyTools(out string _, out warnings);
        }

        //Assert
        verified.Should().BeTrue();
        warnings.Count.Should().Be(1);
        warnings[0].Should().StartWith(AuthoringTools.MissingSvtAv1);
        warnings[0].Should().Contain("AllowAv1EncoderFallback = true on the VideoAuthoringRequest");
        warnings[0].Should().NotContain("GlobalFFOptions");
    }

    #endregion

    #region The ffmpeg-path decision

    [Fact]
    public void An_engine_with_tools_that_finds_both_takes_the_ffmpeg_path()
    {
        //Arrange
        VideoAuthoringRequest request = FFmpegEncoderRequest();
        bool ffmpeg;
        string reason;

        //Act
        using (EngineScope.Use(EngineWith(new FakeExternalTools())))
        {
            ffmpeg = InProcessAuthoring.UsesFFmpegPath(request, true, out reason);
        }

        //Assert
        ffmpeg.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void An_engine_without_tools_falls_back_in_process_saying_which_engine_it_is()
    {
        //Arrange
        VideoAuthoringRequest request = FFmpegEncoderRequest();
        bool ffmpeg;
        string reason;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72), "phone")))
        {
            ffmpeg = InProcessAuthoring.UsesFFmpegPath(request, true, out reason);
        }

        //Assert
        ffmpeg.Should().BeFalse();
        reason.Should().StartWith(
            "the request asked for libsvtav1 through ffmpeg, and the registered video-processing engine is 'phone', "
            + "not ffmpeg, so the picture and the sound were encoded in process instead");
    }

    [Fact]
    public void A_missing_tool_falls_back_in_a_real_run_but_not_in_a_dry_run()
    {
        //Arrange
        VideoAuthoringRequest request = FFmpegEncoderRequest();
        bool real;
        bool dry;
        string reason;

        //Act
        using (EngineScope.Use(EngineWith(new FakeExternalTools { FFprobeFound = false })))
        {
            real = InProcessAuthoring.UsesFFmpegPath(request, true, out reason);
            dry = InProcessAuthoring.UsesFFmpegPath(request, false, out _);
        }

        //Assert
        real.Should().BeFalse();
        reason.Should().Contain("ffmpeg and ffprobe cannot be run on this machine");
        dry.Should().BeTrue();
    }

    [Fact]
    public void An_engine_with_tools_counts_as_the_ffmpeg_engine_and_one_without_does_not()
    {
        //Arrange
        SyntheticEngine with = EngineWith(new FakeExternalTools());
        SyntheticEngine without = new SyntheticEngine(new SyntheticClip(128, 72));

        //Act
        bool withTools = InProcessAuthoring.IsFFmpegEngine(with);
        bool withoutTools = InProcessAuthoring.IsFFmpegEngine(without);

        //Assert
        withTools.Should().BeTrue();
        withoutTools.Should().BeFalse();
    }

    #endregion

    #region Nothing registered

    [Fact]
    public void With_nothing_registered_the_master_flavour_is_refused()
    {
        //Arrange
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = "/clips/source.mkv",
            OutputPath = "/out/clip.cbvmaster",
        };

        //Act
        VideoAuthoringException dry;
        VideoAuthoringException real;
        using (NothingRegisteredScope.Begin())
        {
            dry = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.RenderCommands(request));
            real = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(WithExistingSource(request)));
        }

        //Assert
        dry.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
        real.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
    }

    [Fact]
    public void With_nothing_registered_the_bespoke_flavour_encoded_in_process_is_refused()
    {
        //Arrange
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = "/clips/source.mkv",
            OutputPath = "/out/clip.cbv",
        };
        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;

        //Act
        VideoAuthoringException dry;
        VideoAuthoringException real;
        using (NothingRegisteredScope.Begin())
        {
            dry = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.RenderCommands(request));
            real = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(WithExistingSource(request)));
        }

        //Assert
        dry.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
        real.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
    }

    [Fact]
    public void With_nothing_registered_the_webm_profile_flavour_is_refused_and_so_is_the_tool_check()
    {
        //Arrange
        VideoAuthoringRequest request = FFmpegEncoderRequest();
        request.Flavour = VideoAuthoringFlavour.WebMProfile;

        //Act
        VideoAuthoringException dry;
        VideoAuthoringException real;
        bool verified;
        string problem;
        using (NothingRegisteredScope.Begin())
        {
            dry = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.RenderCommands(request));
            real = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(WithExistingSource(request)));
            verified = CbvAuthor.TryVerifyTools(out problem);
        }

        //Assert
        dry.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
        real.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
        verified.Should().BeFalse();
        problem.Should().Be(InProcessAuthoring.NoEngineRegistered);
    }

    [Fact]
    public void With_nothing_registered_the_export_is_refused()
    {
        //Arrange
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = AuthoringTestAssets.Path_("av1-flac.cbvmaster"),
            OutputPath = "/nowhere/out.mp4",
        };

        //Act
        VideoAuthoringException dry;
        VideoAuthoringException real;
        using (NothingRegisteredScope.Begin())
        {
            dry = Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(request));
            real = Assert.Throws<VideoAuthoringException>(() => VideoExporter.Run(request));
        }

        //Assert
        dry.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
        real.Message.Should().Be(InProcessAuthoring.NoEngineRegistered);
    }

    [Fact]
    public void The_refusal_names_both_registration_calls()
    {
        //Arrange
        string message = InProcessAuthoring.NoEngineRegistered;

        //Act
        bool desktop = message.Contains("CodeBrixVideoProcessing.Register()");
        bool android = message.Contains("CodeBrixVideoProcessingAndroid.Register()");

        //Assert
        desktop.Should().BeTrue();
        android.Should().BeTrue();
        message.Should().StartWith("Authoring decodes through the registered video-processing engine and none is registered.");
    }

    #endregion

    private static SyntheticEngine EngineWith(FakeExternalTools tools) =>
        new SyntheticEngine(new SyntheticClip(128, 72), "fake-ffmpeg", tools: tools);

    private static VideoAuthoringRequest FFmpegEncoderRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = "/clips/source.mkv",
            OutputPath = "/out/clip.cbv",
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        return request;
    }

    // Write checks the source exists before it looks for an engine; the test asset folder always has this file.
    private static VideoAuthoringRequest WithExistingSource(VideoAuthoringRequest request)
    {
        request.SourcePath = AuthoringTestAssets.Path_("av1-flac.cbvmaster");
        return request;
    }
}
