using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The ffmpeg vocabulary with no ffmpeg anywhere: the progress parse, the encoder-listing parse, the libsvtav1 to
/// libaom-av1 rewrite, and a pass run end to end through a fake external-tools facet on the synthetic engine -
/// progress, the line recorded after a fallback, and a failure's message.
/// </summary>
public class FFmpegVocabularyTests
{
    private static readonly TimeSpan TwoSeconds = TimeSpan.FromSeconds(2);

    #region Progress

    [Theory]
    [InlineData("frame=   12 fps=0.0 q=0.0 size=       0kB time=00:00:00.50 bitrate=N/A speed=   1x", 5000000L)]
    [InlineData("size=     256kB time=01:02:03.04 bitrate= 1.2kbits/s", 37230400000L)]
    [InlineData("time=00:00:01.5 bitrate=N/A", 15000000L)]
    public void A_progress_line_yields_the_time_it_reached(string line, long ticks)
    {
        //Arrange
        string text = line;

        //Act
        bool parsed = FFmpegVocabulary.TryParseProgressTime(text, out TimeSpan time);

        //Assert
        parsed.Should().BeTrue();
        time.Ticks.Should().Be(ticks);
    }

    [Theory]
    [InlineData("Stream mapping:")]
    [InlineData("frame=    0 fps=0.0 q=0.0 size=N/A time=N/A bitrate=N/A")]
    [InlineData("")]
    public void A_line_without_a_time_yields_none(string line)
    {
        //Arrange
        string text = line;

        //Act
        bool parsed = FFmpegVocabulary.TryParseProgressTime(text, out TimeSpan time);

        //Assert
        parsed.Should().BeFalse();
        time.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Progress_is_reported_in_whole_percents_in_order_and_never_backwards_then_100_at_the_end()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.ErrorLines.AddRange(new[]
        {
            "Input #0, matroska,webm, from 'source.mkv':",
            "frame=    5 time=00:00:00.25 bitrate=N/A",
            "frame=    6 time=00:00:00.26 bitrate=N/A",
            "frame=   10 time=00:00:00.50 bitrate=N/A",
            "frame=    8 time=00:00:00.40 bitrate=N/A",
            "frame=   30 time=00:00:01.53 bitrate=N/A",
        });
        List<AuthoringProgress> reported = new List<AuthoringProgress>();
        VideoAuthoringRequest request = DryWebMRequest();
        request.SourceDuration = TwoSeconds;
        request.ProgressCallback = reported.Add;
        List<AuthoringCommand> commands = new List<AuthoringCommand>();

        //Act
        FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            commands,
            new List<string>());

