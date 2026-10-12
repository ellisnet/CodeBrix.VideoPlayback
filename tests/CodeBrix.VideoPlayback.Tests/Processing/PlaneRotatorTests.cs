using System;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Pins <see cref="PlaneRotator" /> to FFmpeg's <c>transpose</c>/<c>hflip</c>/<c>vflip</c> byte for byte, on a
/// plane whose every sample is different, and checks the round trips that need no oracle.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class PlaneRotatorTests
{
    private const int Width = 13;

    private const int Height = 7;

    public static TheoryData<RotationAngle, bool, bool> EveryOrientation()
    {
        TheoryData<RotationAngle, bool, bool> data = new TheoryData<RotationAngle, bool, bool>();
        foreach (RotationAngle angle in new[] { RotationAngle.None, RotationAngle.Clockwise90, RotationAngle.Rotate180, RotationAngle.Clockwise270 })
        {
            data.Add(angle, false, false);
            data.Add(angle, true, false);
            data.Add(angle, false, true);
            data.Add(angle, true, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryOrientation))]
    public void Rotate_matches_ffmpeg_exactly_for_every_angle_and_flip(RotationAngle angle, bool flipHorizontal, bool flipVertical)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        byte[] source = Distinct(Width, Height, 1);
        PlaneRotator.GetRotatedSize(Width, Height, angle, out int width, out int height);
        byte[] actual = new byte[width * height];

        //Act
        byte[] expected = FfmpegProcessingOracle.FilterRaw(source, "gray", Width, Height, FfmpegChain(angle, flipHorizontal, flipVertical), "gray");
        PlaneRotator.Rotate(source, Width, Height, Width, actual, width, 1, 1, angle, flipHorizontal, flipVertical);

        //Assert
        actual.Should().Equal(expected);
    }

    [Theory]
    [InlineData(RotationAngle.Clockwise90)]
    [InlineData(RotationAngle.Clockwise270)]
    public void Rotate_matches_ffmpeg_exactly_for_ten_bit_and_rgba(RotationAngle angle)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        byte[] deep = Distinct(Width, Height, 2);
        byte[] rgba = ProcessingTestPatterns.Rgba(Width, Height);
        byte[] deepActual = new byte[deep.Length];
        byte[] rgbaActual = new byte[rgba.Length];

        //Act
        byte[] deepExpected = FfmpegProcessingOracle.FilterRaw(deep, "gray10le", Width, Height, FfmpegChain(angle, true, false), "gray10le");
        byte[] rgbaExpected = FfmpegProcessingOracle.FilterRaw(rgba, "rgba", Width, Height, FfmpegChain(angle, false, true), "rgba");
        PlaneRotator.Rotate(deep, Width, Height, Width * 2, deepActual, Height * 2, 2, 1, angle, true, false);
        PlaneRotator.Rotate(rgba, Width, Height, Width * 4, rgbaActual, Height * 4, 1, 4, angle, false, true);

        //Assert
        deepActual.Should().Equal(deepExpected);
        rgbaActual.Should().Equal(rgbaExpected);
    }

    [Fact]
    public void Rotate_by_a_quarter_four_times_returns_the_original()
    {
        //Arrange
        byte[] original = Distinct(Width, Height, 2);
        byte[] current = (byte[])original.Clone();
        int width = Width;
        int height = Height;

        //Act
        for (int turn = 0; turn < 4; turn++)
        {
            byte[] next = new byte[current.Length];
            PlaneRotator.Rotate(current, width, height, width * 2, next, height * 2, 2, 1, RotationAngle.Clockwise90);
            (width, height) = (height, width);
            current = next;
        }

        //Assert
        current.Should().Equal(original);
    }

    [Theory]
    [InlineData(RotationAngle.Clockwise90, RotationAngle.Clockwise270)]
    [InlineData(RotationAngle.Rotate180, RotationAngle.Rotate180)]
    public void Rotate_then_its_inverse_returns_the_original_rgba(RotationAngle angle, RotationAngle inverse)
    {
        //Arrange
        byte[] original = ProcessingTestPatterns.Rgba(Width, Height);
        PlaneRotator.GetRotatedSize(Width, Height, angle, out int width, out int height);
        byte[] turned = new byte[original.Length];
        byte[] back = new byte[original.Length];

        //Act
        PlaneRotator.Rotate(original, Width, Height, Width * 4, turned, width * 4, 1, 4, angle);
        PlaneRotator.Rotate(turned, width, height, width * 4, back, Width * 4, 1, 4, inverse);

        //Assert
        back.Should().Equal(original);
    }

    [Theory]
    [InlineData(0d, RotationAngle.None)]
    [InlineData(-90d, RotationAngle.Clockwise90)]
    [InlineData(90d, RotationAngle.Clockwise270)]
    [InlineData(180d, RotationAngle.Rotate180)]
    [InlineData(-180d, RotationAngle.Rotate180)]
    [InlineData(270d, RotationAngle.Clockwise90)]
    [InlineData(-89.9d, RotationAngle.Clockwise90)]
    public void FromDisplayMatrixRotation_turns_the_counter_clockwise_angle_into_the_upright_rotation(double degrees, RotationAngle expected) =>
        PlaneRotator.FromDisplayMatrixRotation(degrees).Should().Be(expected);

    [Fact]
    public void Rotate_refuses_a_destination_of_the_unrotated_size_and_a_bad_angle()
    {
        //Arrange
        byte[] source = new byte[Width * Height];
        byte[] tooNarrow = new byte[(Height * Width) - 1];

        //Act
        Exception small = Record.Exception(() => PlaneRotator.Rotate(source, Width, Height, Width, tooNarrow, Height, 1, 1, RotationAngle.Clockwise90));
        Exception angle = Record.Exception(() => PlaneRotator.Rotate(source, Width, Height, Width, new byte[source.Length], Width, 1, 1, (RotationAngle)45));

        //Assert
        small.Should().BeOfType<ArgumentException>();
        small.Message.Should().Contain("destination");
        angle.Should().BeOfType<ArgumentOutOfRangeException>();
        angle.Message.Should().Contain("45");
    }

    internal static string FfmpegChain(RotationAngle angle, bool flipHorizontal, bool flipVertical)
    {
        string chain = string.Empty;
        if (flipHorizontal) chain += "hflip,";
        if (flipVertical) chain += "vflip,";
        chain += angle switch
        {
            RotationAngle.Clockwise90 => "transpose=clock",
            RotationAngle.Rotate180 => "hflip,vflip",
            RotationAngle.Clockwise270 => "transpose=cclock",
            _ => "null",
        };

        return chain;
    }

    /// <summary>A plane in which no two samples are equal (up to 2 bytes each), so any misplaced sample shows.</summary>
    internal static byte[] Distinct(int width, int height, int bytes, int salt = 0)
    {
        byte[] plane = new byte[width * height * bytes];
        int maximum = bytes == 1 ? 255 : 1023;
        for (int i = 0; i < width * height; i++)
        {
            ProcessingTestPatterns.Write(plane, i, bytes, ((i * 7) + 3 + salt) % (maximum + 1));
        }

        return plane;
    }
}
