using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Exceptions;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// What happens when the installed FFmpeg has no SVT-AV1 encoder: the message a failed pass is reported with,
/// the up-front warning from the tool check, and <see cref="VideoAuthoringRequest.AllowAv1EncoderFallback" />.
/// </summary>
/// <remarks>
/// <para>
/// The tests that run FFmpeg are only meaningful where its build has NO libsvtav1 - the Windows "essentials"
/// builds are the usual case - so every one of them skips itself wherever libsvtav1 IS present, as it is in the
/// usual Linux distribution and Homebrew builds. The fallback tests also skip where libaom-av1 is missing,
/// because then there is nothing to fall back to. That is the suite working as designed, not a gap in it.
/// </para>
/// <para>
/// The tests that check the wording run no FFmpeg at all and run everywhere.
/// </para>
/// <para>
/// One test changes the PROCESS-WIDE switch and puts it back. Every test that depends on that switch lives in
/// this class, and the tests of one class never run at the same time, so none of them can see it changed. No
/// other class authors with libsvtav1 on a machine that lacks it, so none of them is affected either.
/// </para>
/// </remarks>
public class Av1EncoderFallbackTests
{
    private const string SvtAv1Present =
        "This machine's FFmpeg has libsvtav1, so a missing SVT-AV1 encoder cannot be reproduced here.";

    private const string AomAv1Missing =
        "This machine's FFmpeg has no libaom-av1 either, so there is nothing for the AV1 fallback to use.";

    #region The wording - no FFmpeg needed