        //Assert
        List<int> percents = reported.ConvertAll(progress => progress.Percent);
        percents.Should().Equal(12, 13, 25, 76, 100);
        reported.TrueForAll(progress => progress.Label == "one pass" && progress.PassNumber == 1 && progress.PassCount == 1)
            .Should().BeTrue();
    }

    [Fact]
    public void Without_a_source_duration_nothing_is_reported()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.ErrorLines.Add("frame=   10 time=00:00:00.50 bitrate=N/A");
        List<AuthoringProgress> reported = new List<AuthoringProgress>();
        VideoAuthoringRequest request = DryWebMRequest();
        request.ProgressCallback = reported.Add;

        //Act
        FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            new List<AuthoringCommand>(),
            new List<string>());

        //Assert
        reported.Count.Should().Be(0);
    }

    #endregion

    #region The encoder listing

    [Fact]
    public void The_encoder_listing_is_parsed_into_encoder_names()
    {
        //Arrange
        string listing = "Encoders:\n V..... = Video\n A..... = Audio\n ------\n"
            + " V....D libaom-av1           libaom AV1 (codec av1)\n"
            + " V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)\r\n"
            + " A....D libopus              libopus Opus (codec opus)\n"
            + " AF...D flac                 FLAC (Free Lossless Audio Codec)\n";

        //Act
        HashSet<string> encoders = FFmpegVocabulary.ParseEncoderListing(listing);

        //Assert
        encoders.Should().Contain("libaom-av1");
        encoders.Should().Contain("libsvtav1");
        encoders.Should().Contain("libopus");
        encoders.Should().Contain("flac");
        encoders.Should().NotContain("libvorbis");
    }

    [Fact]
    public void The_encoder_listing_is_asked_of_ffmpeg_through_the_engines_tools()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.Encoders.Remove("libsvtav1");

        //Act
        HashSet<string> encoders = FFmpegVocabulary.ListEncoders(tools, TestContext.Current.CancellationToken);

        //Assert
        encoders.Should().Contain("libaom-av1");
        encoders.Should().NotContain("libsvtav1");
        tools.Runs.Count.Should().Be(1);
        tools.Runs[0].ToolName.Should().Be("ffmpeg");
        tools.Runs[0].Arguments.Render().Should().Be("-hide_banner -encoders");
    }

    #endregion

    #region The AV1 encoder fallback

    [Fact]
    public void The_fallback_rewrites_the_svtav1_line_into_the_libaom_line()
    {
        //Arrange
        VideoAuthoringRequest request = DryWebMRequest();
        request.Video.SpeedPreset = 13;
        request.Video.ConstantRateFactor = 30;
        FFmpegVocabulary.CommandLine line = FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None);
        List<string> notes = new List<string>();

        //Act
        string problem = FFmpegVocabulary.ApplyAv1EncoderFallback(line, name => name != "libsvtav1", notes);

        //Assert
        problem.Should().BeNull();
        line.Render().Should().Be(
            "-autorotate -i \"/clips/source.mkv\" -map 0:v:0 -map 0:a:0 -map_chapters -1 -map_metadata -1 "
            + "-c:v libaom-av1 -cpu-used 8 -crf 30 -b:v 0 -pix_fmt yuv420p -vf \"format=yuv420p\" "
            + "-c:a libopus -b:a 128k -ar 48000 -ac 2 -f webm -cues_to_front 1 \"/out/clip.cbv\" -y");
        notes.Should().Equal(
            "libsvtav1 (SVT-AV1) is not available in this ffmpeg, so the request's AllowAv1EncoderFallback encoded AV1 "
            + "with libaom-av1 instead.",
            "-preset 13 became -cpu-used 8, the nearest value libaom-av1 accepts.",
            "-b:v 0 was added after -crf, which libaom-av1 needs for the rate factor to set the quality.");
    }

    [Fact]
    public void The_fallback_rewrite_matches_the_line_a_libaom_request_composes_when_no_cap_is_needed()
    {
        //Arrange
        VideoAuthoringRequest svt = DryWebMRequest();
        svt.Video.SpeedPreset = 6;
        VideoAuthoringRequest aom = DryWebMRequest();
        aom.Video.Encoder = AuthoringVideoEncoder.LibAomAv1;
        aom.Video.SpeedPreset = 6;
        FFmpegVocabulary.CommandLine line = FFmpegVocabulary.BuildWebMProfile(svt, ResolvedLutChain.None);
        List<string> notes = new List<string>();

        //Act
        FFmpegVocabulary.ApplyAv1EncoderFallback(line, name => name != "libsvtav1", notes);

        //Assert
        line.Render().Should().Be(FFmpegVocabulary.BuildWebMProfile(aom, ResolvedLutChain.None).Render());
        notes[1].Should().Be("-preset 6 became -cpu-used 6.");
    }

    [Fact]
    public void A_build_that_has_svtav1_is_left_completely_alone()
    {
        //Arrange
        VideoAuthoringRequest request = DryWebMRequest();
        FFmpegVocabulary.CommandLine line = FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None);
        string before = line.Render();
        List<string> notes = new List<string>();

        //Act
        string problem = FFmpegVocabulary.ApplyAv1EncoderFallback(line, _ => true, notes);

        //Assert
        problem.Should().BeNull();
        line.Render().Should().Be(before);
        notes.Count.Should().Be(0);
    }

    [Fact]
    public void A_build_with_neither_av1_encoder_has_nothing_to_fall_back_to()
    {
        //Arrange
        VideoAuthoringRequest request = DryWebMRequest();
        FFmpegVocabulary.CommandLine line = FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None);
        string before = line.Render();
        List<string> notes = new List<string>();

        //Act
        string problem = FFmpegVocabulary.ApplyAv1EncoderFallback(
            line, name => name != "libsvtav1" && name != "libaom-av1", notes);

        //Assert
        problem.Should().Be(FFmpegVocabulary.LibAomMissingProblem);
        line.Render().Should().Be(before);
        notes.Count.Should().Be(0);
    }

    [Fact]
    public void The_recorded_command_is_the_line_that_ran_after_the_fallback()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.Encoders.Remove("libsvtav1");
        VideoAuthoringRequest request = DryWebMRequest();
        request.Video.SpeedPreset = 13;
        request.AllowAv1EncoderFallback = true;
        List<AuthoringCommand> commands = new List<AuthoringCommand>();
        List<string> notes = new List<string>();

        //Act
        FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            commands,
            notes);

        //Assert
        commands.Count.Should().Be(1);
        commands[0].Arguments.Should().Contain("-c:v libaom-av1 -cpu-used 8 -crf 30 -b:v 0");
        commands[0].Arguments.Should().NotContain("libsvtav1");
        tools.PassArguments.Should().Equal(commands[0].Arguments);
        notes.Count.Should().Be(3);
        notes.TrueForAll(note => note.StartsWith("one pass: ", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public void Without_the_requests_switch_no_encoder_listing_is_asked_for_and_nothing_is_rewritten()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        tools.Encoders.Remove("libsvtav1");
        VideoAuthoringRequest request = DryWebMRequest();
        List<AuthoringCommand> commands = new List<AuthoringCommand>();

        //Act
        FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            commands,
            new List<string>());

        //Assert
        tools.Runs.Count.Should().Be(1);
        commands[0].Arguments.Should().Contain("-c:v libsvtav1");
    }

    #endregion

    #region Failures

    [Fact]
    public void A_non_zero_exit_is_reported_with_the_line_the_exit_code_and_the_end_of_the_error_stream()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools { ExitCode = 1 };
        for (int i = 0; i < 20; i++) tools.ErrorLines.Add("line " + i);
        tools.ErrorLines.Add("[vost#0:0 @ 0x55] Unknown encoder 'libsvtav1'");
        VideoAuthoringRequest request = DryWebMRequest();
        FFmpegVocabulary.CommandLine line = FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None);
        string rendered = line.Render();
        List<AuthoringCommand> commands = new List<AuthoringCommand>();

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => FFmpegVocabulary.Run(
            tools, line, request, "one pass", 1, 1, commands, new List<string>()));

        //Assert
        failure.Message.Should().StartWith("The one pass failed. " + AuthoringTools.MissingSvtAv1 + " ");
        failure.Message.Should().Contain(AuthoringTools.SvtAv1Remedies);
        failure.Message.Should().Contain(" The command was: ffmpeg " + rendered + "\nffmpeg exited with code 1. ");
        failure.Message.Should().EndWith("line 19\n[vost#0:0 @ 0x55] Unknown encoder 'libsvtav1'");
        failure.Message.Should().NotContain("line 8\n");
        commands.Count.Should().Be(0);
    }

    [Fact]
    public void A_cancelled_pass_is_a_cancelled_pass_not_a_failure()
    {
        //Arrange
        using CancellationTokenSource cancel = new CancellationTokenSource();
        FakeExternalTools tools = new FakeExternalTools { OnRun = _ => cancel.Cancel() };
        VideoAuthoringRequest request = DryWebMRequest();
        request.CancellationToken = cancel.Token;
        List<AuthoringCommand> commands = new List<AuthoringCommand>();

        //Act
        Action run = () => FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            commands,
            new List<string>());

        //Assert
        Assert.ThrowsAny<OperationCanceledException>(run);
        commands.Count.Should().Be(0);
    }

    [Fact]
    public void The_pass_is_run_as_ffmpeg_and_is_killed_outright_when_cancelled()
    {
        //Arrange
        FakeExternalTools tools = new FakeExternalTools();
        VideoAuthoringRequest request = DryWebMRequest();

        //Act
        FFmpegVocabulary.Run(
            tools,
            FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None),
            request,
            "one pass",
            1,
            1,
            new List<AuthoringCommand>(),
            new List<string>());

        //Assert
        tools.Runs[0].ToolName.Should().Be("ffmpeg");
        tools.Runs[0].KillGracePeriod.Should().Be(TimeSpan.Zero);
        tools.Runs[0].OnStandardErrorLine.Should().NotBeNull();
    }

    [Fact]
    public void A_path_the_command_line_cannot_carry_is_refused_as_an_authoring_failure()
    {
        //Arrange
        VideoAuthoringRequest request = DryWebMRequest();
        request.OutputPath = "/out/a \"quoted\" clip.cbv";

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => FFmpegVocabulary.BuildWebMProfile(request, ResolvedLutChain.None));

        //Assert
        failure.Message.Should().StartWith("The path '/out/a \"quoted\" clip.cbv' cannot be put on an ffmpeg command line");
    }

    #endregion

    #region Through CbvAuthor, on the synthetic engine with fake tools

    [Fact]
    public void A_webm_profile_request_runs_its_one_pass_through_the_registered_engines_tools()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("fake-tools-webm");
        FakeExternalTools tools = new FakeExternalTools();
        tools.Encoders.Remove("libsvtav1");
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), "fake-ffmpeg", tools: tools);
        VideoAuthoringRequest request = RunnableWebMRequest(work);
        request.AllowAv1EncoderFallback = true;
        tools.OnRun = _ => File.WriteAllBytes(request.OutputPath, new byte[] { 1, 2, 3 });
        string dryRun;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(engine))
        {
            dryRun = CbvAuthor.RenderCommands(request)[0].Arguments;
            result = CbvAuthor.Write(request);
        }

        //Assert
        dryRun.Should().Contain("-c:v libsvtav1");
        result.Commands.Count.Should().Be(1);
        result.Commands[0].RunsFFmpeg.Should().BeTrue();
        result.Commands[0].Arguments.Should().Be(dryRun.Replace("-c:v libsvtav1 -preset 13 -crf 30", "-c:v libaom-av1 -cpu-used 8 -crf 30 -b:v 0"));
        tools.PassArguments.Should().Equal(result.Commands[0].Arguments);
        string.Join("\n", result.Notes).Should().Contain("one pass: libsvtav1 (SVT-AV1) is not available");
    }

    [Fact]
    public void The_source_survey_reads_subtitles_and_chapters_from_an_engine_with_tools()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("fake-tools-survey");
        FakeExternalTools tools = new FakeExternalTools();
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), "fake-ffmpeg", tools: tools)
        {
            SubtitleStreams = new[]
            {
                new CodeBrix.VideoProcessing.Core.Media.SubtitleStreamInfo { Index = 2, CodecName = "subrip", Language = "eng" },
            },
            Chapters = new[]
            {
                new CodeBrix.VideoProcessing.Core.Media.ChapterInfo { Index = 0, End = TimeSpan.FromSeconds(1), Title = "One" },
                new CodeBrix.VideoProcessing.Core.Media.ChapterInfo { Index = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2) },
            },
        };
        VideoAuthoringRequest request = RunnableWebMRequest(work);
        tools.OnRun = _ => File.WriteAllBytes(request.OutputPath, new byte[] { 1 });
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(engine))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        string notes = string.Join("\n", result.Notes);
        notes.Should().Contain("the source's own subtitle stream(s) #2 subrip (eng) were NOT carried");
        notes.Should().Contain("the source's own 2 chapter(s) were NOT carried");
        notes.Should().NotContain("but not for chapters");
    }

    [Fact]
    public void On_an_engine_without_tools_the_survey_says_chapters_were_not_checked()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("no-tools-survey");
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), "phone")
        {
            SubtitleStreams = new[]
            {
                new CodeBrix.VideoProcessing.Core.Media.SubtitleStreamInfo { Index = 2, CodecName = "mov_text", Language = "eng" },
            },
        };
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = WriteDummySource(work),
            OutputPath = work.File("clip.cbv"),
            TemporaryFolder = work.Path,
            ValidateProfile = false,
        };
        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        request.Video.SpeedPreset = 8;
        request.Audio.Include = false;
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(engine))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        string notes = string.Join("\n", result.Notes);
        notes.Should().Contain("the source's own subtitle stream(s) #2 mov_text (eng) were NOT carried");
        notes.Should().Contain(
            "the source was checked for subtitle streams of its own through the 'phone' engine, but not for chapters");
    }

    #endregion

    private static VideoAuthoringRequest DryWebMRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.WebMProfile,
            SourcePath = "/clips/source.mkv",
            OutputPath = "/out/clip.cbv",
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        request.Video.SpeedPreset = 13;
        request.Video.ConstantRateFactor = 30;
        return request;
    }

    private static VideoAuthoringRequest RunnableWebMRequest(WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.WebMProfile,
            SourcePath = WriteDummySource(work),
            OutputPath = work.File("clip.cbv"),
            TemporaryFolder = work.Path,
            ValidateProfile = false,
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        request.Video.SpeedPreset = 13;
        request.Video.ConstantRateFactor = 30;
        return request;
    }

    // The synthetic engine serves its clip whatever path it is handed; the file only has to exist.
    private static string WriteDummySource(WorkFolder work)
    {
        string path = work.File("source.mkv");
        File.WriteAllBytes(path, new byte[] { 0 });
        return path;
    }
}
