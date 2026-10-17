using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// THE DECISIVE COMPARISON: for the same request over the same pixels, the frames the in-process shaping hands
/// the encoder on an engine without FFmpeg against the frames the ffmpeg prepare pass writes (read back by the
/// ffmpeg engine) - frame count and timestamps exact, rotation exact, scaling and grading within measured bounds.
/// </summary>
/// <remarks>
/// <para>
/// The bounds are the differences MEASURED on these clips (host FFmpeg 7.1) with a level or two of head-room; the
/// means are asserted too, because a worst case can hide a systematic shift. The prepare pass's scale carries
/// <c>accurate_rnd+full_chroma_int</c>, as the core's own oracle measurements do (MAINTAINER-README, "THE MANAGED
/// FRAME-PROCESSING PRIMITIVES"); with it the 8-bit scaling means fell to a third or less of what FFmpeg's default
/// rounding gave (scale-down-lanczos luma mean 0.352 -> 0.092, everything-8-bit 1.424 -> 0.401), and the bounds
/// were tightened to match. Where they are still wider than the oracle's it is the grade: the RGB conversions
/// FFmpeg inserts round <c>lut3d</c> use its default (bicubic, no full chroma interpolation) scaler, which no scale
/// flag reaches. The scaler's larger worst cases sit on the clip's hard-edged frame-counter block (Lanczos ringing);
/// the 10-bit cases did not move.
/// </para>
/// <para>
/// FRAME RATES are compared in <see cref="AuthoringFrameRateMode.Filter" /> mode, where FFmpeg's <c>fps</c> filter
/// is what the managed resampler mirrors. <see cref="AuthoringFrameRateMode.Encoder" /> mode is the same <c>fps</c>
/// filter at the end of FFmpeg's chain (it used to be the output-side <c>-r</c>, which kept different frames and
/// wrote 12 instead of 10 when it halved a 2-second clip from 10 to 5 frames a second), so both paths keep the same
/// frames in both modes - see
/// <see cref="Encoder_mode_halving_keeps_the_same_frames_on_both_paths" />.
/// </para>
/// </remarks>
public class ManagedVersusFFmpegShapingTests
{
    /// <summary>The cases: name, then the bounds (luma worst, chroma worst, alpha worst, luma mean, chroma mean).</summary>
    public static TheoryData<string, int, int, int, double, double> Cases => new TheoryData<string, int, int, int, double, double>
    {
        { "rotate-90", 0, 0, 0, 0d, 0d },
        { "rotate-180", 0, 0, 0, 0d, 0d },
        { "rotate-270", 0, 0, 0, 0d, 0d },
        { "rotate-90-10-bit-alpha", 0, 0, 0, 0d, 0d },
        { "scale-down-lanczos", 2, 2, 0, 0.2d, 0.2d },
        { "scale-up-lanczos", 4, 2, 0, 0.1d, 0.1d },
        { "scale-down-bilinear", 2, 2, 0, 0.2d, 0.2d },
        { "scale-long-side", 2, 2, 0, 0.2d, 0.1d },
        { "scale-short-side", 2, 2, 0, 0.1d, 0.1d },
        { "scale-down-alpha", 2, 2, 1, 0.2d, 0.2d },
        { "scale-down-10-bit", 4, 2, 0, 0.1d, 0.1d },
        { "scale-up-10-bit", 12, 2, 0, 0.1d, 0.1d },
        { "grade-single", 4, 6, 0, 1.3d, 0.5d },
        { "grade-composed", 4, 6, 0, 1.4d, 0.5d },
        { "grade-10-bit", 3, 36, 0, 0.6d, 0.9d },
        { "fps-halve", 0, 0, 0, 0d, 0d },
        { "fps-up-15", 0, 0, 0, 0d, 0d },
        { "fps-up-24", 0, 0, 0, 0d, 0d },
        { "fps-30-to-29.97", 0, 0, 0, 0d, 0d },
        { "everything-8-bit", 4, 5, 0, 0.6d, 0.6d },
        { "everything-10-bit-alpha", 12, 22, 3, 0.7d, 0.9d },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_managed_shaping_matches_the_ffmpeg_prepare_pass_within_its_bounds(
        string name,
        int lumaWorst,
        int chromaWorst,
        int alphaWorst,
        double lumaMean,
        double chromaMean)
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("managed-vs-ffmpeg");
        (SyntheticClip clip, bool alpha) = Describe(name, out Action<AuthoringVideoSettings> configure);
        VideoAuthoringRequest request = Request(clip, work, configure);

        //Act
        List<ShapedFrames.Snapshot> managed = ShapedFrames.Managed(new SyntheticEngine(clip), request, work, alpha);
        List<ShapedFrames.Snapshot> ffmpeg = ShapedFrames.FFmpeg(request, work, alpha);
        ShapedFrames.Difference difference = ShapedFrames.Compare(managed, ffmpeg);

        //Assert
        managed.Count.Should().Be(ffmpeg.Count);
        for (int i = 0; i < managed.Count; i++)
        {
            managed[i].Timestamp.Should().Be(ffmpeg[i].Timestamp, "frame " + i + " of " + name);
            managed[i].Width.Should().Be(ffmpeg[i].Width);
            managed[i].Height.Should().Be(ffmpeg[i].Height);
        }

        difference.LumaWorst.Should().BeLessThanOrEqualTo(lumaWorst, difference.ToString());
        difference.ChromaWorst.Should().BeLessThanOrEqualTo(chromaWorst, difference.ToString());
        difference.AlphaWorst.Should().BeLessThanOrEqualTo(alphaWorst, difference.ToString());
        difference.LumaMean.Should().BeLessThanOrEqualTo(lumaMean, difference.ToString());
        difference.ChromaMean.Should().BeLessThanOrEqualTo(chromaMean, difference.ToString());
    }

