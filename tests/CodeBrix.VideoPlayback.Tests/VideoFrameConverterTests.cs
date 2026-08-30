using System;
using System.Diagnostics;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Measures the production colour converter against <see cref="ScalarYuvReference" />, and pins the
/// behaviour a presenter depends on: opaque alpha, untouched row padding, and no allocation per frame.
/// </summary>
public class VideoFrameConverterTests
{
    // The production converter does its sums in 14-bit fixed point; the reference does them in double.
    // The coefficients therefore differ by up to half a fixed-point step, which works out below 0.02 of an
    // output level - far too small to change a result except when the true value sits within that distance
    // of a rounding boundary, where the two can land on either side of it. One level of tolerance covers
    // exactly that and nothing more: a genuine mistake in a matrix, a range, a siting rule or the dither
    // would move pixels by far more than one level.
    private const int Tolerance = 1;

    private const int SampleWidth = 37;
    private const int SampleHeight = 21;

    /// <summary>Every layout, depth, range, matrix and siting the converter claims to handle.</summary>
    /// <returns>The cross product, as theory data.</returns>
    public static TheoryData<VideoPixelLayout, int, VideoColorRange, VideoMatrixCoefficients, VideoChromaSiting>
        ColourCombinations()
    {
        TheoryData<VideoPixelLayout, int, VideoColorRange, VideoMatrixCoefficients, VideoChromaSiting> data = new();

        VideoPixelLayout[] layouts =
        {
            VideoPixelLayout.I420,
            VideoPixelLayout.I422,
            VideoPixelLayout.I444,
            VideoPixelLayout.Gray,
        };

        int[] depths = { 8, 10, 12 };

        VideoColorRange[] ranges = { VideoColorRange.Limited, VideoColorRange.Full };

        VideoMatrixCoefficients[] matrices =
        {
            VideoMatrixCoefficients.Bt470Bg,
            VideoMatrixCoefficients.Bt709,
            VideoMatrixCoefficients.Bt2020NonConstantLuminance,
        };

        VideoChromaSiting[] sitings =
        {
            VideoChromaSiting.Unknown,
            VideoChromaSiting.Vertical,
            VideoChromaSiting.Colocated,
            VideoChromaSiting.Interstitial,
        };

        foreach (VideoPixelLayout layout in layouts)
        {
            foreach (int depth in depths)
            {
                foreach (VideoColorRange range in ranges)
                {
                    foreach (VideoMatrixCoefficients matrix in matrices)
                    {
                        foreach (VideoChromaSiting siting in sitings)
                        {
                            data.Add(layout, depth, range, matrix, siting);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ColourCombinations))]
    public void ToBgra32_matches_the_scalar_reference(
        VideoPixelLayout layout,
        int bitDepth,
        VideoColorRange range,
        VideoMatrixCoefficients matrix,
        VideoChromaSiting siting)
    {
        //Arrange
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Unspecified,
            VideoTransferCharacteristics.Unspecified,
            matrix,
            range,
            siting);

        SyntheticPlanes planes = SyntheticPlanes.Create(SampleWidth, SampleHeight, layout, bitDepth, seed: 12345);

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, color);

        byte[] expected = ScalarYuvReference.ConvertToBgra(
            planes.Luma, planes.ChromaU, planes.ChromaV, SampleWidth, SampleHeight, layout, bitDepth, color);

        int stride = VideoFrameConverter.GetBgraStride(SampleWidth);
        byte[] actual = new byte[VideoFrameConverter.GetBgraBufferSize(SampleWidth, SampleHeight)];

        //Act
        VideoFrameConverter.ToBgra32(frame, actual, stride);

        //Assert
        int worst = 0;
        string where = string.Empty;
        for (int row = 0; row < SampleHeight; row++)
        {
            for (int column = 0; column < SampleWidth; column++)
            {
                int offset = ((row * SampleWidth) + column) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    int difference = Math.Abs(actual[offset + channel] - expected[offset + channel]);
                    if (difference <= worst) continue;
                    worst = difference;
                    where = $"row {row}, column {column}, channel {channel} "
                        + $"(expected {expected[offset + channel]}, actual {actual[offset + channel]})";
                }
            }
        }

        worst.Should().BeLessThanOrEqualTo(
            Tolerance,
            $"the converter should track the scalar reference for {layout} {bitDepth}-bit {range} {matrix} "
            + $"{siting}, but the worst difference was {worst} at {where}");
    }