    [Fact]
    public void AllowAv1EncoderFallback_is_off_by_default()
    {
        //Arrange
        VideoAuthoringRequest request = new VideoAuthoringRequest();

        //Act
        bool allowed = request.AllowAv1EncoderFallback;

        //Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void RenderCommands_renders_the_requested_encoder_whether_or_not_the_fallback_is_allowed()
    {
        //Arrange
        VideoAuthoringRequest without = RenderOnlyRequest(false);
        VideoAuthoringRequest with = RenderOnlyRequest(true);

        //Act
        string withoutLine = CbvAuthor.RenderCommands(without)[0].Arguments;
        string withLine = CbvAuthor.RenderCommands(with)[0].Arguments;

        //Assert
        withLine.Should().Be(withoutLine);
        withLine.Should().Contain("-c:v libsvtav1");
    }

    [Fact]
    public void A_failed_pass_that_is_not_a_missing_encoder_keeps_the_message_it_always_had()
    {
        //Arrange
        FFMpegException failure = new FFMpegException(
            FFMpegExceptionType.Process, "ffmpeg exited with non-zero exit-code (1 - Invalid data found)");

        //Act
        string message = CbvAuthor.DescribePassFailure("video pass", "-i \"in.mkv\" -c:v libsvtav1", false, failure);

        //Assert
        message.Should().Be("The video pass failed. The command was: ffmpeg -i \"in.mkv\" -c:v libsvtav1");
    }

    [Fact]
    public void A_missing_libsvtav1_is_explained_between_the_failed_pass_and_the_command()
    {
        //Arrange
        Exception failure = new InvalidOperationException(
            "wrapped",
            MissingEncoder("libsvtav1", "The SVT-AV1 encoder library (libsvtav1) is not present on this machine."));

        //Act
        string message = CbvAuthor.DescribePassFailure("video pass", "-c:v libsvtav1 -preset 8", false, failure);

        //Assert
        message.Should().StartWith("The video pass failed. " + AuthoringTools.MissingSvtAv1 + " ");
        message.Should().Contain(AuthoringTools.SvtAv1Remedies);
        message.Should().Contain("AllowAv1EncoderFallback = true on the VideoAuthoringRequest");
        message.Should().Contain("GlobalFFOptions.Configure(o => o.AllowAv1EncoderFallback = true)");
        message.Should().EndWith(" The command was: ffmpeg -c:v libsvtav1 -preset 8");
    }

    [Fact]
    public void With_the_fallback_on_a_missing_libsvtav1_is_explained_with_the_reason_it_could_not_help()
    {
        //Arrange
        const string wrapperExplanation =
            "The SVT-AV1 encoder library (libsvtav1) is not present on this machine, or the ffmpeg being used could "
            + "not find it. FFOptions.AllowAv1EncoderFallback is enabled, but the fallback could not be used: this "
            + "ffmpeg has no libaom-av1 encoder either, so there was nothing to fall back to. Install an ffmpeg build "
            + "that includes libsvtav1 or libaom-av1.";
        Exception failure = MissingEncoder("libsvtav1", wrapperExplanation);

        //Act
        string message = CbvAuthor.DescribePassFailure("one pass", "-c:v libsvtav1", true, failure);

        //Assert
        message.Should().Be("The one pass failed. " + wrapperExplanation + " The command was: ffmpeg -c:v libsvtav1");
        message.Should().NotContain(AuthoringTools.SvtAv1Remedies);
    }

    [Fact]
    public void With_the_fallback_on_and_no_reason_given_the_message_still_says_the_fallback_could_not_help()
    {
        //Arrange
        FFMpegEncoderNotFoundException failure = new FFMpegEncoderNotFoundException(
            "libsvtav1", "ffmpeg exited with non-zero exit-code (1 - Unknown encoder 'libsvtav1')");

        //Act
        string message = CbvAuthor.DescribePassFailure("video pass", "-c:v libsvtav1", true, failure);

        //Assert
        message.Should().Be(
            "The video pass failed. " + AuthoringTools.MissingSvtAv1 + " " + AuthoringTools.FallbackCouldNotHelp
            + " The command was: ffmpeg -c:v libsvtav1");
    }

    [Fact]
    public void A_missing_encoder_other_than_libsvtav1_is_named()
    {
        //Arrange
        Exception failure = MissingEncoder("libopus", string.Empty);

        //Act
        string message = CbvAuthor.DescribePassFailure("one pass", "-c:a libopus", false, failure);

        //Assert
        message.Should().Contain("has no 'libopus' encoder");
        message.Should().NotContain("SVT-AV1");
        message.Should().EndWith(" The command was: ffmpeg -c:a libopus");
    }

    [Fact]
    public void A_build_with_every_encoder_earns_no_warning()
    {
        //Arrange
        Func<string, bool> everything = _ => true;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeEncoderWarnings(everything);

        //Assert
        warnings.Count.Should().Be(0);
    }

    [Fact]
    public void A_build_without_libsvtav1_is_warned_about_and_told_about_the_fallback()
    {
        //Arrange
        Func<string, bool> allButSvtAv1 = name => name != AuthoringEncoderNames.LibSvtAv1;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeEncoderWarnings(allButSvtAv1);

        //Assert
        warnings.Count.Should().Be(1);
        warnings[0].Should().StartWith(AuthoringTools.MissingSvtAv1);
        warnings[0].Should().Contain("AllowAv1EncoderFallback = true on the VideoAuthoringRequest");
    }

    [Fact]
    public void A_build_with_no_av1_encoder_at_all_is_told_the_fallback_cannot_help()
    {
        //Arrange
        Func<string, bool> noAv1 = name =>
            name != AuthoringEncoderNames.LibSvtAv1 && name != AuthoringEncoderNames.LibAomAv1;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeEncoderWarnings(noAv1);

        //Assert
        warnings.Count.Should().Be(1);
        warnings[0].Should().StartWith("Neither AV1 encoder");
        warnings[0].Should().Contain("AllowAv1EncoderFallback cannot help");
    }

    [Fact]
    public void A_build_without_the_audio_encoders_is_warned_about_each_of_them()
    {
        //Arrange
        Func<string, bool> videoOnly = name =>
            name == AuthoringEncoderNames.LibSvtAv1 || name == AuthoringEncoderNames.LibAomAv1;

        //Act
        IReadOnlyList<string> warnings = AuthoringTools.DescribeEncoderWarnings(videoOnly);

        //Assert
        warnings.Count.Should().Be(2);
        warnings[0].Should().Contain("(libopus)");
        warnings[1].Should().Contain("(libvorbis)");
    }

    #endregion

    #region Against the installed FFmpeg

    [Fact]
    public void Without_libsvtav1_a_bespoke_request_fails_with_the_explanation_and_the_fallback_setting()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();
        using WorkFolder work = new WorkFolder("no-svtav1-bespoke");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request =
            SvtAv1Request(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        AssertExplainsMissingSvtAv1(failure, "video pass");
        File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void Without_libsvtav1_a_webm_profile_request_fails_with_the_explanation_and_the_fallback_setting()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();
        using WorkFolder work = new WorkFolder("no-svtav1-webm");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request =
            SvtAv1Request(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        AssertExplainsMissingSvtAv1(failure, "one pass");
        File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void With_the_fallback_allowed_a_bespoke_request_is_authored_with_libaom_when_libsvtav1_is_missing()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();
        Assert.SkipUnless(AuthoringEncoders.HasAomAv1Encoder, AomAv1Missing);
        using WorkFolder work = new WorkFolder("fallback-bespoke");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request =
            SvtAv1Request(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);
        request.AllowAv1EncoderFallback = true;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.Mux.Should().NotBeNull();
        result.Commands.Count.Should().Be(2);
        result.Commands[0].Label.Should().Be("video pass");
        AssertRanWithLibAom(result, "video pass");
        result.Commands[1].Label.Should().Be("audio pass");
        result.Commands[1].Arguments.Should().Contain("-c:a libvorbis");

        // The dry run cannot know what the build has, so it goes on rendering what the request asked for.
        CbvAuthor.RenderCommands(request)[0].Arguments.Should().Contain("-c:v libsvtav1");
    }

    [Fact]
    public void With_the_fallback_allowed_a_webm_profile_request_is_authored_with_libaom_when_libsvtav1_is_missing()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();
        Assert.SkipUnless(AuthoringEncoders.HasAomAv1Encoder, AomAv1Missing);
        using WorkFolder work = new WorkFolder("fallback-webm");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request =
            SvtAv1Request(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.cbv"), work);
        request.AllowAv1EncoderFallback = true;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.Commands.Count.Should().Be(1);
        result.Commands[0].Label.Should().Be("one pass");
        AssertRanWithLibAom(result, "one pass");
    }

    [Fact]
    public void ZZZ_The_process_wide_switch_turns_the_fallback_on_for_a_request_that_leaves_its_own_off()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();
        Assert.SkipUnless(AuthoringEncoders.HasAomAv1Encoder, AomAv1Missing);
        using WorkFolder work = new WorkFolder("fallback-global");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request =
            SvtAv1Request(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.cbv"), work);
        bool previous = GlobalFFOptions.Current.AllowAv1EncoderFallback;

        try
        {
            GlobalFFOptions.Configure(options => options.AllowAv1EncoderFallback = true);

            //Act
            VideoAuthoringResult result = CbvAuthor.Write(request);

            //Assert
            request.AllowAv1EncoderFallback.Should().BeFalse();
            result.PassesProfile.Should().BeTrue();
            AssertRanWithLibAom(result, "one pass");
        }
        finally
        {
            GlobalFFOptions.Configure(options => options.AllowAv1EncoderFallback = previous);
        }
    }

    [Fact]
    public void Without_libsvtav1_the_tool_check_warns_about_it_up_front()
    {
        //Arrange
        SkipUnlessSvtAv1IsMissing();

        //Act
        bool verified = CbvAuthor.TryVerifyTools(out string problem, out IReadOnlyList<string> warnings);
        CbvAuthor.VerifyTools(out IReadOnlyList<string> thrownWarnings);
        bool binariesOnly = CbvAuthor.TryVerifyTools(out string binariesProblem);

        //Assert
        verified.Should().BeTrue();
        problem.Should().BeEmpty();
        AssertWarnsAboutSvtAv1(warnings);
        AssertWarnsAboutSvtAv1(thrownWarnings);

        // The overload without warnings still answers only "are the binaries there?".
        binariesOnly.Should().BeTrue();
        binariesProblem.Should().BeEmpty();
    }

    #endregion

    private static void SkipUnlessSvtAv1IsMissing()
    {
        SyntheticSource.SkipWithoutFFmpeg();
        Assert.SkipWhen(AuthoringEncoders.HasSvtAv1Encoder, SvtAv1Present);
    }

    // Asks for the library's DEFAULT encoder outright, whatever this machine has - the opposite of the other
    // suites, which pick whichever encoder is present. The fastest SVT-AV1 preset, 13, is also the one the
    // fallback has to cap, so the rewrite is exercised as well as the codec swap.
    private static VideoAuthoringRequest SvtAv1Request(
        VideoAuthoringFlavour flavour,
        string source,
        string output,
        WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work.Path,
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        request.Video.SpeedPreset = 13;
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;

        return request;
    }

    private static VideoAuthoringRequest RenderOnlyRequest(bool allowFallback)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.WebMProfile,
            SourcePath = "/clips/source.mkv",
            OutputPath = "/out/clip.cbv",
            AllowAv1EncoderFallback = allowFallback,
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        return request;
    }

    private static Exception MissingEncoder(string encoderName, string explanation)
    {
        string message = (explanation.Length > 0 ? explanation + " " : string.Empty)
            + "ffmpeg exited with non-zero exit-code (1 - Unknown encoder '" + encoderName + "')";

        return new FFMpegEncoderNotFoundException(
            encoderName, message, null, "[vost#0:0 @ 0000] Unknown encoder '" + encoderName + "'");
    }

    private static void AssertExplainsMissingSvtAv1(VideoAuthoringException failure, string pass)
    {
        // Everything a caller that logs only Message needs, in the order a reader wants it.
        failure.Message.Should().StartWith("The " + pass + " failed. " + AuthoringTools.MissingSvtAv1 + " ");
        failure.Message.Should().Contain("AllowAv1EncoderFallback = true on the VideoAuthoringRequest");
        failure.Message.Should().Contain("The command was: ffmpeg ");
        failure.Message.Should().Contain("-c:v libsvtav1");

        // And FFmpeg's own account is still underneath it, whole.
        (failure.InnerException is FFMpegEncoderNotFoundException).Should().BeTrue();
        FFMpegEncoderNotFoundException missing = (FFMpegEncoderNotFoundException)failure.InnerException;
        missing.EncoderName.Should().Be(AuthoringEncoderNames.LibSvtAv1);
        missing.FFMpegErrorOutput.Should().Contain("Unknown encoder 'libsvtav1'");
    }

    private static void AssertRanWithLibAom(VideoAuthoringResult result, string pass)
    {
        // The command recorded is the one that RAN, not the one the request rendered.
        string ran = result.Commands[0].Arguments;
        ran.Should().Contain("-c:v libaom-av1");
        ran.Should().Contain("-cpu-used 8");
        ran.Should().Contain("-b:v 0");
        ran.Should().NotContain("libsvtav1");

        bool noted = false;
        foreach (string note in result.Notes)
        {
            if (note.StartsWith(pass + ": ", StringComparison.Ordinal) && note.Contains("libaom-av1")) noted = true;
        }

        noted.Should().BeTrue();
    }

    private static void AssertWarnsAboutSvtAv1(IReadOnlyList<string> warnings)
    {
        bool warned = false;
        foreach (string warning in warnings)
        {
            if (warning.StartsWith(AuthoringTools.MissingSvtAv1, StringComparison.Ordinal)
                && warning.Contains("AllowAv1EncoderFallback"))
            {
                warned = true;
            }
        }

        warned.Should().BeTrue();
    }
}
