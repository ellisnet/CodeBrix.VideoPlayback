using System;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Pins <see cref="FrameRotator" /> to FFmpeg on a whole ten-bit 4:2:0 frame and checks its refusals.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class FrameRotatorTests
{
    [Theory]
    [InlineData(RotationAngle.Clockwise90, false, false)]
    [InlineData(RotationAngle.Clockwise270, true, false)]
    [InlineData(RotationAngle.Rotate180, false, true)]
    public void Rotate_turns_every_plane_of_a_ten_bit_i420_frame_as_ffmpeg_does(RotationAngle angle, bool flipHorizontal, bool flipVertical)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        const int Width = 16;
        const int Height = 10;
        byte[] y = PlaneRotatorTests.Distinct(Width, Height, 2, 0);
        byte[] u = PlaneRotatorTests.Distinct(Width / 2, Height / 2, 2, 100);
        byte[] v = PlaneRotatorTests.Distinct(Width / 2, Height / 2, 2, 200);
        byte[] packed = Concat(y, u, v);

        PlaneRotator.GetRotatedSize(Width, Height, angle, out int width, out int height);
        byte[] outY = new byte[y.Length];
        byte[] outU = new byte[u.Length];
        byte[] outV = new byte[v.Length];

        //Act
        byte[] expected = FfmpegProcessingOracle.FilterRaw(
            packed, "yuv420p10le", Width, Height, PlaneRotatorTests.FfmpegChain(angle, flipHorizontal, flipVertical), "yuv420p10le");

        FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateI420(Width, Height, 10, y, Width * 2, u, Width, v, Width),
            PlanarFrame.CreateI420(width, height, 10, outY, width * 2, outU, width, outV, width),
            angle,
            flipHorizontal,
            flipVertical);

        //Assert
        Concat(outY, outU, outV).Should().Equal(expected);
    }

    [Fact]
    public void Rotate_carries_the_alpha_plane_with_the_picture()
    {
        //Arrange
        byte[] y = PlaneRotatorTests.Distinct(5, 3, 1, 0);
        byte[] a = PlaneRotatorTests.Distinct(5, 3, 1, 50);
        byte[] outY = new byte[15];
        byte[] outA = new byte[15];
        byte[] expectedA = new byte[15];

        //Act
        FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateGray(5, 3, 8, y, 5, a, 5),
            PlanarFrame.CreateGray(3, 5, 8, outY, 3, outA, 3),
            RotationAngle.Clockwise270);
        PlaneRotator.Rotate(a, 5, 3, 5, expectedA, 3, 1, 1, RotationAngle.Clockwise270);

        //Assert
        outA.Should().Equal(expectedA);
    }

    [Fact]
    public void Rotate_refuses_a_quarter_turn_of_an_odd_sized_i420_frame_by_naming_the_dimension()
    {
        //Arrange
        byte[] y = new byte[33 * 24];
        byte[] chroma = new byte[17 * 12];

        //Act
        Exception quarter = Record.Exception(() => FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateI420(33, 24, 8, y, 33, chroma, 17, chroma, 17),
            PlanarFrame.CreateI420(24, 33, 8, new byte[33 * 24], 24, new byte[12 * 17], 12, new byte[12 * 17], 12),
            RotationAngle.Clockwise90));

        Exception half = Record.Exception(() => FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateI420(33, 24, 8, y, 33, chroma, 17, chroma, 17),
            PlanarFrame.CreateI420(33, 24, 8, new byte[33 * 24], 33, new byte[17 * 12], 17, new byte[17 * 12], 17),
            RotationAngle.Rotate180,
            true));

        //Assert
        quarter.Should().BeOfType<ArgumentException>();
        quarter.Message.Should().Contain("width 33");
        quarter.Message.Should().Contain("odd");
        half.Should().BeNull();
        FrameRotator.CanRotate(ProcessingFrameLayout.I420, 33, 24, RotationAngle.Clockwise270).Should().BeFalse();
        FrameRotator.CanRotate(ProcessingFrameLayout.I420, 32, 24, RotationAngle.Clockwise270).Should().BeTrue();
    }

    [Fact]
    public void Rotate_refuses_a_quarter_turn_of_i422_as_not_supported()
    {
        //Arrange
        byte[] y = new byte[8 * 4];
        byte[] chroma = new byte[4 * 4];

        //Act
        Exception thrown = Record.Exception(() => FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateI422(8, 4, 8, y, 8, chroma, 4, chroma, 4),
            PlanarFrame.CreateI422(4, 8, 8, new byte[32], 4, new byte[16], 2, new byte[16], 2),
            RotationAngle.Clockwise90));

        //Assert
        thrown.Should().BeOfType<NotSupportedException>();
        thrown.Message.Should().Contain("4:2:2");
    }

    [Fact]
    public void Rotate_refuses_a_destination_of_the_wrong_size_or_kind()
    {
        //Arrange
        byte[] y = new byte[8 * 4];

        //Act
        Exception size = Record.Exception(() => FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateGray(8, 4, 8, y, 8),
            PlanarFrame.CreateGray(8, 4, 8, new byte[32], 8),
            RotationAngle.Clockwise90));

        Exception depth = Record.Exception(() => FrameRotator.Rotate(
            ReadOnlyPlanarFrame.CreateGray(8, 4, 8, y, 8),
            PlanarFrame.CreateGray(8, 4, 10, new byte[64], 16),
            RotationAngle.None));

        //Assert
        size.Should().BeOfType<ArgumentException>();
        size.Message.Should().Contain("4x8");
        depth.Should().BeOfType<ArgumentException>();
        depth.Message.Should().Contain("10-bit");
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts) length += part.Length;
        byte[] all = new byte[length];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, at);
            at += part.Length;
        }

        return all;
    }
}