    [Theory]
    [InlineData(VideoPixelLayout.I420)]
    [InlineData(VideoPixelLayout.I422)]
    [InlineData(VideoPixelLayout.I444)]
    public void ToBgra32_maps_limited_range_endpoints_to_black_and_white(VideoPixelLayout layout)
    {
        //Arrange
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited,
            VideoChromaSiting.Colocated);

        SyntheticPlanes black = SyntheticPlanes.Uniform(32, 16, layout, 8, luma: 16, chroma: 128);
        SyntheticPlanes white = SyntheticPlanes.Uniform(32, 16, layout, 8, luma: 235, chroma: 128);

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        byte[] blackPixels = Convert(black, pool, color);
        byte[] whitePixels = Convert(white, pool, color);

        //Assert
        blackPixels[0].Should().Be(0);
        blackPixels[1].Should().Be(0);
        blackPixels[2].Should().Be(0);
        whitePixels[0].Should().Be(255);
        whitePixels[1].Should().Be(255);
        whitePixels[2].Should().Be(255);
    }

    [Fact]
    public void ToBgra32_maps_full_range_endpoints_to_black_and_white()
    {
        //Arrange
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Full,
            VideoChromaSiting.Colocated);

        SyntheticPlanes black = SyntheticPlanes.Uniform(32, 16, VideoPixelLayout.I420, 8, luma: 0, chroma: 128);
        SyntheticPlanes white = SyntheticPlanes.Uniform(32, 16, VideoPixelLayout.I420, 8, luma: 255, chroma: 128);

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        byte[] blackPixels = Convert(black, pool, color);
        byte[] whitePixels = Convert(white, pool, color);

        //Assert
        blackPixels[2].Should().Be(0);
        whitePixels[2].Should().Be(255);
    }

    [Fact]
    public void ToBgra32_writes_a_red_pixel_where_red_is_expected()
    {
        //Arrange - BT.709 limited range, the chroma pair that names pure red.
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited,
            VideoChromaSiting.Colocated);

        SyntheticPlanes red = SyntheticPlanes.Uniform(32, 16, VideoPixelLayout.I420, 8, luma: 63, chroma: 102);
        red.SetChromaV(240);

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        byte[] pixels = Convert(red, pool, color);

        //Assert - red near full, green and blue near nothing.
        ((int)pixels[2]).Should().BeGreaterThan(240);
        ((int)pixels[1]).Should().BeLessThan(16);
        ((int)pixels[0]).Should().BeLessThan(16);
    }

    [Fact]
    public void ToBgra32_always_writes_an_opaque_alpha_channel()
    {
        //Arrange
        SyntheticPlanes planes = SyntheticPlanes.Create(24, 9, VideoPixelLayout.I420, 8, seed: 77);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        byte[] pixels = Convert(planes, pool, VideoColorInfo.Unspecified);

        //Assert
        for (int index = 3; index < pixels.Length; index += 4) pixels[index].Should().Be(255);
    }

    [Fact]
    public void ToBgra32_leaves_the_padding_of_a_wider_destination_untouched()
    {
        //Arrange
        const int width = 37;
        const int height = 11;
        const int padding = 32;

        SyntheticPlanes planes = SyntheticPlanes.Create(width, height, VideoPixelLayout.I420, 8, seed: 5);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, VideoColorInfo.Unspecified);

        int stride = (width * 4) + padding;
        byte[] destination = new byte[stride * height];
        Array.Fill(destination, (byte)0xAB);

        //Act
        VideoFrameConverter.ToBgra32(frame, destination, stride);

        //Assert
        for (int row = 0; row < height; row++)
        {
            for (int index = width * 4; index < stride; index++)
            {
                destination[(row * stride) + index].Should().Be(0xAB);
            }
        }
    }

    [Fact]
    public void ToBgra32_honours_chroma_siting_on_a_sharp_chroma_edge()
    {
        //Arrange - a vertical chroma edge, which interstitial siting must soften and co-sited siting must not.
        SyntheticPlanes planes = SyntheticPlanes.ChromaEdge(32, 8);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        VideoColorInfo colocated = new VideoColorInfo(
            VideoColorPrimaries.Bt709, VideoTransferCharacteristics.Bt709, VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited, VideoChromaSiting.Colocated);

        VideoColorInfo interstitial = new VideoColorInfo(
            VideoColorPrimaries.Bt709, VideoTransferCharacteristics.Bt709, VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited, VideoChromaSiting.Interstitial);

        //Act
        byte[] hard = Convert(planes, pool, colocated);
        byte[] soft = Convert(planes, pool, interstitial);

        //Assert
        hard.Should().NotEqual(soft);
    }

    [Fact]
    public void ToBgra32_converts_a_high_dynamic_range_frame_without_tone_mapping()
    {
        //Arrange - the same picture, once declared as PQ and once as BT.709. The converter ignores the
        //transfer curve, so the two must come out identical.
        SyntheticPlanes planes = SyntheticPlanes.Create(20, 8, VideoPixelLayout.I420, 10, seed: 909);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        VideoColorInfo standard = new VideoColorInfo(
            VideoColorPrimaries.Bt2020, VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt2020NonConstantLuminance, VideoColorRange.Limited, VideoChromaSiting.Vertical);

        VideoColorInfo highDynamicRange = new VideoColorInfo(
            VideoColorPrimaries.Bt2020, VideoTransferCharacteristics.SmpteSt2084,
            VideoMatrixCoefficients.Bt2020NonConstantLuminance, VideoColorRange.Limited, VideoChromaSiting.Vertical);

        //Act
        byte[] sdr = Convert(planes, pool, standard);
        byte[] hdr = Convert(planes, pool, highDynamicRange);

        //Assert
        hdr.Should().Equal(sdr);
    }

    [Fact]
    public void ToBgra32_throws_when_the_frame_is_null()
        => ((Action)(() => VideoFrameConverter.ToBgra32(null, new byte[16], 4)))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void ToBgra32_throws_when_the_destination_is_too_small()
    {
        //Arrange
        SyntheticPlanes planes = SyntheticPlanes.Create(16, 8, VideoPixelLayout.I420, 8, seed: 1);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, VideoColorInfo.Unspecified);
        byte[] tooSmall = new byte[16 * 8 * 4 - 1];

        //Act
        Action act = () => VideoFrameConverter.ToBgra32(frame, tooSmall, 16 * 4);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToBgra32_throws_when_the_stride_is_narrower_than_a_row()
    {
        //Arrange
        SyntheticPlanes planes = SyntheticPlanes.Create(16, 8, VideoPixelLayout.I420, 8, seed: 1);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, VideoColorInfo.Unspecified);
        byte[] destination = new byte[16 * 8 * 4];

        //Act
        Action act = () => VideoFrameConverter.ToBgra32(frame, destination, (16 * 4) - 1);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToBgra32_throws_for_an_unsupported_bit_depth()
    {
        //Arrange
        VideoFramePlane luma = new VideoFramePlane(new IntPtr(64), 64, 8, 8, 1);
        byte[] destination = new byte[8 * 8 * 4];

        //Act
        Action act = () => VideoFrameConverter.ToBgra32(
            luma, VideoFramePlane.Empty, VideoFramePlane.Empty, 8, 8, VideoPixelLayout.Gray, 9,
            VideoColorInfo.Unspecified, destination, 32);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToBgra32_throws_for_an_unknown_layout()
    {
        //Arrange
        VideoFramePlane luma = new VideoFramePlane(new IntPtr(64), 64, 8, 8, 1);
        byte[] destination = new byte[8 * 8 * 4];

        //Act
        Action act = () => VideoFrameConverter.ToBgra32(
            luma, VideoFramePlane.Empty, VideoFramePlane.Empty, 8, 8, VideoPixelLayout.Unknown, 8,
            VideoColorInfo.Unspecified, destination, 32);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetBgraStride_is_four_bytes_a_pixel()
        => VideoFrameConverter.GetBgraStride(1920).Should().Be(7680);

    [Fact]
    public void GetBgraBufferSize_is_the_stride_times_the_height()
        => VideoFrameConverter.GetBgraBufferSize(1920, 1080).Should().Be(7680 * 1080);

    [Fact]
    public void GetBgraStride_throws_for_a_width_of_zero()
        => ((Action)(() => VideoFrameConverter.GetBgraStride(0))).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void IsHardwareAccelerated_reports_the_vector_support_of_this_machine()
        => VideoFrameConverter.IsHardwareAccelerated.Should()
            .Be(System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated
                || System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated);

    [Fact]
    public void ToBgra32_allocates_nothing_once_it_is_warm()
    {
        //Arrange
        SyntheticPlanes planes = SyntheticPlanes.Create(320, 180, VideoPixelLayout.I420, 8, seed: 42);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, VideoColorInfo.Unspecified);

        int stride = VideoFrameConverter.GetBgraStride(320);
        byte[] destination = new byte[VideoFrameConverter.GetBgraBufferSize(320, 180)];

        for (int warmUp = 0; warmUp < 4; warmUp++) VideoFrameConverter.ToBgra32(frame, destination, stride);

        //Act
        long allocated = SteadyStateAllocation.MeasureSmallest(
            () =>
            {
                for (int iteration = 0; iteration < 50; iteration++)
                {
                    VideoFrameConverter.ToBgra32(frame, destination, stride);
                }
            });

        //Assert
        allocated.Should().Be(0L);
    }

    [Fact]
    public void ToBgra32_converts_a_1080p_frame_within_the_recorded_budget()
    {
        //Arrange
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("CODEBRIX_VIDEOPLAYBACK_RUN_BENCHMARKS") == "1",
            "Set CODEBRIX_VIDEOPLAYBACK_RUN_BENCHMARKS=1 to run the colour-conversion benchmark.");

        const int width = 1920;
        const int height = 1080;
        const int warmUpIterations = 20;
        const int timedIterations = 200;

        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709, VideoTransferCharacteristics.Bt709, VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited, VideoChromaSiting.Vertical);

        SyntheticPlanes planes = SyntheticPlanes.Create(width, height, VideoPixelLayout.I420, 8, seed: 2026);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = planes.ToFrame(pool, color);

        using BgraFrameBufferPool surfaces = new BgraFrameBufferPool();
        BgraFrameBuffer surface = surfaces.Rent(width, height);

        for (int warmUp = 0; warmUp < warmUpIterations; warmUp++)
        {
            VideoFrameConverter.ToBgra32(frame, surface.AsSpan(), surface.Stride);
        }

        //Act
        Stopwatch clock = Stopwatch.StartNew();
        for (int iteration = 0; iteration < timedIterations; iteration++)
        {
            VideoFrameConverter.ToBgra32(frame, surface.AsSpan(), surface.Stride);
        }

        clock.Stop();
        surfaces.Return(surface);

        double millisecondsPerFrame = clock.Elapsed.TotalMilliseconds / timedIterations;
        double framesPerSecond = 1000.0 / millisecondsPerFrame;

        //Assert
        TestContext.Current.TestOutputHelper.WriteLine(
            $"1080p 8-bit 4:2:0 BT.709 limited -> BGRA32: {millisecondsPerFrame:0.000} ms/frame "
            + $"({framesPerSecond:0.0} fps) over {timedIterations} iterations. "
            + $"Vector256 accelerated: {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}.");

        millisecondsPerFrame.Should().BeLessThan(33.0);
    }

    private static byte[] Convert(SyntheticPlanes planes, PinnedFrameBufferPool pool, VideoColorInfo color)
    {
        using VideoFrame frame = planes.ToFrame(pool, color);
        int stride = VideoFrameConverter.GetBgraStride(planes.Width);
        byte[] pixels = new byte[VideoFrameConverter.GetBgraBufferSize(planes.Width, planes.Height)];
        VideoFrameConverter.ToBgra32(frame, pixels, stride);
        return pixels;
    }

    /// <summary>Planar sample data held as plain arrays, and the means to put it into a pooled frame.</summary>
    private sealed class SyntheticPlanes
    {
        private SyntheticPlanes(
            int width, int height, VideoPixelLayout layout, int bitDepth,
            int[] luma, int[] chromaU, int[] chromaV, int chromaWidth, int chromaHeight)
        {
            Width = width;
            Height = height;
            Layout = layout;
            BitDepth = bitDepth;
            Luma = luma;
            ChromaU = chromaU;
            ChromaV = chromaV;
            ChromaWidth = chromaWidth;
            ChromaHeight = chromaHeight;
        }

        public int Width { get; }

        public int Height { get; }

        public VideoPixelLayout Layout { get; }

        public int BitDepth { get; }

        public int[] Luma { get; }

        public int[] ChromaU { get; }

        public int[] ChromaV { get; }

        public int ChromaWidth { get; }

        public int ChromaHeight { get; }

        public static SyntheticPlanes Create(int width, int height, VideoPixelLayout layout, int bitDepth, int seed)
        {
            Dimensions(width, height, layout, out int chromaWidth, out int chromaHeight);
            int maximum = (1 << bitDepth) - 1;

            uint state = (uint)seed | 1u;
            int[] luma = new int[width * height];
            for (int index = 0; index < luma.Length; index++) luma[index] = (int)(Next(ref state) % (uint)(maximum + 1));

            int[] chromaU = null;
            int[] chromaV = null;
            if (chromaWidth > 0)
            {
                chromaU = new int[chromaWidth * chromaHeight];
                chromaV = new int[chromaWidth * chromaHeight];
                for (int index = 0; index < chromaU.Length; index++)
                {
                    chromaU[index] = (int)(Next(ref state) % (uint)(maximum + 1));
                    chromaV[index] = (int)(Next(ref state) % (uint)(maximum + 1));
                }
            }

            return new SyntheticPlanes(width, height, layout, bitDepth, luma, chromaU, chromaV, chromaWidth, chromaHeight);
        }

        public static SyntheticPlanes Uniform(
            int width, int height, VideoPixelLayout layout, int bitDepth, int luma, int chroma)
        {
            Dimensions(width, height, layout, out int chromaWidth, out int chromaHeight);

            int[] lumaSamples = new int[width * height];
            Array.Fill(lumaSamples, luma);

            int[] chromaU = null;
            int[] chromaV = null;
            if (chromaWidth > 0)
            {
                chromaU = new int[chromaWidth * chromaHeight];
                chromaV = new int[chromaWidth * chromaHeight];
                Array.Fill(chromaU, chroma);
                Array.Fill(chromaV, chroma);
            }

            return new SyntheticPlanes(width, height, layout, bitDepth, lumaSamples, chromaU, chromaV, chromaWidth, chromaHeight);
        }

        public static SyntheticPlanes ChromaEdge(int width, int height)
        {
            SyntheticPlanes planes = Uniform(width, height, VideoPixelLayout.I420, 8, luma: 128, chroma: 128);
            for (int row = 0; row < planes.ChromaHeight; row++)
            {
                for (int column = 0; column < planes.ChromaWidth; column++)
                {
                    planes.ChromaU[(row * planes.ChromaWidth) + column] = column < planes.ChromaWidth / 2 ? 32 : 224;
                }
            }

            return planes;
        }

        public void SetChromaV(int value)
        {
            if (ChromaV != null) Array.Fill(ChromaV, value);
        }

        public VideoFrame ToFrame(PinnedFrameBufferPool pool, VideoColorInfo color)
        {
            VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(Width, Height, Layout, BitDepth);
            VideoFrameBuffer buffer = pool.Rent(descriptor);
            ((PinnedVideoFrameBuffer)buffer).Clear();

            WritePlane(buffer.Y, Luma, Width, Height);
            if (ChromaU != null)
            {
                WritePlane(buffer.U, ChromaU, ChromaWidth, ChromaHeight);
                WritePlane(buffer.V, ChromaV, ChromaWidth, ChromaHeight);
            }

            VideoFrameInfo info = new VideoFrameInfo(
                Width, Height, Width, Height, Layout, BitDepth, TimeSpan.Zero, 0, 0, true, color, null);

            return VideoFrame.Create(buffer, info, pool);
        }

        private static void Dimensions(
            int width, int height, VideoPixelLayout layout, out int chromaWidth, out int chromaHeight)
        {
            if (layout == VideoPixelLayout.Gray)
            {
                chromaWidth = 0;
                chromaHeight = 0;
                return;
            }

            int shiftX = layout == VideoPixelLayout.I420 || layout == VideoPixelLayout.I422 ? 1 : 0;
            int shiftY = layout == VideoPixelLayout.I420 ? 1 : 0;
            chromaWidth = (width + (1 << shiftX) - 1) >> shiftX;
            chromaHeight = (height + (1 << shiftY) - 1) >> shiftY;
        }

        private unsafe void WritePlane(VideoFramePlane plane, int[] samples, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                byte* target = (byte*)plane.Data + ((long)row * plane.Stride);
                if (BitDepth == 8)
                {
                    for (int column = 0; column < width; column++)
                    {
                        target[column] = (byte)samples[(row * width) + column];
                    }
                }
                else
                {
                    ushort* wide = (ushort*)target;
                    for (int column = 0; column < width; column++)
                    {
                        wide[column] = (ushort)samples[(row * width) + column];
                    }
                }
            }
        }

        private static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
