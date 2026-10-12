using System;
using System.Collections.Generic;
using CodeBrix.Audio.FlacWriter;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Output;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The pieces of the in-process passes that need no source to test: which engine counts as what, which settings
/// shape the picture (the prepare pass, or the in-process shaping on another engine), how a rate factor maps onto the encoder, how lock step is judged, and what a FLAC
/// stream header must look like to be stored.
/// </summary>
public class InProcessAuthoringTests
{
    [Fact]
    public void A_request_the_source_can_be_read_for_as_it_stands_has_no_prepare_pass()
    {
        //Arrange
        List<string> needs;
        VideoAuthoringRequest request = DryRequest();

        //Act
        needs = InProcessAuthoring.SettingsNeedingPreparePass(request, false);

        //Assert
        needs.Count.Should().Be(0);
    }

    [Fact]
    public void Every_setting_that_needs_the_prepare_pass_is_named()
    {
        //Arrange
        VideoAuthoringRequest request = DryRequest();
        request.Video.FrameSize = AuthoringFrameSize.LongSide(720);
        request.Video.FrameRate = 30;

        //Act
        List<string> needs = InProcessAuthoring.SettingsNeedingPreparePass(request, true);

        //Assert
        needs.Should().Equal("FrameSize", "FrameRate/FrameRateMode", "Luts");
    }

