using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Skia.Internal;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Measures the colour shader against the core's vector converter.
/// </summary>
/// <remarks>
/// <para>
/// This is the real correctness proof for the graphics path, and it needs no graphics device: SkiaSharp's
/// runtime effects compile and RUN on the raster backend, so the very shader that ships can be pointed at a
/// software surface and its output compared, pixel for pixel, with the converter the core has already
/// measured against a double-precision oracle.
/// </para>
/// <para>
/// The two will never agree exactly. The converter works in 16-bit fixed point with integer chroma blends;
/// the shader works in floating point with the sampler's own linear filter. What the tests pin is that the
/// disagreement stays within a small, stated bound - which is what says the two are the same conversion
/// rather than two different ones that happen to look alike.
/// </para>
/// </remarks>
public class YuvShaderSourceTests
{
    /// <summary>
    /// The largest per-channel difference between the shader and the converter these tests will accept.
    /// </summary>
    /// <remarks>
    /// Measured on this repository's reference frames, the worst case is 2 and the mean is well under a
    /// tenth of one level. The bound is deliberately just above what was measured, so a real divergence in
    /// either implementation fails rather than passes quietly.
    /// </remarks>
    private const int Tolerance = 3;

    private readonly ITestOutputHelper output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Where the measured differences are written.</param>
    public YuvShaderSourceTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void The_plain_shader_compiles()
    {
        //Arrange
        string source = YuvShaderSource.Build();

        //Act
        SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(source, out string errors);

        //Assert
        (effect != null).Should().BeTrue(errors ?? string.Empty);
        source.Should().Contain("uniform shader yPlane");
        source.Should().NotContain("lookupTable");
        effect?.Dispose();
    }

    [Fact]
    public void The_lookup_shader_compiles_and_declares_the_atlas()
    {
        //Arrange
        string source = YuvShaderSource.Build(LutInterpolation.Tetrahedral);

        //Act
        SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(source, out string errors);

        //Assert
        (effect != null).Should().BeTrue(errors ?? string.Empty);
        source.Should().Contain("uniform shader lookupTable");
        source.Should().Contain("uniform float lookupSize");
        effect?.Dispose();
    }

    [Theory]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Vertical, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Colocated, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Interstitial, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Vertical, VideoColorRange.Full, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Vertical, VideoColorRange.Limited, VideoMatrixCoefficients.Smpte170M)]
    [InlineData(VideoPixelLayout.I420, VideoChromaSiting.Vertical, VideoColorRange.Limited, VideoMatrixCoefficients.Bt2020NonConstantLuminance)]
    [InlineData(VideoPixelLayout.I422, VideoChromaSiting.Vertical, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I422, VideoChromaSiting.Interstitial, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.I444, VideoChromaSiting.Colocated, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    [InlineData(VideoPixelLayout.Gray, VideoChromaSiting.Vertical, VideoColorRange.Limited, VideoMatrixCoefficients.Bt709)]
    public void The_shader_agrees_with_the_core_converter(
        VideoPixelLayout layout,
        VideoChromaSiting siting,
        VideoColorRange range,
        VideoMatrixCoefficients matrix)
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            matrix,
            range,
            siting);

        using VideoFrame frame = TestFrames.CreatePattern(pool, 64, 32, layout, color, 5);

        byte[] reference = new byte[VideoFrameConverter.GetBgraBufferSize(frame.Width, frame.Height)];
        VideoFrameConverter.ToBgra32(frame, reference, VideoFrameConverter.GetBgraStride(frame.Width));

        //Act
        byte[] shaded = RenderWithShader(frame, null, 0);

        //Assert
        Compare(reference, shaded, frame.Width, frame.Height, $"{layout}/{siting}/{range}/{matrix}");
    }

    [Fact]
    public void The_shader_agrees_with_the_core_converter_on_real_decoded_frames()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        List<VideoFrame> frames = CbvFrameSource.Decode(TestFrames.Asset("raw-synthetic.cbv"), pool, 6);

        //Act & Assert
        try
        {
            foreach (VideoFrame frame in frames)
            {
                byte[] reference = new byte[VideoFrameConverter.GetBgraBufferSize(frame.Width, frame.Height)];
                VideoFrameConverter.ToBgra32(frame, reference, VideoFrameConverter.GetBgraStride(frame.Width));

                byte[] shaded = RenderWithShader(frame, null, 0);
                Compare(reference, shaded, frame.Width, frame.Height, $"frame {frame.FrameNumber}");
            }
        }
        finally
        {
            foreach (VideoFrame frame in frames) frame.Dispose();
        }
    }

    [Fact]
    public void An_identity_atlas_in_the_shader_leaves_the_picture_where_it_was()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame =
            TestFrames.CreatePattern(pool, 48, 24, VideoPixelLayout.I420, TestFrames.Bt709Limited, 2);

        byte[] plain = RenderWithShader(frame, null, 0);

        using SKBitmap atlas = BuildAtlas(new Effects.EffectComposer(33));

        //Act
        using SKImage atlasImage = SKImage.FromBitmap(atlas);
        byte[] graded = RenderWithShader(frame, atlasImage, 33);

        //Assert - the atlas holds 8-bit nodes, so an identity table is identity to within its own quantum
        Compare(plain, graded, frame.Width, frame.Height, "identity atlas", 2);
    }

    private void Compare(byte[] expected, byte[] actual, int width, int height, string what, int tolerance = Tolerance)
    {
        int worst = 0;
        long total = 0;
        long counted = 0;
        int worstIndex = -1;

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int index = (pixel * 4) + channel;
                int difference = Math.Abs(expected[index] - actual[index]);
                total += difference;
                counted++;
                if (difference <= worst) continue;
                worst = difference;
                worstIndex = index;
            }

            actual[(pixel * 4) + 3].Should().Be((byte)255);
        }

        output.WriteLine(
            $"{what}: worst channel difference {worst} (at byte {worstIndex}), mean "
            + $"{(double)total / counted:0.0000} over {counted} channels.");

        if (worst > tolerance)
        {
            Assert.Fail(
                $"The shader and the core converter disagree by {worst} levels on {what}, which is more than "
                + $"the {tolerance} this test allows. Byte {worstIndex}: converter "
                + $"{expected[worstIndex]}, shader {actual[worstIndex]}.");
        }
    }

    private static byte[] RenderWithShader(
        VideoFrame frame,
        SKImage lookupAtlas,
        int lookupSize,
        LutInterpolation interpolation = LutInterpolation.Tetrahedral)
    {
        SKImageInfo info =
            new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using YuvSurfaceRenderer renderer = new YuvSurfaceRenderer();
        using SKSurface surface = SKSurface.Create(info);

        renderer.Render(frame, surface, null, lookupAtlas, lookupSize, interpolation);

        byte[] pixels = new byte[frame.Width * frame.Height * 4];
        using (SKImage snapshot = surface.Snapshot())
        using (SKPixmap map = snapshot.PeekPixels())
        {
            for (int row = 0; row < frame.Height; row++)
            {
                map.GetPixelSpan()
                    .Slice(row * map.RowBytes, frame.Width * 4)
                    .CopyTo(pixels.AsSpan(row * frame.Width * 4));
            }
        }

        return pixels;
    }

    private static unsafe SKBitmap BuildAtlas(Effects.EffectComposer composer)
    {
        int width = LutAtlas.GetWidth(composer.Size);
        int height = LutAtlas.GetHeight(composer.Size);

        SKBitmap bitmap =
            new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

        LutAtlas.Write(
            composer,
            new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount),
            bitmap.RowBytes);

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }
}
