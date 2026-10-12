using System;
using System.Text.Json;
using CodeBrix.VideoPlayback.Authoring.Export;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// A master with alpha composited over a colour, an image and a video, checked pixel by pixel in what ffmpeg decodes
/// from the MP4 - and the length and sound rules a video backdrop brings with it.
/// </summary>
/// <remarks>
/// The master's LEFT half is fully transparent and its RIGHT half opaque white, so the left half shows the backdrop
/// exactly and the right half never does. Every colour is checked to within a tolerance that covers AV1, H.264 and
/// two colour conversions.
/// </remarks>
public class VideoExporterBackdropTests : IClassFixture<ExportFixture>
{
    private const int Tolerance = 14;
    private const int LeftX = ExportTestMedia.Width / 4;
    private const int RightX = ExportTestMedia.Width * 3 / 4;
    private const int MiddleY = ExportTestMedia.Height / 2;

    private readonly ExportFixture fixture;

    /// <summary>Creates the class.</summary>
    /// <param name="fixture">The shared masters.</param>
    public VideoExporterBackdropTests(ExportFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public void The_default_backdrop_is_50_percent_grey()
    {
        //Arrange
        ExportTestMedia.Require();
        VideoExportRequest request = Request(fixture.Halves, "default-grey.mp4");

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert
        Near(ExportTestMedia.Mean(rgb, 3, result.Width, result.Height, LeftX, MiddleY), 128, 128, 128);
        Near(ExportTestMedia.Mean(rgb, 3, result.Width, result.Height, RightX, MiddleY), 255, 255, 255);
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
    }

    [Fact]
    public void A_colour_backdrop_shows_through_the_transparent_half()
    {
        //Arrange
        ExportTestMedia.Require();
        VideoExportRequest request = Request(fixture.Halves, "blue.mp4");
        request.Backdrop = ExportBackdrop.FromColour(0x2040C0);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert
        Near(ExportTestMedia.Mean(rgb, 5, result.Width, result.Height, LeftX, MiddleY), 0x20, 0x40, 0xC0);
        Near(ExportTestMedia.Mean(rgb, 5, result.Width, result.Height, RightX, MiddleY), 255, 255, 255);
    }

    [Fact]
    public void A_half_transparent_picture_blends_with_the_colour_under_it()
    {
        //Arrange
        ExportTestMedia.Require();
        VideoExportRequest request = Request(fixture.HalfRed, "blend.mp4");
        request.Backdrop = ExportBackdrop.FromColour(0x0000FF);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert - red at alpha 128 over blue is (255*128 + 0*127) / 255 red and (0*128 + 255*127) / 255 blue
        Near(ExportTestMedia.Mean(rgb, 4, result.Width, result.Height, ExportTestMedia.Width / 2, MiddleY), 128, 0, 127);
        result.AudioSource.Should().Be(VideoExportAudioSource.None);
    }

    [Fact]
    public void An_image_backdrop_of_another_size_is_scaled_to_the_picture()
    {
        //Arrange
        ExportTestMedia.Require();
        string png = ExportTestMedia.WritePng(fixture.File("backdrop.png"), 40, 30, 200, 160, 20);
        VideoExportRequest request = Request(fixture.Halves, "image.mp4");
        request.Backdrop = ExportBackdrop.FromImage(png);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert
        Near(ExportTestMedia.Mean(rgb, 2, result.Width, result.Height, LeftX, MiddleY), 200, 160, 20);
        Near(ExportTestMedia.Mean(rgb, 2, result.Width, result.Height, RightX, MiddleY), 255, 255, 255);
        result.Notes.Should().Contain(n => n.Contains("backdrop image is 40x30 and was scaled to 128x72"));
    }

    [Fact]
    public void Rgba_pixels_from_the_callers_own_decoder_make_a_backdrop()
    {
        //Arrange
        ExportTestMedia.Require();
        byte[] pixels = new byte[ExportTestMedia.Width * ExportTestMedia.Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 30;
            pixels[i + 1] = 200;
            pixels[i + 2] = 90;
            pixels[i + 3] = 255;
        }

        VideoExportRequest request = Request(fixture.Halves, "rgba.mp4");
        request.Backdrop = ExportBackdrop.FromImageRgba(ExportTestMedia.Width, ExportTestMedia.Height, pixels);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert
        Near(ExportTestMedia.Mean(rgb, 1, result.Width, result.Height, LeftX, MiddleY), 30, 200, 90);
    }

    [Fact]
    public void A_longer_backdrop_video_plays_on_alone_and_its_sound_is_not_used()
    {
        //Arrange
        ExportTestMedia.Require();
        string clip = ExportTestMedia.WriteBackdropClip(fixture.File("green-long.mkv"), "0x00FF00", 64, 36, 2, true);
        VideoExportRequest request = Request(fixture.Halves, "video-long.mp4");
        request.Backdrop = ExportBackdrop.FromVideo(clip);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert - ours lasts 10 frames; frames 10..19 are the backdrop alone, green across the whole picture
        result.FramesWritten.Should().Be(20);
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        Near(ExportTestMedia.Mean(rgb, 4, result.Width, result.Height, LeftX, MiddleY), 0, 255, 0);
        Near(ExportTestMedia.Mean(rgb, 4, result.Width, result.Height, RightX, MiddleY), 255, 255, 255);
        Near(ExportTestMedia.Mean(rgb, 15, result.Width, result.Height, RightX, MiddleY), 0, 255, 0);
        Near(ExportTestMedia.Mean(rgb, 15, result.Width, result.Height, LeftX, MiddleY), 0, 255, 0);
        ((int)ExportTestMedia.Number(ExportTestMedia.Stream(probe, "audio").Value, "sample_rate")).Should().Be(48000);
        result.Notes.Should().Contain(n => n.Contains("backdrop video is 64x36 and was scaled to 128x72"));
        result.Notes.Should().Contain(n => n.Contains("plays on alone for 10 more picture(s)"));
    }

    [Fact]
    public void A_shorter_backdrop_video_leaves_ours_over_grey_not_over_the_colour_setting()
    {
        //Arrange
        ExportTestMedia.Require();
        string clip = ExportTestMedia.WriteBackdropClip(fixture.File("green-short.mkv"), "0x00FF00", 128, 72, 0.5, false);
        VideoExportRequest request = Request(fixture.Halves, "video-short.mp4");
        request.Backdrop = ExportBackdrop.FromVideo(clip);
        request.Backdrop.Colour = 0xFF0000;

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        byte[] rgb = ExportTestMedia.DecodeRgb(result.OutputPath, fixture.Folder);

        //Assert
        result.FramesWritten.Should().Be(10);
        Near(ExportTestMedia.Mean(rgb, 2, result.Width, result.Height, LeftX, MiddleY), 0, 255, 0);
        Near(ExportTestMedia.Mean(rgb, 8, result.Width, result.Height, LeftX, MiddleY), 128, 128, 128);
        result.Notes.Should().Contain(n => n.Contains("ended first; the last 5 picture(s) were composited over 50% grey"));
    }

    [Fact]
    public void A_silent_master_takes_the_backdrop_videos_sound_and_silence_follows_it()
    {
        //Arrange
        ExportTestMedia.Require();
        string clip = ExportTestMedia.WriteBackdropClip(fixture.File("green-short-sound.mkv"), "0x00FF00", 128, 72, 0.5, true);
        VideoExportRequest request = Request(fixture.SilentHalves, "silent-video.mp4");
        request.Backdrop = ExportBackdrop.FromVideo(clip);

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);
        float[] sound = ExportTestMedia.DecodeMono(result.OutputPath, fixture.Folder);

        //Assert - the backdrop's 44.1 kHz tone for its half second, silence for the rest
        result.AudioSource.Should().Be(VideoExportAudioSource.Backdrop);
        ((int)ExportTestMedia.Number(ExportTestMedia.Stream(probe, "audio").Value, "sample_rate")).Should().Be(44100);
        double tone = ExportTestMedia.Rms(sound, 4410, 11025);
        (tone > 0.05).Should().BeTrue("the tone should be heard, level " + tone);
        (ExportTestMedia.Rms(sound, 30870, 8820) < 0.01).Should().BeTrue();
        result.Notes.Should().Contain("the source is flagged silent, so the backdrop video's sound was used");
    }