    [Fact]
    public void An_engine_other_than_ffmpeg_needs_nothing_installed()
    {
        //Arrange
        IVideoProcessor engine = new NamedOnlyEngine("phone");

        //Act
        bool available = InProcessAuthoring.TryVerifyEngine(engine, out string problem);

        //Assert
        available.Should().BeTrue();
        problem.Should().BeEmpty();
        InProcessAuthoring.IsFFmpegEngine(engine).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 24)]
    [InlineData(63, 51)]
    public void A_rate_factor_maps_to_the_nearest_core_quality_level(int constantRateFactor, int expected)
    {
        //Arrange
        int factor = constantRateFactor;

        //Act
        VideoQuality quality = InProcessAuthoring.QualityFor(factor);

        //Assert
        quality.QualityLevel.Should().Be(expected);
    }

    [Fact]
    public void Lock_step_is_described_by_frame_count_and_by_key_frame()
    {
        //Arrange
        bool[] picture = { true, false, false, true, false };

        //Act
        string same = InProcessAuthoring.DescribeLockStepMismatch(picture, 5, new[] { true, false, false, true, false });
        string fewer = InProcessAuthoring.DescribeLockStepMismatch(picture, 4, new[] { true, false, false, true });
        string moved = InProcessAuthoring.DescribeLockStepMismatch(picture, 5, new[] { true, false, true, false, false });

        //Assert
        same.Should().BeNull();
        fewer.Should().Be("the video pass wrote 5 frame(s) and the alpha pass 4.");
        moved.Should().Be("frame 2 is a key frame in the alpha pass and not in the video pass.");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void The_flac_writers_stream_header_is_the_codec_private_data_a_flac_track_needs(int bitDepth)
    {
        //Arrange
        float[] tone = new float[48000 * 2];
        for (int i = 0; i < 48000; i++)
        {
            float sample = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / 48000f);
            tone[i * 2] = sample;
            tone[(i * 2) + 1] = -sample;
        }

        using FlacPacketEncoder encoder = new FlacPacketEncoder(2, 48000, new FlacEncoderOptions { BitsPerSample = bitDepth });

        //Act
        encoder.Encode(tone);
        encoder.Finish();
        byte[] header = encoder.StreamHeader.ToArray();
        string problem = InProcessAuthoring.DescribeFlacCodecPrivateProblem(header, 48000, 2, bitDepth, 48000);
        string stale = InProcessAuthoring.DescribeFlacCodecPrivateProblem(header, 48000, 2, bitDepth, 47999);

        //Assert
        problem.Should().BeNull();
        stale.Should().Contain("not the final one");
        header[4].Should().Be((byte)0x00);
    }

    [Fact]
    public void A_header_that_does_not_open_with_streaminfo_is_described()
    {
        //Arrange
        byte[] header = new byte[4 + 4 + 34];
        header[0] = (byte)'f';
        header[1] = (byte)'L';
        header[2] = (byte)'a';
        header[3] = (byte)'C';
        header[4] = 0x84;
        header[7] = 34;

        //Act
        string problem = InProcessAuthoring.DescribeFlacCodecPrivateProblem(header, 48000, 2, 16, 0);

        //Assert
        problem.Should().Contain("not the 34-byte STREAMINFO");
    }

    [Fact]
    public void On_the_desktop_engine_the_master_flavour_needs_only_ffmpeg_and_ffprobe()
    {
        //Arrange
        Assert.SkipUnless(CbvAuthor.TryVerifyTools(out string missing), missing);

        //Act
        bool available = InProcessAuthoring.TryVerifyEngine(CodeBrixVideoProcessing.Processor, out string problem);

        //Assert
        available.Should().BeTrue(problem);
        InProcessAuthoring.IsFFmpegEngine(CodeBrixVideoProcessing.Processor).Should().BeTrue();
    }

    [Theory]
    [InlineData(129, 72, "the picture is 129x72, and 4:2:0 AV1 needs even dimensions, so the last column was cropped away (not resampled) and it was encoded at 128x72.")]
    [InlineData(128, 73, "the picture is 128x73, and 4:2:0 AV1 needs even dimensions, so the last row was cropped away (not resampled) and it was encoded at 128x72.")]
    [InlineData(129, 73, "the picture is 129x73, and 4:2:0 AV1 needs even dimensions, so the last column and the last row were cropped away (not resampled) and it was encoded at 128x72.")]
    public void A_crop_note_says_exactly_what_was_cropped(int width, int height, string expectedStart)
    {
        //Arrange
        int w = width;

        //Act
        string note = InProcessAuthoring.DescribeCrop(w, height);

        //Assert
        note.Should().StartWith(expectedStart);
    }

    [Fact]
    public void An_even_picture_earns_no_crop_note()
    {
        //Arrange
        int width = 128;

        //Act
        string note = InProcessAuthoring.DescribeCrop(width, 72);

        //Assert
        note.Should().BeNull();
    }

    [Fact]
    public void A_picture_one_pixel_wide_cannot_be_cropped_to_even()
    {
        //Arrange
        int width = 1;

        //Act
        Action check = () => InProcessAuthoring.RequireCroppableSize(width, 72, "thin.mkv");

        //Assert
        check.Should().Throw<VideoAuthoringException>().WithMessage("*1x72*nothing left*");
    }

    [Fact]
    public void A_cropped_frame_keeps_the_top_left_samples_and_the_strides()
    {
        //Arrange
        byte[] y = new byte[5 * 3];
        for (int i = 0; i < y.Length; i++) y[i] = (byte)i;
        byte[] u = new byte[3 * 2];
        byte[] v = new byte[3 * 2];
        for (int i = 0; i < u.Length; i++)
        {
            u[i] = (byte)(100 + i);
            v[i] = (byte)(200 + i);
        }

        CodeBrix.VideoProcessing.Core.Frames.VideoFrameData frame = CodeBrix.VideoProcessing.Core.Frames.VideoFrameData.CreateI420(
            5,
            3,
            CodeBrix.VideoProcessing.Core.Frames.VideoPlane.FromArray(y, 5, 3),
            CodeBrix.VideoProcessing.Core.Frames.VideoPlane.FromArray(u, 3, 2),
            CodeBrix.VideoProcessing.Core.Frames.VideoPlane.FromArray(v, 3, 2),
            TimeSpan.Zero,
            0);

        //Act
        CodeBrix.VideoProcessing.Core.Frames.VideoFrameData cropped = InProcessAuthoring.CropI420(frame, 4, 2);

        //Assert
        cropped.Width.Should().Be(4);
        cropped.Height.Should().Be(2);
        cropped.Y.GetRow(1).ToArray().Should().Equal((byte)5, (byte)6, (byte)7, (byte)8);
        cropped.U.GetRow(0).ToArray().Should().Equal((byte)100, (byte)101);
        cropped.V.Rows.Should().Be(1);
    }

    private static VideoAuthoringRequest DryRequest()
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = "/nowhere/source.mov",
            OutputPath = "/nowhere/clip.cbvmaster",
            TemporaryFolder = "/nowhere/work",
        };

        request.Video.KeyframeIntervalFrames = 48;
        return request;
    }
}
