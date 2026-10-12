using System;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Output;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// The export's dry run and its refusals: what it would do for each flavour, said the same way every time, and
/// every request it turns away with a message naming the piece involved.
/// </summary>
public class VideoExporterRequestTests
{
    #region Dry run

    [Fact]
    public void A_master_with_alpha_plans_a_composite_over_50_percent_grey_by_default()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        string[] lines = plan.Split('\n');
        lines[0].Should().Be("source mode3 " + request.SourcePath);
        lines[1].Should().StartWith("decode av01 ");
        lines[1].Should().EndWith(" 8-bit alpha");
        lines[2].Should().Be("composite over colour #808080");
        lines[3].Should().Be("audio ours flac 48000 Hz 2 channel(s) -> aac 128 kbit/s");
        lines[4].Should().Be("write mp4 h264 ffmpeg engine-default keyint auto faststart rgba-input title none -> /nowhere/out.mp4");
        lines.Length.Should().Be(5);
    }

    [Fact]
    public void The_dry_run_says_the_same_thing_every_time()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.Backdrop = ExportBackdrop.FromVideo("/nowhere/backdrop.mkv");

        //Act
        string first = VideoExporter.DryRun(request);
        string second = VideoExporter.DryRun(request);

        //Assert
        first.Should().Be(second);
        first.Should().Contain("composite over video /nowhere/backdrop.mkv scaled-to ");
    }

    [Fact]
    public void A_master_without_alpha_plans_no_composite_and_notes_a_backdrop_it_ignores()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-flac.cbvmaster"));
        request.Backdrop = ExportBackdrop.FromColour(0x112233);

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().Contain("\nno composite - the source has no alpha plane\n");
        plan.Should().Contain("i420-input");
        plan.Should().EndWith("\nnote the source has no alpha plane; backdrop ignored");
    }

    [Fact]
    public void A_mode2_file_with_a_backdrop_notes_the_backdrop_is_ignored()
    {
        //Arrange
        VideoExportRequest request = Request(ExportTestMedia.RepositoryAsset("av1-vorbis.cbv"));
        request.Backdrop = ExportBackdrop.FromImage("/nowhere/picture.png");

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().StartWith("source mode2 ");
        plan.Should().Contain("\naudio ours vorbis ");
        plan.Should().Contain("note the source has no alpha plane; backdrop ignored");
    }

    [Fact]
    public void A_mode1_file_plans_its_opus_sound_into_aac_at_the_asked_rate()
    {
        //Arrange
        VideoExportRequest request = Request(ExportTestMedia.RepositoryAsset("av1-opus.webm"));
        request.Backdrop = ExportBackdrop.FromColour(0x000000);
        request.AudioBitrateKilobitsPerSecond = 96;
        request.Codec = VideoExportCodec.Hevc;
        request.Quality = VideoQuality.ConstantQuality(23);
        request.KeyframeIntervalFrames = 12;
        request.FastStart = false;
        request.Title = "Clip";

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().StartWith("source mode1 ");
        plan.Should().Contain("\ndecode av01 96x54 12 fps 8-bit no-alpha\n");
        plan.Should().Contain("\naudio ours opus 48000 Hz 1 channel(s) -> aac 96 kbit/s\n");
        plan.Should().Contain("\nwrite mp4 hevc ffmpeg cq 23 keyint 12 no-faststart i420-input title 'Clip' -> /nowhere/out.mp4");
        plan.Should().Contain("note the source has no alpha plane; backdrop ignored");
    }

    [Fact]
    public void Captions_and_chapters_are_noted_as_left_out()
    {
        //Arrange
        VideoExportRequest request = Request(ExportTestMedia.RepositoryAsset("av1-vorbis-captions-chapters.cbv"));

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().EndWith("\nnote " + VideoExporter.CaptionsNote);
    }

    [Fact]
    public void Without_audio_the_plan_says_none()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.IncludeAudio = false;

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().Contain("\naudio none (IncludeAudio is false)\n");
    }

    [Fact]
    public void An_rgba_backdrop_of_another_size_is_planned_as_scaled()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.Backdrop = ExportBackdrop.FromImageRgba(2, 2, new byte[16]);

        //Act
        string plan = VideoExporter.DryRun(request);

        //Assert
        plan.Should().Contain("\ncomposite over image rgba 2x2 scaled-to ");
    }

    #endregion

    #region Refusals

    [Fact]
    public void A_missing_source_is_refused_by_name()
    {
        //Arrange
        VideoExportRequest request = Request("/nowhere/missing.cbvmaster");

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(request));

        //Assert
        thrown.Message.Should().Contain("/nowhere/missing.cbvmaster");
        thrown.Message.Should().Contain("does not exist");
    }

    [Theory]
    [InlineData("/nowhere/out.mov")]
    [InlineData("/nowhere/out.webm")]
    [InlineData("/nowhere/out")]
    public void An_output_that_is_not_mp4_is_refused(string output)
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.OutputPath = output;

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => VideoExporter.Run(request));

        //Assert
        thrown.Message.Should().Contain("does not end in .mp4");
    }

    [Fact]
    public void A_null_request_is_refused()
    {
        //Act + Assert
        Assert.Throws<ArgumentNullException>(() => VideoExporter.Run(null));
        Assert.Throws<ArgumentNullException>(() => VideoExporter.DryRun(null));
    }

    [Fact]
    public void Hevc_on_an_engine_without_it_is_refused_naming_the_engine()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.Codec = VideoExportCodec.Hevc;
        IVideoProcessor engine = new NamedOnlyEngine("phone");

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => VideoExporter.Run(request, engine));

        //Assert
        thrown.Message.Should().Contain("'phone'");
        thrown.Message.Should().Contain("HevcOutput");
    }

    [Fact]
    public void An_alpha_master_on_an_engine_without_rgba_input_is_refused_naming_the_engine()
    {
        //Arrange
        ExportTestMedia.RegisterDecoders();
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        IVideoProcessor engine = new NamedOnlyEngine("phone");

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => VideoExporter.Run(request, engine));

        //Assert
        thrown.Message.Should().Contain("'phone'");
        thrown.Message.Should().Contain("RgbaInput");
    }

    [Fact]
    public void Negative_settings_are_refused()
    {
        //Arrange
        VideoExportRequest keyframes = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        keyframes.KeyframeIntervalFrames = -1;
        VideoExportRequest bitrate = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        bitrate.AudioBitrateKilobitsPerSecond = 0;

        //Act + Assert
        Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(keyframes)).Message.Should().Contain("KeyframeIntervalFrames");
        Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(bitrate)).Message.Should().Contain("AudioBitrateKilobitsPerSecond");
    }

    [Fact]
    public void Incomplete_backdrops_are_refused()
    {
        //Arrange
        VideoExportRequest image = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        image.Backdrop = new ExportBackdrop { Kind = ExportBackdropKind.Image };
        VideoExportRequest video = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        video.Backdrop = new ExportBackdrop { Kind = ExportBackdropKind.Video };
        VideoExportRequest rgba = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        rgba.Backdrop = new ExportBackdrop { Kind = ExportBackdropKind.Image, ImageRgba = new byte[12], ImageWidth = 2, ImageHeight = 2 };

        //Act + Assert
        Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(image)).Message.Should().Contain("ImagePath");
        Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(video)).Message.Should().Contain("VideoPath");
        Assert.Throws<VideoAuthoringException>(() => VideoExporter.DryRun(rgba)).Message.Should().Contain("width x height x 4");
        Assert.Throws<ArgumentException>(() => ExportBackdrop.FromImageRgba(2, 2, new byte[12]));
    }

    [Fact]
    public void A_missing_backdrop_video_is_refused_by_name()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-missing-backdrop");
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        request.OutputPath = work.File("out.mp4");
        request.Backdrop = ExportBackdrop.FromVideo("/nowhere/backdrop.mkv");

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => VideoExporter.Run(request));

        //Assert
        thrown.Message.Should().Contain("/nowhere/backdrop.mkv");
        System.IO.File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void The_missing_decoder_messages_name_what_to_register()
    {
        //Act + Assert - a registered decoder cannot be taken back out for one test, so the words are checked here
        ExportDecoding.DescribeMissingVideoDecoder("av01").Should().Contain("CodeBrixVideoPlaybackDav1d.Register()");
        ExportDecoding.DescribeMissingVideoDecoder("vp09").Should().Contain("VideoDecoders.Register(");
        ExportDecoding.DescribeMissingAudioDecoder("opus").Should().Contain("CodeBrixAudioOpus.Register()");
        ExportDecoding.DescribeMissingAudioDecoder("flac").Should().Contain("FlacPacketCodecFactory");
        ExportDecoding.DescribeMissingAudioDecoder("vorbis").Should().Contain("CodeBrix.Audio.Core");
        ExportDecoding.DescribeMissingAudioDecoder("aac").Should().Contain("SharedAudioOutput.RegisterPacketCodecFactory(");
        ExportDecoding.DescribeMissingAudioDecoder("opus").Should().Contain("IncludeAudio to false");
    }

    [Fact]
    public void A_cancelled_token_stops_the_export_before_it_starts()
    {
        //Arrange
        VideoExportRequest request = Request(AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"));
        using System.Threading.CancellationTokenSource cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        request.CancellationToken = cancellation.Token;

        //Act + Assert
        Assert.ThrowsAny<OperationCanceledException>(() => VideoExporter.Run(request));
    }

    #endregion

    #region Frame rates

    [Theory]
    [InlineData(1000000L, 10, 1)]
    [InlineData(333667L, 30000, 1001)]
    [InlineData(416667L, 24, 1)]
    [InlineData(417083L, 24000, 1001)]
    [InlineData(123457L, 10000000, 123457)]
    public void A_frame_duration_becomes_an_exact_rate(long ticks, int numerator, int denominator)
    {
        //Act
        CodeBrix.VideoProcessing.Core.Media.FrameRate rate = ExportSource.FrameRateFor(TimeSpan.FromTicks(ticks));

        //Assert
        rate.Should().Be(new CodeBrix.VideoProcessing.Core.Media.FrameRate(numerator, denominator));
    }

    #endregion

    private static VideoExportRequest Request(string source) => new VideoExportRequest
    {
        SourcePath = source,
        OutputPath = "/nowhere/out.mp4",
    };
}
