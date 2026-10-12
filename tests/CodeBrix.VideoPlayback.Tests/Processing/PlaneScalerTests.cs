using System;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Measures <see cref="PlaneScaler" /> against FFmpeg's <c>scale</c> filter on a synthetic plane, and pins the
/// exact cases (identity, single-axis identity, interleaved channels) down to the byte.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class PlaneScalerTests
{
    private const int SourceWidth = 64;

    private const int SourceHeight = 48;

    // Measured against FFmpeg 7.1.5 (default scale flags plus the kernel, i.e. its x86 vector paths), a
    // 64x48 synthetic plane of ramps, a hard block, a two-pixel checker and seeded noise:
    //   Bilinear  8-bit  down 37x29  worst 1  mean 0.047    up 101x77  worst 1  mean 0.067
    //   Lanczos3  8-bit  down        worst 1  mean 0.089    up         worst 2  mean 0.081
    //   Bilinear 10-bit  down        worst 1  mean 0.022    up         worst 1  mean 0.047
    //   Lanczos3 10-bit  down        worst 1  mean 0.053    up         worst 7  mean 0.083
    // With FFmpeg's +accurate_rnd the eight-bit means fall to 0.004-0.023 (the remaining gap is FFmpeg's
    // twelve-bit vertical weights against these fourteen-bit ones). The one larger number - 7 of 1023 - sits
    // in a single output column (x = 3) of the checker strip, where FFmpeg's deep-sample horizontal path
    // rings differently at the left edge; everywhere else that case agrees to 1. The bounds are just above
    // the measurement.
    [Theory]
    [InlineData(ScalingFilter.Bilinear, 8, 37, 29, 1, 0.07)]
    [InlineData(ScalingFilter.Bilinear, 8, 101, 77, 1, 0.09)]
    [InlineData(ScalingFilter.Lanczos3, 8, 37, 29, 1, 0.11)]
    [InlineData(ScalingFilter.Lanczos3, 8, 101, 77, 2, 0.11)]
    [InlineData(ScalingFilter.Bilinear, 10, 37, 29, 1, 0.04)]
    [InlineData(ScalingFilter.Bilinear, 10, 101, 77, 1, 0.07)]
    [InlineData(ScalingFilter.Lanczos3, 10, 37, 29, 1, 0.07)]
    [InlineData(ScalingFilter.Lanczos3, 10, 101, 77, 8, 0.11)]
    public void Scale_agrees_with_ffmpeg_to_a_measured_bound(
        ScalingFilter filter,
        int bitDepth,
        int destinationWidth,
        int destinationHeight,
        int worstBound,
        double meanBound)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        int bytes = bitDepth > 8 ? 2 : 1;
        byte[] source = ProcessingTestPatterns.Plane(SourceWidth, SourceHeight, bitDepth);
        string pixelFormat = bitDepth == 8 ? "gray" : "gray10le";
        string flags = filter == ScalingFilter.Bilinear ? "bilinear" : "lanczos";

        //Act
        byte[] expected = FfmpegProcessingOracle.FilterRaw(
            source, pixelFormat, SourceWidth, SourceHeight,
            $"scale={destinationWidth}:{destinationHeight}:flags={flags}", pixelFormat);

        byte[] actual = new byte[destinationWidth * destinationHeight * bytes];
        PlaneScaler.Scale(
            source, SourceWidth, SourceHeight, SourceWidth * bytes,
            actual, destinationWidth, destinationHeight, destinationWidth * bytes,
            bytes, 1, filter, (1 << bitDepth) - 1);

        //Assert
        (int worst, double mean) = ProcessingTestPatterns.Compare(expected, actual, bytes);
        ProcessingTestPatterns.Record(
            $"scaler {filter} {bitDepth}-bit {SourceWidth}x{SourceHeight}->{destinationWidth}x{destinationHeight}", worst, mean);

        worst.Should().BeLessThanOrEqualTo(worstBound);
        mean.Should().BeLessThanOrEqualTo(meanBound);
    }

    [Theory]
    [InlineData(ScalingFilter.Bilinear, 8)]
    [InlineData(ScalingFilter.Lanczos3, 8)]
    [InlineData(ScalingFilter.Lanczos3, 12)]
    public void Scale_with_an_unchanged_geometry_is_byte_identical(ScalingFilter filter, int bitDepth)
    {
        //Arrange
        int bytes = bitDepth > 8 ? 2 : 1;
        byte[] source = ProcessingTestPatterns.Plane(SourceWidth, SourceHeight, bitDepth);
        byte[] actual = new byte[source.Length];

        //Act
        PlaneScaler.Scale(
            source, SourceWidth, SourceHeight, SourceWidth * bytes,
            actual, SourceWidth, SourceHeight, SourceWidth * bytes,
            bytes, 1, filter, (1 << bitDepth) - 1);

        //Assert
        actual.Should().Equal(source);
    }

    [Fact]
    public void Scale_leaves_an_unchanged_axis_exact()
    {
        //Arrange - only the height changes, so every column is a 1-D scale of the same column
        byte[] source = ProcessingTestPatterns.Plane(SourceWidth, SourceHeight, 8);
        byte[] actual = new byte[SourceWidth * 20];
        byte[] column = new byte[SourceHeight];
        byte[] columnScaled = new byte[20];

        //Act
        PlaneScaler.Scale(source, SourceWidth, SourceHeight, SourceWidth, actual, SourceWidth, 20, SourceWidth, 1, 1, ScalingFilter.Lanczos3, 255);

        //Assert
        for (int x = 0; x < SourceWidth; x++)
        {
            for (int y = 0; y < SourceHeight; y++) column[y] = source[(y * SourceWidth) + x];
            PlaneScaler.Scale(column, 1, SourceHeight, 1, columnScaled, 1, 20, 1, 1, 1, ScalingFilter.Lanczos3, 255);
            for (int y = 0; y < 20; y++) actual[(y * SourceWidth) + x].Should().Be(columnScaled[y]);
        }
    }

    [Fact]
    public void Scale_treats_interleaved_rgba_channels_as_four_independent_planes()
    {
        //Arrange
        byte[] rgba = ProcessingTestPatterns.Rgba(SourceWidth, SourceHeight);
        const int Width = 45;
        const int Height = 31;
        byte[] actual = new byte[Width * Height * 4];

        //Act
        PlaneScaler.Scale(rgba, SourceWidth, SourceHeight, SourceWidth * 4, actual, Width, Height, Width * 4, 1, 4, ScalingFilter.Lanczos3, 255);

        //Assert
        byte[] channel = new byte[SourceWidth * SourceHeight];
        byte[] scaled = new byte[Width * Height];
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < channel.Length; i++) channel[i] = rgba[(i * 4) + c];
            PlaneScaler.Scale(channel, SourceWidth, SourceHeight, SourceWidth, scaled, Width, Height, Width, 1, 1, ScalingFilter.Lanczos3, 255);
            for (int i = 0; i < scaled.Length; i++) actual[(i * 4) + c].Should().Be(scaled[i]);
        }
    }

    [Fact]
    public void Scale_through_an_instance_matches_the_static_call_and_honours_strides()
    {
        //Arrange
        byte[] source = ProcessingTestPatterns.Plane(SourceWidth, SourceHeight, 10);
        const int Width = 50;
        const int Height = 70;
        const int PaddedStride = (Width * 2) + 22;
        byte[] viaStatic = new byte[Width * Height * 2];
        byte[] viaInstance = new byte[PaddedStride * Height];
        viaInstance.AsSpan().Fill(0xEE);
        PlaneScaler scaler = new PlaneScaler(SourceWidth, SourceHeight, Width, Height, ScalingFilter.Bilinear);

        //Act
        PlaneScaler.Scale(source, SourceWidth, SourceHeight, SourceWidth * 2, viaStatic, Width, Height, Width * 2, 2, 1, ScalingFilter.Bilinear, 1023);
        scaler.Scale(source, SourceWidth * 2, viaInstance, PaddedStride, 2, 1, 1023);
        scaler.Scale(source, SourceWidth * 2, viaInstance, PaddedStride, 2, 1, 1023);

        //Assert - the same samples, and the padding at the end of each row untouched
        for (int y = 0; y < Height; y++)
        {
            viaInstance.AsSpan(y * PaddedStride, Width * 2).ToArray().Should().Equal(viaStatic.AsSpan(y * Width * 2, Width * 2).ToArray());
            viaInstance[(y * PaddedStride) + (Width * 2)].Should().Be(0xEE);
        }
    }

    [Fact]
    public void Scale_clamps_a_lanczos_overshoot_to_the_sample_range()
    {
        //Arrange - a hard black/white edge rings under Lanczos; at 10 bits the ring must stop at 0 and 1023
        const int Width = 16;
        byte[] source = new byte[Width * 2 * 2];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < Width; x++) ProcessingTestPatterns.Write(source, (y * Width) + x, 2, x < Width / 2 ? 0 : 1023);
        }

        byte[] actual = new byte[37 * 2 * 2];

        //Act
        PlaneScaler.Scale(source, Width, 2, Width * 2, actual, 37, 2, 74, 2, 1, ScalingFilter.Lanczos3, 1023);

        //Assert
        for (int i = 0; i < 74; i++)
        {
            int sample = ProcessingTestPatterns.Read(actual, i, 2);
            sample.Should().BeInRange(0, 1023);
        }

        ProcessingTestPatterns.Read(actual, 0, 2).Should().Be(0);
        ProcessingTestPatterns.Read(actual, 36, 2).Should().Be(1023);
    }

    [Theory]
    [InlineData(100, 100, 50, 100, ScalingFilter.Lanczos3)]
    [InlineData(100, 100, 100, 99, ScalingFilter.Lanczos3)]
    [InlineData(100, 100, 200, 200, ScalingFilter.Bilinear)]
    [InlineData(100, 100, 100, 100, ScalingFilter.Bilinear)]
    [InlineData(100, 100, 50, 200, ScalingFilter.Lanczos3)]
    public void ChooseDefault_picks_lanczos_for_any_downscale_and_bilinear_otherwise(
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        ScalingFilter expected) =>
        PlaneScaler.ChooseDefault(sourceWidth, sourceHeight, destinationWidth, destinationHeight).Should().Be(expected);

    [Fact]
    public void Scale_refuses_bad_arguments_by_name()
    {
        //Arrange
        byte[] source = new byte[16 * 16];
        byte[] small = new byte[10];
        byte[] destination = new byte[8 * 8];

        //Act
        Exception tooSmall = Record.Exception(() => PlaneScaler.Scale(source, 16, 16, 16, small, 8, 8, 8, 1, 1, ScalingFilter.Bilinear, 255));
        Exception badDepth = Record.Exception(() => PlaneScaler.Scale(source, 16, 16, 16, destination, 8, 8, 8, 3, 1, ScalingFilter.Bilinear, 255));
        Exception badChannels = Record.Exception(() => PlaneScaler.Scale(source, 16, 16, 16, destination, 8, 8, 8, 1, 3, ScalingFilter.Bilinear, 255));
        Exception badStride = Record.Exception(() => PlaneScaler.Scale(source, 16, 16, 15, destination, 8, 8, 8, 1, 1, ScalingFilter.Bilinear, 255));
        Exception badMaximum = Record.Exception(() => PlaneScaler.Scale(source, 16, 16, 16, destination, 8, 8, 8, 1, 1, ScalingFilter.Bilinear, 1023));
        Exception badSize = Record.Exception(() => new PlaneScaler(0, 16, 8, 8, ScalingFilter.Bilinear));
        Exception overlap = Record.Exception(() => PlaneScaler.Scale(source, 8, 8, 8, source.AsSpan(8), 8, 8, 8, 1, 1, ScalingFilter.Bilinear, 255));

        //Assert
        tooSmall.Should().BeOfType<ArgumentException>();
        tooSmall.Message.Should().Contain("destination");
        badDepth.Should().BeOfType<ArgumentOutOfRangeException>();
        badDepth.Message.Should().Contain("bytesPerSample");
        badChannels.Should().BeOfType<ArgumentOutOfRangeException>();
        badChannels.Message.Should().Contain("channels");
        badStride.Should().BeOfType<ArgumentOutOfRangeException>();
        badStride.Message.Should().Contain("sourceStride");
        badMaximum.Should().BeOfType<ArgumentOutOfRangeException>();
        badMaximum.Message.Should().Contain("maxSampleValue");
        badSize.Should().BeOfType<ArgumentOutOfRangeException>();
        badSize.Message.Should().Contain("sourceWidth");
        overlap.Should().BeOfType<ArgumentException>();
        overlap.Message.Should().Contain("overlaps");
    }
}
