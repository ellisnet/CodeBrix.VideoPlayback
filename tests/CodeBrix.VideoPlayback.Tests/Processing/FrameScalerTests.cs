using System;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Measures <see cref="FrameScaler" /> on whole frames against FFmpeg's <c>scale</c> filter, and checks that it
/// is exactly the per-plane scaler applied with the right chroma geometry.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class FrameScalerTests
{
    // Measured against FFmpeg 7.1.5 on a 64x48 frame of three different synthetic planes (default flags plus
    // the kernel). Chroma is scaled from 32x24 to the destination's own rounded-up half size, centre-sited,
    // which is also what FFmpeg does when the siting is unspecified - hence chroma agrees as well as luma:
    //   Lanczos3  8-bit -> 38x30    luma worst 1 mean 0.090   chroma worst 1 mean 0.072
    //   Bilinear 10-bit -> 101x77   luma worst 1 mean 0.047   chroma worst 1 mean 0.031
    [Theory]
    [InlineData(ScalingFilter.Lanczos3, 8, 38, 30, 1, 0.11, 1, 0.09)]
    [InlineData(ScalingFilter.Bilinear, 10, 101, 77, 1, 0.06, 1, 0.045)]
    public void Scale_an_i420_frame_agrees_with_ffmpeg_to_a_measured_bound(
        ScalingFilter filter,
        int bitDepth,
        int width,
        int height,
        int lumaWorst,
        double lumaMean,
        int chromaWorst,
        double chromaMean)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        const int SourceWidth = 64;
        const int SourceHeight = 48;
        int bytes = bitDepth > 8 ? 2 : 1;
        byte[] y = ProcessingTestPatterns.Plane(SourceWidth, SourceHeight, bitDepth, 1);
        byte[] u = ProcessingTestPatterns.Plane(SourceWidth / 2, SourceHeight / 2, bitDepth, 2);
        byte[] v = ProcessingTestPatterns.Plane(SourceWidth / 2, SourceHeight / 2, bitDepth, 3);
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        byte[] outY = new byte[width * height * bytes];
        byte[] outU = new byte[chromaWidth * chromaHeight * bytes];
        byte[] outV = new byte[chromaWidth * chromaHeight * bytes];
        string pixelFormat = bitDepth == 8 ? "yuv420p" : "yuv420p10le";
        string flags = filter == ScalingFilter.Bilinear ? "bilinear" : "lanczos";

        //Act
        byte[] expected = FfmpegProcessingOracle.FilterRaw(
            Concat(y, u, v), pixelFormat, SourceWidth, SourceHeight, $"scale={width}:{height}:flags={flags}", pixelFormat);

        FrameScaler scaler = new FrameScaler(ProcessingFrameLayout.I420, SourceWidth, SourceHeight, width, height, filter);
        scaler.Scale(
            ReadOnlyPlanarFrame.CreateI420(SourceWidth, SourceHeight, bitDepth, y, SourceWidth * bytes, u, SourceWidth / 2 * bytes, v, SourceWidth / 2 * bytes),
            PlanarFrame.CreateI420(width, height, bitDepth, outY, width * bytes, outU, chromaWidth * bytes, outV, chromaWidth * bytes));

        //Assert
        (int worstY, double meanY) = ProcessingTestPatterns.Compare(expected.AsSpan(0, outY.Length).ToArray(), outY, bytes);
        (int worstC, double meanC) = ProcessingTestPatterns.Compare(expected.AsSpan(outY.Length).ToArray(), Concat(outU, outV), bytes);
        ProcessingTestPatterns.Record($"frame scaler {filter} {pixelFormat} -> {width}x{height} luma", worstY, meanY);
        ProcessingTestPatterns.Record($"frame scaler {filter} {pixelFormat} -> {width}x{height} chroma", worstC, meanC);

        worstY.Should().BeLessThanOrEqualTo(lumaWorst);
        meanY.Should().BeLessThanOrEqualTo(lumaMean);
        worstC.Should().BeLessThanOrEqualTo(chromaWorst);
        meanC.Should().BeLessThanOrEqualTo(chromaMean);
    }

    [Fact]
    public void Scale_an_odd_sized_i420_frame_with_alpha_is_the_plane_scaler_on_each_plane()
    {
        //Arrange - 33x21 to 20x13: chroma 17x11 to 10x7
        byte[] y = ProcessingTestPatterns.Plane(33, 21, 12, 1);
        byte[] u = ProcessingTestPatterns.Plane(17, 11, 12, 2);
        byte[] v = ProcessingTestPatterns.Plane(17, 11, 12, 3);
        byte[] a = ProcessingTestPatterns.Plane(33, 21, 12, 4);
        byte[] outY = new byte[20 * 13 * 2];
        byte[] outU = new byte[10 * 7 * 2];
        byte[] outV = new byte[10 * 7 * 2];
        byte[] outA = new byte[20 * 13 * 2];

        //Act
        FrameScaler.ScaleWithDefaultFilter(
            ReadOnlyPlanarFrame.CreateI420(33, 21, 12, y, 66, u, 34, v, 34, a, 66),
            PlanarFrame.CreateI420(20, 13, 12, outY, 40, outU, 20, outV, 20, outA, 40));

        //Assert
        outY.Should().Equal(Plane(y, 33, 21, 20, 13));
        outU.Should().Equal(Plane(u, 17, 11, 10, 7));
        outV.Should().Equal(Plane(v, 17, 11, 10, 7));
        outA.Should().Equal(Plane(a, 33, 21, 20, 13));
    }

    [Fact]
    public void Scale_an_rgba_frame_scales_the_four_interleaved_channels()
    {
        //Arrange
        byte[] rgba = ProcessingTestPatterns.Rgba(40, 30);
        byte[] viaFrame = new byte[60 * 45 * 4];
        byte[] viaPlane = new byte[60 * 45 * 4];

        //Act
        FrameScaler.Scale(
            ReadOnlyPlanarFrame.CreateRgba(40, 30, rgba, 160),
            PlanarFrame.CreateRgba(60, 45, viaFrame, 240),
            ScalingFilter.Bilinear);
        PlaneScaler.Scale(rgba, 40, 30, 160, viaPlane, 60, 45, 240, 1, 4, ScalingFilter.Bilinear, 255);

        //Assert
        viaFrame.Should().Equal(viaPlane);
    }

    [Fact]
    public void Scale_refuses_frames_that_do_not_match_the_instance_or_each_other()
    {
        //Arrange
        FrameScaler scaler = new FrameScaler(ProcessingFrameLayout.Gray, 8, 8, 4, 4, ScalingFilter.Bilinear);
        byte[] source = new byte[64];

        //Act
        Exception wrongSize = Record.Exception(() => scaler.Scale(
            ReadOnlyPlanarFrame.CreateGray(8, 8, 8, source, 8),
            PlanarFrame.CreateGray(5, 4, 8, new byte[20], 5)));

        Exception wrongLayout = Record.Exception(() => scaler.Scale(
            ReadOnlyPlanarFrame.CreateRgba(8, 8, new byte[256], 32),
            PlanarFrame.CreateGray(4, 4, 8, new byte[16], 4)));

        Exception alphaFromNothing = Record.Exception(() => FrameScaler.Scale(
            ReadOnlyPlanarFrame.CreateGray(8, 8, 8, source, 8),
            PlanarFrame.CreateGray(4, 4, 8, new byte[16], 4, new byte[16], 4),
            ScalingFilter.Bilinear));

        Exception badPlane = Record.Exception(() => ReadOnlyPlanarFrame.CreateI420(8, 8, 8, source, 8, new byte[15], 4, new byte[16], 4));

        //Assert
        wrongSize.Should().BeOfType<ArgumentException>();
        wrongSize.Message.Should().Contain("4x4");
        wrongLayout.Should().BeOfType<ArgumentException>();
        wrongLayout.Message.Should().Contain("Rgba");
        alphaFromNothing.Should().BeOfType<ArgumentException>();
        alphaFromNothing.Message.Should().Contain("alpha");
        badPlane.Should().BeOfType<ArgumentException>();
        badPlane.Message.Should().Contain("u");
    }

    private static byte[] Plane(byte[] source, int width, int height, int outWidth, int outHeight)
    {
        byte[] result = new byte[outWidth * outHeight * 2];
        PlaneScaler.Scale(
            source, width, height, width * 2,
            result, outWidth, outHeight, outWidth * 2,
            2, 1, PlaneScaler.ChooseDefault(width, height, outWidth, outHeight), 4095);
        return result;
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
