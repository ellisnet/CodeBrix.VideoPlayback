using System;
using System.Collections.Generic;
using System.Text.Json;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Export;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// Every flavour exports to an MP4 that ffprobe - which has never heard of CBVF - reads as H.264 and AAC at the
/// source's size and length, with its index in front and its title tag. Exporting needs no audio device: see
/// <see cref="ExportWithoutAudioDeviceTests" />.
/// </summary>
public class VideoExporterModeTests
{
    [Fact]
    public void A_master_with_alpha_exports_to_h264_and_aac_with_faststart_and_a_title()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-mode3");
        List<AuthoringProgress> progress = new List<AuthoringProgress>();
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"),
            OutputPath = work.File("mode3.mp4"),
            Title = "Mode three",
            ProgressCallback = progress.Add,
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.EngineName.Should().Be("ffmpeg");
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        result.CodecUsed.Should().Be(VideoExportCodec.H264);
        JsonElement video = ExportTestMedia.Stream(probe, "video").Value;
        JsonElement audio = ExportTestMedia.Stream(probe, "audio").Value;
        video.GetProperty("codec_name").GetString().Should().Be("h264");
        audio.GetProperty("codec_name").GetString().Should().Be("aac");
        video.GetProperty("width").GetInt32().Should().Be(result.Width);
        video.GetProperty("height").GetInt32().Should().Be(result.Height);
        CloseTo(ExportTestMedia.Number(video, "duration"), result.Duration.TotalSeconds, result.FrameRate.FrameDuration.TotalSeconds);
        CloseTo(result.Duration.TotalSeconds, 1.0, result.FrameRate.FrameDuration.TotalSeconds);
        ExportTestMedia.MoovBeforeMdat(result.OutputPath).Should().BeTrue();
        ExportTestMedia.Title(probe).Should().Be("Mode three");
        progress.Count.Should().BeGreaterThan(0);
        progress[progress.Count - 1].Percent.Should().Be(100);
        progress[0].Label.Should().Be(VideoExporter.PassLabel);
    }

    [Fact]
    public void A_master_without_alpha_exports_and_ignores_its_backdrop_with_a_note()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-mode3-no-alpha");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = AuthoringTestAssets.Path_("av1-flac.cbvmaster"),
            OutputPath = work.File("mode3-opaque.mp4"),
            Backdrop = ExportBackdrop.FromColour(0xFF0000),
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.Notes.Should().Contain(VideoExporter.BackdropIgnoredNote);
        ExportTestMedia.Stream(probe, "video").Value.GetProperty("codec_name").GetString().Should().Be("h264");
        ExportTestMedia.Stream(probe, "audio").Value.GetProperty("codec_name").GetString().Should().Be("aac");
    }

    [Fact]
    public void A_mode2_file_exports_its_vorbis_sound_as_aac()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-mode2");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-vorbis.cbv"),
            OutputPath = work.File("mode2.mp4"),
            Backdrop = ExportBackdrop.FromColour(0x00FF00),
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.Notes.Should().Contain(VideoExporter.BackdropIgnoredNote);
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        JsonElement video = ExportTestMedia.Stream(probe, "video").Value;
        video.GetProperty("codec_name").GetString().Should().Be("h264");
        video.GetProperty("width").GetInt32().Should().Be(96);
        video.GetProperty("height").GetInt32().Should().Be(54);
        ExportTestMedia.Stream(probe, "audio").Value.GetProperty("codec_name").GetString().Should().Be("aac");
        CloseTo(ExportTestMedia.Number(video, "duration"), 1.0, result.FrameRate.FrameDuration.TotalSeconds);
        ExportTestMedia.MoovBeforeMdat(result.OutputPath).Should().BeTrue();
    }

    [Fact]
    public void A_mode1_file_exports_its_opus_sound_as_aac()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-mode1");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-opus.webm"),
            OutputPath = work.File("mode1.mp4"),
        };

        //Act
        VideoExportResult result;
        using (ExportTestMedia.UseOpusDecoder()) result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);
        float[] sound = ExportTestMedia.DecodeMono(result.OutputPath, work.Path);

        //Assert
        result.FramesWritten.Should().Be(12);
        result.FrameRate.Should().Be(new CodeBrix.VideoProcessing.Core.Media.FrameRate(12, 1));
        JsonElement video = ExportTestMedia.Stream(probe, "video").Value;
        JsonElement audio = ExportTestMedia.Stream(probe, "audio").Value;
        video.GetProperty("codec_name").GetString().Should().Be("h264");
        audio.GetProperty("codec_name").GetString().Should().Be("aac");
        audio.GetProperty("channels").GetInt32().Should().Be(1);
        CloseTo(ExportTestMedia.Number(video, "duration"), 1.0, 1d / 12);
        double level = ExportTestMedia.Rms(sound, 4800, 19200);
        (level > 0.05).Should().BeTrue("the tone should be heard, level " + level);
    }

    [Fact]
    public void A_mode1_file_with_vorbis_exports_through_the_built_in_decoder()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-mode1-vorbis");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-vorbis.webm"),
            OutputPath = work.File("mode1-vorbis.mp4"),
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        ExportTestMedia.Stream(probe, "video").Value.GetProperty("codec_name").GetString().Should().Be("h264");
        ExportTestMedia.Stream(probe, "audio").Value.GetProperty("codec_name").GetString().Should().Be("aac");
        CodeBrix.Audio.Opus.CodeBrixAudioOpus.IsRegistered.Should().BeFalse();
    }

    [Fact]
    public void A_file_with_captions_and_chapters_exports_and_notes_they_were_left_out()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-captions");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-vorbis-captions-chapters.cbv"),
            OutputPath = work.File("captions.mp4"),
            IncludeAudio = false,
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.Notes.Should().Contain(VideoExporter.CaptionsNote);
        result.AudioSource.Should().Be(VideoExportAudioSource.None);
        ExportTestMedia.Stream(probe, "audio").HasValue.Should().BeFalse();
        ExportTestMedia.Stream(probe, "subtitle").HasValue.Should().BeFalse();
    }

    [Fact]
    public void Hevc_is_written_when_asked_on_an_engine_that_has_it()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-hevc");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-opus.webm"),
            OutputPath = work.File("hevc.mp4"),
            Codec = VideoExportCodec.Hevc,
            IncludeAudio = false,
            FastStart = false,
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.CodecUsed.Should().Be(VideoExportCodec.Hevc);
        ExportTestMedia.Stream(probe, "video").Value.GetProperty("codec_name").GetString().Should().Be("hevc");
        ExportTestMedia.MoovBeforeMdat(result.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void A_ten_bit_picture_is_reduced_to_eight_bits_with_a_note()
    {
        //Arrange - a 10-bit AV1 WebM made by ffmpeg's own encoder from a solid colour
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-ten-bit");
        string source = work.File("ten-bit.webm");
        string encoder = AuthoringEncoders.HasAomAv1Encoder ? "libaom-av1 -cpu-used 8 -crf 20 -b:v 0" : "libsvtav1 -preset 12 -crf 20";
        CodeBrix.VideoProcessing.FFMpegArguments
            .FromFileInput("color=c=0x2040C0:size=96x64:rate=10:duration=1", false, input => input.ForceFormat("lavfi"))
            .OutputToFile(source, true, output => output
                .WithCustomArgument("-c:v " + encoder + " -pix_fmt yuv420p10le")
                .ForceFormat("webm"))
            .ProcessSynchronously();
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = source,
            OutputPath = work.File("eight-bit.mp4"),
            Quality = CodeBrix.VideoProcessing.Core.Output.VideoQuality.ConstantQuality(10),
        };

        //Act
        string plan = VideoExporter.DryRun(request);
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, work.Path);
        (int R, int G, int B) mean = ExportTestMedia.Mean(rgb, 5, 96, 64, 48, 32);

        //Assert
        plan.Should().Contain(" 10-bit reduced-to-8-bit no-alpha");
        result.Notes.Should().Contain("the picture is 10-bit; MP4 exports are 8-bit, so it was reduced to 8 bits");
        ExportTestMedia.Stream(ExportTestMedia.Probe(result.OutputPath), "video").Value
            .GetProperty("pix_fmt").GetString().Should().Be("yuv420p");
        (Math.Abs(mean.R - 0x20) <= 10 && Math.Abs(mean.G - 0x40) <= 10 && Math.Abs(mean.B - 0xC0) <= 10)
            .Should().BeTrue("expected about (32, 64, 192) but was " + mean);
    }

    [Fact]
    public void A_cancelled_export_leaves_no_output()
    {
        //Arrange
        ExportTestMedia.Require();
        using WorkFolder work = new WorkFolder("export-cancel");
        using System.Threading.CancellationTokenSource cancellation = new System.Threading.CancellationTokenSource();
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = AuthoringTestAssets.Path_("av1-alpha-flac.cbvmaster"),
            OutputPath = work.File("cancelled.mp4"),
            CancellationToken = cancellation.Token,
            ProgressCallback = p => { if (p.Percent >= 20) cancellation.Cancel(); },
        };

        //Act
        Assert.ThrowsAny<OperationCanceledException>(() => VideoExporter.Run(request));

        //Assert
        System.IO.File.Exists(request.OutputPath).Should().BeFalse();
    }

    private static void CloseTo(double actual, double expected, double tolerance)
    {
        (Math.Abs(actual - expected) <= tolerance + 1e-6).Should().BeTrue(
            "expected " + expected + " within " + tolerance + " but was " + actual);
    }
}