    [Fact]
    public void A_master_without_an_audio_track_counts_as_silent()
    {
        //Arrange
        ExportTestMedia.Require();
        string clip = ExportTestMedia.WriteBackdropClip(fixture.File("green-sound.mkv"), "0x00FF00", 128, 72, 1, true);
        VideoExportRequest request = Request(fixture.MutedHalves, "muted-video.mp4");
        request.Backdrop = ExportBackdrop.FromVideo(clip);

        //Act
        VideoExportResult result = VideoExporter.Run(request);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.Backdrop);
        result.Notes.Should().Contain("the source has no audio track, so the backdrop video's sound was used");
    }

    [Fact]
    public void A_silent_master_over_a_colour_has_no_audio_track()
    {
        //Arrange
        ExportTestMedia.Require();
        VideoExportRequest request = Request(fixture.SilentHalves, "silent-colour.mp4");

        //Act
        VideoExportResult result = VideoExporter.Run(request);
        JsonElement probe = ExportTestMedia.Probe(result.OutputPath);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.None);
        ExportTestMedia.Stream(probe, "audio").HasValue.Should().BeFalse();
        result.Notes.Should().Contain(n => n.Contains("a colour backdrop has no sound"));
    }

    [Fact]
    public void A_backdrop_video_without_sound_under_a_silent_master_gives_no_audio_track()
    {
        //Arrange
        ExportTestMedia.Require();
        string clip = ExportTestMedia.WriteBackdropClip(fixture.File("green-quiet.mkv"), "0x00FF00", 128, 72, 1, false);
        VideoExportRequest request = Request(fixture.SilentHalves, "silent-quiet.mp4");
        request.Backdrop = ExportBackdrop.FromVideo(clip);

        //Act
        VideoExportResult result = VideoExporter.Run(request);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.None);
        ExportTestMedia.Stream(ExportTestMedia.Probe(result.OutputPath), "audio").HasValue.Should().BeFalse();
    }

    private VideoExportRequest Request(string source, string output) => new VideoExportRequest
    {
        SourcePath = source,
        OutputPath = fixture.File(output),
        Quality = CodeBrix.VideoProcessing.Core.Output.VideoQuality.ConstantQuality(10),
    };

    private static void Near((int R, int G, int B) actual, int r, int g, int b)
    {
        (Math.Abs(actual.R - r) <= Tolerance && Math.Abs(actual.G - g) <= Tolerance && Math.Abs(actual.B - b) <= Tolerance)
            .Should().BeTrue("expected about (" + r + ", " + g + ", " + b + ") but was " + actual);
    }
}
