using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing;
using SilverAssertions;
using Xunit;
using CoreColorMatrix = CodeBrix.VideoProcessing.Core.Media.VideoColorMatrix;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// An RGB source - FFV1 <c>bgra</c> / <c>bgr0</c>, QuickTime Animation <c>argb</c>, PNG <c>rgba</c> - authored
/// in process on the REAL ffmpeg engine, which delivers it as 4:2:0 YUV converted with BT.709 and says so: no
/// refusal, no prepare pass, a file tagged BT.709, and a flat colour that plays back as the colour it was. And the
/// contract check that remains: an engine reporting the RGB matrix for the YUV samples it delivers is refused.
/// </summary>
public class RgbSourceInProcessAuthoringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The flat colour every source is filled with: a saturated green, which a wrong matrix moves a long way.
    private const int Red = 32;
    private const int Green = 200;
    private const int Blue = 64;

    // One flat colour, encoded at a moderate rate factor and converted RGB -> YUV -> RGB with 8-bit rounding on
    // both sides: a few levels per channel is the whole error budget (the measured worst is 1). BT.709 samples
    // read back as BT.601 move this colour by about 13 levels in red and 27 in green, well outside it.
    private const int ColourTolerance = 4;

    #region The ffmpeg engine

    [Theory]
    [InlineData("ffv1", "bgra", "mkv", true)]
    [InlineData("qtrle", "argb", "mov", true)]
    [InlineData("png", "rgba", "mov", true)]
    [InlineData("ffv1", "bgr0", "mkv", false)]
    public void An_rgb_source_authors_a_master_in_process_tagged_bt709_that_plays_back_its_colour(
        string codec, string pixelFormat, string container, bool alpha)
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("rgb-master-" + codec + "-" + pixelFormat);
        string source = WriteRgbClip(work.File("source." + container), codec, pixelFormat, alpha);
        VideoAuthoringRequest request = Request(VideoAuthoringFlavour.Master, source, work.File("clip.cbvmaster"), work);
        request.Video.Alpha = alpha ? AuthoringAlphaMode.Include : AuthoringAlphaMode.Auto;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        PlayedColour played = PlayThrough(result.OutputPath);

        //Assert
        result.Commands[0].Label.Should().Be("video pass");
        result.Commands.Any(c => c.RunsFFmpeg).Should().BeFalse();
        reader.Tracks[0].Color.Matrix.Should().Be(VideoMatrixCoefficients.Bt709);
        (reader.AlphaPlaneTrack != null).Should().Be(alpha);
        played.Failure.Should().BeNull();
        played.Frames.Should().BeGreaterThan(0);
        played.WorstRed.Should().BeLessThanOrEqualTo(ColourTolerance);
        played.WorstGreen.Should().BeLessThanOrEqualTo(ColourTolerance);
        played.WorstBlue.Should().BeLessThanOrEqualTo(ColourTolerance);
        if (alpha)
        {
            played.WorstTransparent.Should().BeLessThanOrEqualTo(12);
            played.WorstOpaque.Should().BeGreaterThanOrEqualTo(243);
        }
    }

    [Fact]
    public void An_rgb_source_authors_a_bespoke_file_in_process_tagged_bt709_that_plays_back_its_colour()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("rgb-bespoke");
        string source = WriteRgbClip(work.File("source.mkv"), "ffv1", "bgr0", false);
        VideoAuthoringRequest request = Request(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);
        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        PlayedColour played = PlayThrough(result.OutputPath);

        //Assert
        result.Commands.Any(c => c.RunsFFmpeg).Should().BeFalse();
        reader.Tracks[0].Color.Matrix.Should().Be(VideoMatrixCoefficients.Bt709);
        played.Failure.Should().BeNull();
        played.Frames.Should().BeGreaterThan(0);
        played.WorstRed.Should().BeLessThanOrEqualTo(ColourTolerance);
        played.WorstGreen.Should().BeLessThanOrEqualTo(ColourTolerance);
        played.WorstBlue.Should().BeLessThanOrEqualTo(ColourTolerance);
    }

    #endregion

    #region The contract check

    [Theory]
    [InlineData(VideoAuthoringFlavour.Master, "clip.cbvmaster")]
    [InlineData(VideoAuthoringFlavour.Bespoke, "clip.cbv")]
    [InlineData(VideoAuthoringFlavour.WebMProfile, "clip.webm")]
    public void An_engine_that_reports_the_rgb_matrix_for_its_yuv_samples_is_refused_by_the_contract_check(
        VideoAuthoringFlavour flavour, string output)
    {
        //Arrange
        using WorkFolder work = new WorkFolder("rgb-contract-" + flavour);
        string source = work.File("synthetic-source.bin");
        File.WriteAllBytes(source, new byte[] { 0 });
        VideoAuthoringRequest request = Request(flavour, source, work.File(output), work);
        if (flavour != VideoAuthoringFlavour.Master) request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), "misdescribing", hasSound: false)
        {
            DeliveredColorMatrix = CoreColorMatrix.Rgb,
        };

        //Act
        Action write = () =>
        {
            using (EngineScope.Use(engine))
            {
                CbvAuthor.Write(request);
            }
        };

        //Assert
        write.Should().Throw<VideoAuthoringException>()
            .WithMessage("*'misdescribing' engine*I420 (YUV) samples*RGB (identity) colour matrix*"
                + "must report the matrix of the samples it delivers*")
            .And.Message.Should().NotContain("ffv1");
        File.Exists(work.File(output)).Should().BeFalse();
    }

    [Fact]
    public void An_engine_that_reports_the_matrix_it_delivers_is_not_refused()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("rgb-contract-honest");
        string source = work.File("synthetic-source.bin");
        File.WriteAllBytes(source, new byte[] { 0 });
        VideoAuthoringRequest request = Request(VideoAuthoringFlavour.Master, source, work.File("clip.cbvmaster"), work);
        SyntheticEngine engine = new SyntheticEngine(new SyntheticClip(128, 72), hasSound: false);
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(engine))
        {
            result = CbvAuthor.Write(request);
        }

        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));

        //Assert
        reader.Tracks[0].Color.Matrix.Should().Be(VideoMatrixCoefficients.Bt709);
    }

    #endregion

    private static VideoAuthoringRequest Request(
        VideoAuthoringFlavour flavour, string source, string output, WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work.Path,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 20;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.Include = false;
        return request;
    }

    /// <summary>
    /// Writes a one-second silent clip of the flat colour in an RGB pixel format - with an alpha channel that is 0
    /// across the left half and 255 across the right one when <paramref name="alpha" /> is set.
    /// </summary>
    private static string WriteRgbClip(string path, string codec, string pixelFormat, bool alpha)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string size = SyntheticSource.Width.ToString(CultureInfo.InvariantCulture) + "x"
            + SyntheticSource.Height.ToString(CultureInfo.InvariantCulture);
        string video = "color=c=black:size=" + size
            + ":rate=" + SyntheticSource.FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=1,format=rgba,geq=r='" + Number(Red) + "':g='" + Number(Green) + "':b='" + Number(Blue)
            + "':a='" + (alpha ? "255*floor(2*X/W)" : "255") + "'";

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => output
                .WithVideoCodec(codec)
                .WithCustomArgument("-pix_fmt " + pixelFormat))
            .ProcessSynchronously();

        return path;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Plays a file through and converts every presented frame to BGRA, measuring the worst distance from the
    /// flat colour over the right (opaque) half and, with an alpha plane, the worst alpha on each side.
    /// </summary>
    private static PlayedColour PlayThrough(string path)
    {
        PlayedColour played = new PlayedColour { WorstOpaque = 255 };

        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
        session.MediaFailed += (s, e) => played.Failure = e.Exception == null ? "media failed" : e.Exception.Message;
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
                    Measure(frame, played);
                }
            }

            if (played.Failure != null) break;
            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        return played;
    }

    private static void Measure(VideoFrame frame, PlayedColour played)
    {
        played.Frames++;
        int stride = VideoFrameConverter.GetBgraStride(frame.Width);
        byte[] bgra = new byte[VideoFrameConverter.GetBgraBufferSize(frame.Width, frame.Height)];
        VideoFrameConverter.ToBgra32(frame, bgra, stride);

        // Away from the edges and from the alpha step in the middle, where chroma is shared across it.
        for (int y = 4; y < frame.Height - 4; y++)
        {
            for (int x = frame.Width / 2 + 4; x < frame.Width - 4; x++)
            {
                int at = y * stride + x * 4;
                played.WorstBlue = Math.Max(played.WorstBlue, Math.Abs(bgra[at] - Blue));
                played.WorstGreen = Math.Max(played.WorstGreen, Math.Abs(bgra[at + 1] - Green));
                played.WorstRed = Math.Max(played.WorstRed, Math.Abs(bgra[at + 2] - Red));
            }
        }

        if (!frame.HasAlpha) return;

        for (int y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<byte> row = frame.A.GetRowBytes(y);
            played.WorstTransparent = Math.Max(played.WorstTransparent, row[0]);
            played.WorstOpaque = Math.Min(played.WorstOpaque, row[frame.Width - 1]);
        }
    }

    private sealed class PlayedColour
    {
        public int Frames;
        public int WorstRed;
        public int WorstGreen;
        public int WorstBlue;
        public int WorstTransparent;
        public int WorstOpaque;
        public string Failure;
    }
}
