using System;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Rendering;
using CodeBrix.VideoPlayback.Skia.Internal;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Frames with an alpha plane - a master (Mode3) file's picture - composed by the presenter: premultiplied
/// BGRA with real opacity on the processor path, the same from the alpha shader, and the host's background
/// showing through when the composition is drawn.
/// </summary>
public class SkiaVideoPresenterAlphaTests
{
    private const int Width = 64;
    private const int Height = 16;

    private static readonly VideoColorInfo FullGray = new VideoColorInfo(
        VideoColorPrimaries.Unspecified,
        VideoTransferCharacteristics.Unspecified,
        VideoMatrixCoefficients.Unspecified,
        VideoColorRange.Full,
        VideoChromaSiting.Unknown);

    [Fact]
    public void The_cpu_path_composes_a_known_alpha_gradient_as_premultiplied_bgra()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        byte[] opaque = ConvertOpaque(pool);
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Present(PatternWithGradient(pool));

        //Act
        presenter.Update();
        using SKImage composed = presenter.CaptureComposedFrame();
        byte[] pixels = ReadPixels(composed);

        //Assert
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                int a = Gradient(x);
                pixels[i + 3].Should().Be((byte)a);
                pixels[i + 0].Should().Be(Premultiply(opaque[i + 0], a));
                pixels[i + 1].Should().Be(Premultiply(opaque[i + 1], a));
                pixels[i + 2].Should().Be(Premultiply(opaque[i + 2], a));
            }
        }
    }

    [Fact]
    public void Drawn_over_a_transparent_canvas_the_composition_keeps_its_alpha()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        byte[] opaque = ConvertOpaque(pool);
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Present(PatternWithGradient(pool));
        using SKSurface view = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        view.Canvas.Clear(SKColors.Transparent);

        //Act
        presenter.Draw(view.Canvas, SKRect.Create(0, 0, Width, Height), VideoStretch.Fill);
        using SKImage snapshot = view.Snapshot();
        byte[] pixels = ReadPixels(snapshot);

        //Assert
        pixels[3].Should().Be((byte)0);
        pixels[0].Should().Be((byte)0);
        int last = (Width - 1) * 4;
        pixels[last + 3].Should().Be((byte)255);
        pixels[last + 0].Should().Be(opaque[last + 0]);
        int middle = (Width / 2) * 4;
        pixels[middle + 3].Should().Be((byte)Gradient(Width / 2));
    }

    [Fact]
    public void Drawn_over_a_solid_background_the_background_shows_through_the_transparent_part()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Present(PatternWithGradient(pool));
        using SKSurface view = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        view.Canvas.Clear(new SKColor(0, 0, 255));

        //Act
        presenter.Draw(view.Canvas, SKRect.Create(0, 0, Width, Height), VideoStretch.Fill);
        using SKImage snapshot = view.Snapshot();
        byte[] pixels = ReadPixels(snapshot);

        //Assert - column 0 is fully transparent, so it is the pure blue behind it (BGRA order).
        pixels[0].Should().Be((byte)255);
        pixels[1].Should().Be((byte)0);
        pixels[2].Should().Be((byte)0);
        pixels[3].Should().Be((byte)255);
    }

    [Fact]
    public void A_frame_without_alpha_is_still_composed_opaque()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Present(TestFrames.CreatePattern(pool, Width, Height, VideoPixelLayout.I420, TestFrames.Bt709Limited));

        //Act
        presenter.Update();
        using SKImage composed = presenter.CaptureComposedFrame();
        byte[] pixels = ReadPixels(composed);

        //Assert
        for (int i = 3; i < pixels.Length; i += 4) pixels[i].Should().Be((byte)255);
    }

    [Fact]
    public void The_alpha_shader_agrees_with_the_cpu_path()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = PatternWithGradient(pool);
        byte[] expected = new byte[Width * Height * 4];
        VideoFrameConverter.ToBgra32(frame, expected, Width * 4);

        //Act
        byte[] actual = RenderWithShader(frame);

        //Assert
        int worst = 0;
        for (int i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        worst.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void The_alpha_shaders_compile_and_bind_the_alpha_plane()
    {
        //Arrange
        string plain = YuvShaderSource.Build(true);
        string lookup = YuvShaderSource.Build(LutInterpolation.Trilinear, true);

        //Act
        using SKRuntimeEffect plainEffect = SKRuntimeEffect.CreateShader(plain, out string plainErrors);
        using SKRuntimeEffect lookupEffect = SKRuntimeEffect.CreateShader(lookup, out string lookupErrors);

        //Assert
        (plainEffect != null).Should().BeTrue(plainErrors ?? string.Empty);
        (lookupEffect != null).Should().BeTrue(lookupErrors ?? string.Empty);
        plain.Should().Contain("uniform shader " + YuvShaderSource.AlphaChild);
        lookup.Should().Contain("uniform shader " + YuvShaderSource.AlphaChild);
        lookup.Should().Contain("lookupTable");
        YuvShaderSource.Build(false).Should().Be(YuvShaderSource.Build());
        YuvShaderSource.Build(LutInterpolation.Tetrahedral, false).Should().Be(YuvShaderSource.Build(LutInterpolation.Tetrahedral));
    }

    [Fact]
    public void The_gpu_path_composes_premultiplied_alpha_on_a_real_context()
    {
        //Arrange
        Assert.SkipUnless(
            HeadlessGraphicsContext.IsAvailable,
            "No headless graphics context: " + HeadlessGraphicsContext.UnavailableReason);
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        byte[] expected = new byte[Width * Height * 4];
        using (VideoFrame reference = PatternWithGradient(pool))
        {
            VideoFrameConverter.ToBgra32(reference, expected, Width * 4);
        }

        byte[] actual = null;

        //Act
        HeadlessGraphicsContext.Run(graphics =>
        {
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics) { RenderPath = VideoRenderPath.GpuNoFallback };
            presenter.Present(PatternWithGradient(pool));
            presenter.Update();
            using SKImage composed = presenter.CaptureComposedFrame();
            actual = ReadPixels(composed);
        });

        //Assert
        int worst = 0;
        for (int i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        worst.Should().BeLessThanOrEqualTo(3);
    }

    private static int Gradient(int x) => x * 255 / (Width - 1);

    private static byte Premultiply(byte colour, int alpha) => (byte)(((colour * alpha) + 127) / 255);

    private static byte[] ConvertOpaque(PinnedFrameBufferPool pool)
    {
        using VideoFrame frame = TestFrames.CreatePattern(pool, Width, Height, VideoPixelLayout.I420, TestFrames.Bt709Limited);
        byte[] pixels = new byte[Width * Height * 4];
        VideoFrameConverter.ToBgra32(frame, pixels, Width * 4);
        return pixels;
    }

    private static unsafe VideoFrame PatternWithGradient(PinnedFrameBufferPool pool)
    {
        VideoFrame frame = TestFrames.CreatePattern(pool, Width, Height, VideoPixelLayout.I420, TestFrames.Bt709Limited);

        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(Width, Height, VideoPixelLayout.Gray, 8));
        for (int y = 0; y < Height; y++)
        {
            byte* row = (byte*)buffer.Y.Data + ((long)y * buffer.Y.Stride);
            for (int x = 0; x < Width; x++) row[x] = (byte)Gradient(x);
        }

        VideoFrame alpha = VideoFrame.Create(
            buffer,
            new VideoFrameInfo(Width, Height, Width, Height, VideoPixelLayout.Gray, 8, TimeSpan.Zero, 0, 0, true, FullGray, null),
            pool);

        frame.AttachAlpha(alpha);
        return frame;
    }

    private static byte[] RenderWithShader(VideoFrame frame)
    {
        SKImageInfo info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using YuvSurfaceRenderer renderer = new YuvSurfaceRenderer();
        using SKSurface surface = SKSurface.Create(info);
        renderer.Render(frame, surface, null, null, 0, LutInterpolation.Tetrahedral);
        using SKImage snapshot = surface.Snapshot();
        return ReadPixels(snapshot);
    }

    private static byte[] ReadPixels(SKImage image)
    {
        SKImageInfo info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] pixels = new byte[image.Width * image.Height * 4];
        using SKBitmap bitmap = new SKBitmap(info);
        image.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0).Should().BeTrue();
        bitmap.GetPixelSpan().CopyTo(pixels);
        return pixels;
    }
}
