using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Ten-bit masters: the setting and where it is allowed, the engine it needs, the dry-run lines, and the round
/// trip - authored on both master paths (the ffmpeg engine, and an engine without FFmpeg), played back with the
/// AV1 decoder package, frames at 10 bits with more than 256 luma levels and a 10-bit alpha plane.
/// </summary>
public class TenBitMasterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    #region The setting

    [Fact]
    public void The_bit_depth_is_8_by_default_and_takes_10()
    {
        //Arrange
        AuthoringVideoSettings video = new AuthoringVideoSettings();

        //Act
        int before = video.BitDepth;
        video.BitDepth = 10;

        //Assert
        before.Should().Be(8);
        video.BitDepth.Should().Be(10);
    }

    [Theory]
    [InlineData(12, "Twelve-bit files are not authored*")]
    [InlineData(9, "A master is authored at 8 or 10 bits*")]
    [InlineData(16, "A master is authored at 8 or 10 bits*")]
    public void Any_other_bit_depth_is_refused(int depth, string message)
    {
        //Arrange
        AuthoringVideoSettings video = new AuthoringVideoSettings();

        //Act
        Action act = () => video.BitDepth = depth;

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage(message);
    }

    [Theory]
    [InlineData(VideoAuthoringFlavour.WebMProfile, "WebM-profile (Mode1)")]
    [InlineData(VideoAuthoringFlavour.Bespoke, "bespoke (Mode2) '.cbv'")]
    public void Ten_bits_outside_a_master_is_refused_by_name(VideoAuthoringFlavour flavour, string named)
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(flavour);
        request.Video.BitDepth = 10;

        //Act
        Action act = () => CbvAuthor.RenderCommands(request);

        //Assert
        act.Should().Throw<VideoAuthoringException>()
            .WithMessage("Video.BitDepth = 10 is a master-file setting: the " + named + " flavour is 8-bit 4:2:0 by decision*");
    }

    [Fact]
    public void A_ten_bit_master_dry_run_lists_ten_bit_colour_and_alpha_passes()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.BitDepth = 10;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Arguments.Should().StartWith("av1 i420 10-bit size decoded-cropped-to-even");
        commands[1].Arguments.Should().StartWith("av1 gray 10-bit size decoded-cropped-to-even");
    }

    [Theory]
    [InlineData(AuthoringAlphaMode.Auto, "yuva420p10le")]
    [InlineData(AuthoringAlphaMode.Exclude, "yuv420p10le")]
    public void A_ten_bit_prepare_pass_writes_a_ten_bit_intermediate(AuthoringAlphaMode alpha, string pixelFormat)
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.BitDepth = 10;
        request.Video.Alpha = alpha;
        request.Video.FrameSize = AuthoringFrameSize.Exact(64, 36);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[0].Label.Should().Be("prepare pass");
        commands[0].Arguments.Should().Contain("format=" + pixelFormat);
        commands[0].Arguments.Should().Contain("-pix_fmt " + pixelFormat);
    }

    [Fact]
    public void An_engine_without_ten_bit_frames_refuses_a_ten_bit_master_by_name()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest(VideoAuthoringFlavour.Master);
        request.Video.BitDepth = 10;
        SyntheticEngine engine = new SyntheticEngine(
            new SyntheticClip(128, 72), "eight-bit-only", VideoProcessorCapabilities.AlphaFrameSource);
        Action act;

        //Act
        using (EngineScope.Use(engine))
        {
            act = () => CbvAuthor.RenderCommands(request);

            //Assert
            act.Should().Throw<VideoAuthoringException>()
                .WithMessage("*10-bit master*'eight-bit-only' engine cannot deliver frames deeper than 8 bits*HighBitDepthFrameSource*");
        }
    }

    #endregion

    #region Round trips

    [Fact]
    public void A_ten_bit_master_from_the_ffmpeg_engine_plays_back_at_ten_bits_with_its_ramp_and_alpha()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("ten-bit-ffmpeg");
        SyntheticClip clip = new SyntheticClip(128, 72, bitDepth: 10, alpha: true);
        string source = clip.WriteForFFmpeg(work.File("ramp10.mkv"));
        VideoAuthoringRequest request = WriteRequest(source, work.File("ramp10.cbvmaster"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.OutputPath));
        PlaybackTally played = PlayThrough(result.OutputPath);

        //Assert
        result.Commands[0].Arguments.Should().StartWith("av1 i420 10-bit");
        result.Commands[1].Arguments.Should().StartWith("av1 gray 10-bit");
        reader.Tracks[0].BitDepth.Should().Be(10);
        reader.AlphaPlaneTrack.BitDepth.Should().Be(10);
        played.Failure.Should().BeNull();
        played.HasAlpha.Should().BeTrue();
        played.Frames.Should().BeGreaterThan(0);
        played.FramesNotTenBit.Should().Be(0);
        played.DistinctLuma.Should().BeGreaterThan(256);
        played.AlphaBytesPerSample.Should().Be(2);
        played.AlphaMaximum.Should().BeGreaterThan(255);
    }

    [Fact]
    public void A_ten_bit_master_from_an_engine_without_ffmpeg_plays_back_at_ten_bits_and_matches_the_ffmpeg_one()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("ten-bit-managed");
        SyntheticClip clip = new SyntheticClip(128, 72, bitDepth: 10, alpha: true);
        string source = clip.WriteForFFmpeg(work.File("ramp10.mkv"));
        VideoAuthoringRequest ffmpegRequest = WriteRequest(source, work.File("ffmpeg.cbvmaster"), work);
        VideoAuthoringRequest managedRequest = WriteRequest(source, work.File("managed.cbvmaster"), work);

        //Act
        VideoAuthoringResult ffmpeg = CbvAuthor.Write(ffmpegRequest);
        VideoAuthoringResult managed;
        using (EngineScope.Use(new SyntheticEngine(clip, hasSound: false)))
        {
            managed = CbvAuthor.Write(managedRequest);
        }

        PlaybackTally played = PlayThrough(managed.OutputPath);
        List<ShapedFrames.Snapshot> ffmpegFrames = MasterFrameDecoder.Decode(ffmpeg.OutputPath, out List<ShapedFrames.Snapshot> ffmpegAlpha);
        List<ShapedFrames.Snapshot> managedFrames = MasterFrameDecoder.Decode(managed.OutputPath, out List<ShapedFrames.Snapshot> managedAlpha);

        //Assert
        played.Failure.Should().BeNull();
        played.FramesNotTenBit.Should().Be(0);
        played.DistinctLuma.Should().BeGreaterThan(256);
        played.HasAlpha.Should().BeTrue();
        managedFrames.Count.Should().Be(clip.FrameCount);
        managedFrames.Count.Should().Be(ffmpegFrames.Count);
        managedFrames.All(f => f.BitDepth == 10).Should().BeTrue();
        MasterFrameDecoder.DistinctLuma(managedFrames).Should().BeGreaterThan(256);

        // The same 10-bit pixels into the same encoder settings: measured identical (worst 0) on 2026-10-11.
        ShapedFrames.Compare(managedFrames, ffmpegFrames).LumaWorst.Should().BeLessThanOrEqualTo(1);
        ShapedFrames.Compare(managedFrames, ffmpegFrames).ChromaWorst.Should().BeLessThanOrEqualTo(1);
        ShapedFrames.Compare(managedAlpha, ffmpegAlpha).LumaWorst.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void A_ten_bit_master_scaled_by_the_ffmpeg_prepare_pass_and_in_process_decode_within_the_ten_bit_scaler_bounds()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("ten-bit-scaled");
        SyntheticClip clip = new SyntheticClip(128, 72, bitDepth: 10, alpha: true);
        string source = clip.WriteForFFmpeg(work.File("ramp10.mkv"));

        VideoAuthoringRequest Request(string output)
        {
            VideoAuthoringRequest request = WriteRequest(source, work.File(output), work);
            request.Video.FrameSize = AuthoringFrameSize.Exact(96, 54);
            request.Video.FillTransparentPixels = false;

            // Near-lossless, so what is compared is the shaping and not two lossy encoders' different decisions
            // about two slightly different inputs (at cq-level 30 those reach tens of levels).
            request.Video.ConstantRateFactor = 0;
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

        //Assert
        ffmpeg.Commands[0].Arguments.Should().Contain("yuva420p10le");
        managed.Commands[0].Arguments.Should().Be("scale 96x54 lanczos3");
        managedFrames.All(f => f.BitDepth == 10 && f.Width == 96 && f.Height == 54).Should().BeTrue();
        picture.LumaWorst.Should().BeLessThanOrEqualTo(6, picture.ToString());
        picture.ChromaWorst.Should().BeLessThanOrEqualTo(4, picture.ToString());
        ShapedFrames.Compare(managedAlpha, ffmpegAlpha).LumaWorst.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void An_eight_bit_source_authored_at_ten_bits_says_it_was_widened()
    {
        //Arrange
        using WorkFolder work = new WorkFolder("ten-bit-widened");
        System.IO.File.WriteAllBytes(work.File("source.bin"), new byte[] { 0 });
        VideoAuthoringRequest request = WriteRequest(work.File("source.bin"), work.File("widened.cbvmaster"), work);
        VideoAuthoringResult result;

        //Act
        using (EngineScope.Use(new SyntheticEngine(new SyntheticClip(128, 72), hasSound: false)))
        {
            result = CbvAuthor.Write(request);
        }

        //Assert
        result.Notes.Should().Contain(note => note.Contains("8-bit and was WIDENED to 10 bits for this master by the engine"));
        MasterFrameDecoder.Decode(result.OutputPath, out _).All(f => f.BitDepth == 10).Should().BeTrue();
    }

    #endregion

    #region Units

    [Fact]
    public void An_eight_bit_frame_is_widened_keeping_studio_levels_and_full_opacity()
    {
        //Arrange
        byte[] y = { 16, 235, 128, 0 };
        byte[] u = { 128 };
        byte[] v = { 240 };
        byte[] a = { 0, 255, 128, 1 };
        VideoFrameData frame = VideoFrameData.CreateI420(
            2, 2, VideoPlane.FromArray(y, 2, 2), VideoPlane.FromArray(u, 1, 1), VideoPlane.FromArray(v, 1, 1),
            TimeSpan.Zero, 0, VideoPlane.FromArray(a, 2, 2));

        //Act
        VideoFrameData wide = InProcessAuthoring.WidenTo(frame, 10, "test");

        //Assert
        wide.BitDepth.Should().Be(10);
        Sample(wide.Y, 0).Should().Be(64);
        Sample(wide.Y, 1).Should().Be(940);
        Sample(wide.V, 0).Should().Be(960);
        Sample(wide.A, 0).Should().Be(0);
        Sample(wide.A, 1).Should().Be(1023);
        Sample(wide.A, 2).Should().Be(514);
    }

    [Fact]
    public void A_fully_opaque_ten_bit_plane_is_1023_little_endian()
    {
        //Arrange
        int depth = 10;

        //Act
        byte[] plane = InProcessAuthoring.OpaquePlane(3, 2, depth);

        //Assert
        plane.Length.Should().Be(12);
        plane.Where((b, i) => i % 2 == 0).All(b => b == 0xFF).Should().BeTrue();
        plane.Where((b, i) => i % 2 == 1).All(b => b == 0x03).Should().BeTrue();
    }

    [Fact]
    public void The_transparent_pixel_fill_copies_whole_ten_bit_samples()
    {
        //Arrange - a 4x2 picture whose left half is fully transparent.
        TransparentPixelFill fill = new TransparentPixelFill();
        byte[] y = Wide(0, 0, 700, 800, 0, 0, 700, 800);
        byte[] u = Wide(1, 600);
        byte[] v = Wide(2, 300);
        byte[] alpha = Wide(0, 0, 1023, 4, 0, 0, 1023, 4);

        //Act
        bool filled = fill.Apply(4, 2, y, 8, u, v, 4, alpha, 8, 2);

        //Assert
        filled.Should().BeTrue();
        Sample(y, 1).Should().Be(700);
        Sample(y, 0).Should().Be(700);
        Sample(u, 0).Should().Be(600);
        Sample(v, 0).Should().Be(300);
        Sample(y, 3).Should().Be(800);
    }

    #endregion

    private static VideoAuthoringRequest DryRequest(VideoAuthoringFlavour flavour) => new VideoAuthoringRequest
    {
        Flavour = flavour,
        SourcePath = "/nowhere/source.mov",
        OutputPath = flavour == VideoAuthoringFlavour.Master ? "/nowhere/clip.cbvmaster" : "/nowhere/clip.cbv",
        TemporaryFolder = "/nowhere/work",
    };

    private static VideoAuthoringRequest WriteRequest(string source, string output, WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work.Path,
        };

        request.Audio.Include = false;
        request.Video.BitDepth = 10;
        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 30;
        request.Video.KeyframeIntervalFrames = 5;
        return request;
    }

    private static int Sample(VideoPlane plane, int index)
    {
        ReadOnlySpan<byte> data = plane.Data.Span;
        return data[2 * index] | (data[(2 * index) + 1] << 8);
    }

    private static int Sample(byte[] data, int index) => data[2 * index] | (data[(2 * index) + 1] << 8);

    private static byte[] Wide(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[2 * i] = (byte)values[i];
            bytes[(2 * i) + 1] = (byte)(values[i] >> 8);
        }

        return bytes;
    }

    private static PlaybackTally PlayThrough(string path)
    {
        PlaybackTally tally = new PlaybackTally();
        HashSet<int> luma = new HashSet<int>();

        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
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
                    if (frame.BitDepth != 10) tally.FramesNotTenBit++;

                    for (int row = 0; row < frame.Height; row++)
                    {
                        ReadOnlySpan<byte> bytes = frame.Y.GetRowBytes(row);
                        for (int x = 0; x + 1 < bytes.Length; x += 2) luma.Add(bytes[x] | (bytes[x + 1] << 8));
                    }

                    if (frame.HasAlpha)
                    {
                        tally.AlphaBytesPerSample = frame.A.BytesPerSample;
                        ReadOnlySpan<byte> row = frame.A.GetRowBytes(frame.Height / 2);
                        for (int x = 0; x + 1 < row.Length; x += 2)
                        {
                            tally.AlphaMaximum = Math.Max(tally.AlphaMaximum, row[x] | (row[x + 1] << 8));
                        }
                    }
                }
            }

            if (tally.Failure != null) break;
            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        tally.DistinctLuma = luma.Count;
        return tally;
    }

    private sealed class PlaybackTally
    {
        public int Frames;
        public int FramesNotTenBit;
        public int DistinctLuma;
        public bool HasAlpha;
        public int AlphaBytesPerSample;
        public int AlphaMaximum;
        public string Failure;
    }
}