    [Fact]
    public void Encoder_mode_halving_keeps_the_same_frames_on_both_paths()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("managed-vs-ffmpeg-encoder-rate");
        SyntheticClip clip = new SyntheticClip(128, 72);
        VideoAuthoringRequest encoderMode = Request(clip, work, video => video.FrameRate = 5);
        VideoAuthoringRequest filterMode = Request(clip, work, video => FilterRate(video, 5));

        //Act
        List<ShapedFrames.Snapshot> managed = ShapedFrames.Managed(new SyntheticEngine(clip), encoderMode, work, false);
        List<ShapedFrames.Snapshot> ffmpegFilter = ShapedFrames.FFmpeg(filterMode, work, false);
        List<ShapedFrames.Snapshot> ffmpegEncoder = ShapedFrames.FFmpeg(encoderMode, work, false);

        //Assert
        managed.Count.Should().Be(10);
        ShapedFrames.Compare(managed, ffmpegFilter).LumaWorst.Should().Be(0);
        for (int i = 0; i < managed.Count; i++) managed[i].Timestamp.Should().Be(ffmpegFilter[i].Timestamp);

        // Encoder mode is fps=5 at the end of FFmpeg's chain: the same ten frames as Filter mode and the managed path.
        ffmpegEncoder.Count.Should().Be(10);
        ShapedFrames.Compare(managed, ffmpegEncoder).LumaWorst.Should().Be(0);
        for (int i = 0; i < managed.Count; i++) managed[i].Timestamp.Should().Be(ffmpegEncoder[i].Timestamp);
    }

    [Fact]
    public void An_odd_sized_four_two_zero_source_cannot_be_turned_a_quarter_in_process_and_says_why()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("managed-odd-rotation");
        SyntheticClip clip = new SyntheticClip(129, 72, rotationDegrees: 90);
        VideoAuthoringRequest request = Request(clip, work, video => { });
        request.SourcePath = work.File("clip.mkv");

        //Act
        Action act = () => ShapedFrames.Managed(new SyntheticEngine(clip), request, work, false);

        //Assert
        act.Should().Throw<VideoAuthoringException>()
            .WithMessage("*AutoRotate could not turn the source*width 129 is odd*AutoRotate to false*");
    }

    private static VideoAuthoringRequest Request(SyntheticClip clip, WorkFolder work, Action<AuthoringVideoSettings> configure)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = work.File("clip.mkv"),
            OutputPath = work.File("clip.cbvmaster"),
            TemporaryFolder = work.Path,
        };

        request.Video.BitDepth = clip.BitDepth;
        configure(request.Video);

        if (clip.Width % 2 == 0) clip.WriteForFFmpeg(request.SourcePath);
        return request;
    }

    private static (SyntheticClip Clip, bool Alpha) Describe(string name, out Action<AuthoringVideoSettings> configure)
    {
        string warm = AuthoringTestAssets.Lut("warm_33.cube");
        string cool = AuthoringTestAssets.Lut("cool_33.cube");

        switch (name)
        {
            case "rotate-90":
                configure = video => { };
                return (new SyntheticClip(128, 72, rotationDegrees: 90), false);
            case "rotate-180":
                configure = video => { };
                return (new SyntheticClip(128, 72, rotationDegrees: 180), false);
            case "rotate-270":
                configure = video => { };
                return (new SyntheticClip(128, 72, rotationDegrees: 270), false);
            case "rotate-90-10-bit-alpha":
                configure = video => { };
                return (new SyntheticClip(128, 72, bitDepth: 10, alpha: true, rotationDegrees: 90), true);
            case "scale-down-lanczos":
                configure = video => video.FrameSize = AuthoringFrameSize.Exact(96, 54);
                return (new SyntheticClip(128, 72), false);
            case "scale-up-lanczos":
                configure = video => video.FrameSize = AuthoringFrameSize.Exact(160, 90);
                return (new SyntheticClip(128, 72), false);
            case "scale-down-bilinear":
                configure = video =>
                {
                    video.FrameSize = AuthoringFrameSize.Exact(96, 54);
                    video.ScalerFlags = "bilinear";
                };
                return (new SyntheticClip(128, 72), false);
            case "scale-long-side":
                configure = video => video.FrameSize = AuthoringFrameSize.LongSide(64);
                return (new SyntheticClip(128, 72), false);
            case "scale-short-side":
                configure = video => video.FrameSize = AuthoringFrameSize.ShortSide(48);
                return (new SyntheticClip(128, 72), false);
            case "scale-down-alpha":
                configure = video => video.FrameSize = AuthoringFrameSize.Exact(96, 54);
                return (new SyntheticClip(128, 72, alpha: true), true);
            case "scale-down-10-bit":
                configure = video => video.FrameSize = AuthoringFrameSize.Exact(96, 54);
                return (new SyntheticClip(128, 72, bitDepth: 10), false);
            case "scale-up-10-bit":
                configure = video => video.FrameSize = AuthoringFrameSize.Exact(160, 90);
                return (new SyntheticClip(128, 72, bitDepth: 10), false);
            case "grade-single":
                configure = video => video.Luts.Add(new AuthoringLutInput(warm));
                return (new SyntheticClip(128, 72), false);
            case "grade-composed":
                configure = video =>
                {
                    video.Luts.Add(new AuthoringLutInput(warm, 40));
                    video.Luts.Add(new AuthoringLutInput(cool, 40));
                };
                return (new SyntheticClip(128, 72), false);
            case "grade-10-bit":
                configure = video => video.Luts.Add(new AuthoringLutInput(warm));
                return (new SyntheticClip(128, 72, bitDepth: 10), false);
            case "fps-halve":
                configure = video => FilterRate(video, 5);
                return (new SyntheticClip(128, 72), false);
            case "fps-up-15":
                configure = video => FilterRate(video, 15);
                return (new SyntheticClip(128, 72), false);
            case "fps-up-24":
                configure = video => FilterRate(video, 24);
                return (new SyntheticClip(128, 72), false);
            case "fps-30-to-29.97":
                configure = video => FilterRate(video, 29.97);
                return (new SyntheticClip(128, 72, frameCount: 60, rateNumerator: 30), false);
            case "everything-8-bit":
                configure = video =>
                {
                    video.FrameSize = AuthoringFrameSize.LongSide(96);
                    FilterRate(video, 15);
                    video.Luts.Add(new AuthoringLutInput(warm));
                };
                return (new SyntheticClip(128, 72, rotationDegrees: 90), false);
            case "everything-10-bit-alpha":
                configure = video =>
                {
                    video.FrameSize = AuthoringFrameSize.Exact(96, 128);
                    FilterRate(video, 5);
                    video.Luts.Add(new AuthoringLutInput(warm));
                };
                return (new SyntheticClip(128, 72, bitDepth: 10, alpha: true, rotationDegrees: 270), true);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "No such case.");
        }
    }

    private static void FilterRate(AuthoringVideoSettings video, double rate)
    {
        video.FrameRate = rate;
        video.FrameRateMode = AuthoringFrameRateMode.Filter;
    }
}
