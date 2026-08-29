using System;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Decoding;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the uncompressed video format: what a descriptor says, how big a frame is, and that a descriptor
/// written out reads back the same.
/// </summary>
public class RawVideoFormatTests
{
    [Fact]
    public void A_descriptor_reads_back_exactly_what_was_written()
    {
        //Arrange
        RawVideoDescriptor original = new RawVideoDescriptor(
            96,
            54,
            10,
            VideoPixelLayout.I422,
            new VideoColorInfo(
                VideoColorPrimaries.Bt2020,
                VideoTransferCharacteristics.SmpteSt2084,
                VideoMatrixCoefficients.Bt2020NonConstantLuminance,
                VideoColorRange.Full,
                VideoChromaSiting.Colocated));

        //Act
        byte[] bytes = RawVideoFormat.CreateDescriptor(original);
        bool parsed = RawVideoFormat.TryParseDescriptor(bytes, out RawVideoDescriptor readBack);

        //Assert
        parsed.Should().BeTrue();
        bytes.Length.Should().Be(RawVideoFormat.DescriptorLength);
        readBack.Width.Should().Be(96);
        readBack.Height.Should().Be(54);
        readBack.BitDepth.Should().Be(10);
        readBack.Layout.Should().Be(VideoPixelLayout.I422);
        readBack.Color.Should().Be(original.Color);
    }

    [Theory]
    [InlineData(VideoPixelLayout.I420, 8, 64, 36, 3456)]
    [InlineData(VideoPixelLayout.I422, 8, 64, 36, 4608)]
    [InlineData(VideoPixelLayout.I444, 8, 64, 36, 6912)]
    [InlineData(VideoPixelLayout.Gray, 8, 64, 36, 2304)]
    [InlineData(VideoPixelLayout.I420, 10, 64, 36, 6912)]
    public void A_frame_is_as_many_bytes_as_its_planes_need(
        VideoPixelLayout layout,
        int bitDepth,
        int width,
        int height,
        long expected)
    {
        //Arrange
        RawVideoDescriptor descriptor = new RawVideoDescriptor(width, height, bitDepth, layout, VideoColorInfo.Unspecified);

        //Act
        long bytes = RawVideoFormat.GetFrameByteCount(descriptor);

        //Assert
        bytes.Should().Be(expected);
    }

    [Fact]
    public void An_odd_size_rounds_its_chroma_planes_up()
    {
        //Arrange
        RawVideoDescriptor descriptor = new RawVideoDescriptor(37, 21, 8, VideoPixelLayout.I420, VideoColorInfo.Unspecified);

        //Act
        int chromaWidth = RawVideoFormat.GetPlaneWidth(descriptor, 1);
        int chromaHeight = RawVideoFormat.GetPlaneHeight(descriptor, 1);

        //Assert
        chromaWidth.Should().Be(19);
        chromaHeight.Should().Be(11);
    }

    [Fact]
    public void Data_that_is_not_a_descriptor_is_refused_rather_than_guessed_at()
    {
        //Arrange
        byte[] wrong = new byte[RawVideoFormat.DescriptorLength];

        //Act
        bool parsed = RawVideoFormat.TryParseDescriptor(wrong, out RawVideoDescriptor _);

        //Assert
        parsed.Should().BeFalse();
    }

    [Fact]
    public void A_descriptor_that_describes_nothing_decodable_is_refused()
    {
        //Arrange
        RawVideoDescriptor descriptor = new RawVideoDescriptor(0, 0, 8, VideoPixelLayout.I420, VideoColorInfo.Unspecified);

        //Act
        Action act = () => RawVideoFormat.CreateDescriptor(descriptor);

        //Assert
        act.Should().Throw<ArgumentException>();
        descriptor.IsValid.Should().BeFalse();
    }
}
