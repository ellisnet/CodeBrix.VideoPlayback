using System;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Skia.Effects;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Exercises the graphics render path on a real graphics context.
/// </summary>
/// <remarks>
/// <para>
/// The context is a surfaceless EGL one - a display with no window behind it - so these run in an ordinary
/// test runner with no screen. Where that cannot be arranged the tests skip themselves and say why; the
/// shader itself is still measured against the core converter on the raster backend by
/// <see cref="YuvShaderSourceTests" />, so nothing goes unchecked, but the texture upload and the
/// device-backed composition surface would then only be exercised by the fallback tests.
/// </para>
/// <para>
/// Every test runs its whole body on the graphics thread, because a graphics context belongs to one thread
/// and a test runner does not promise which one a test gets.
/// </para>
/// </remarks>
public class SkiaVideoPresenterGpuTests
{
    private readonly ITestOutputHelper output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Where the measurements are written.</param>
    public SkiaVideoPresenterGpuTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void A_headless_graphics_context_is_available_on_this_machine()
    {
        //Arrange & Act
        bool available = HeadlessGraphicsContext.IsAvailable;

        //Assert
        Assert.SkipUnless(
            available,
            "No headless graphics context: " + HeadlessGraphicsContext.UnavailableReason);

        available.Should().BeTrue();
    }

    [Fact]
    public void GpuAuto_takes_the_graphics_path_when_a_context_is_there()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics);
            presenter.ResolveRenderPath().Should().Be(VideoRenderBackend.Gpu);
            presenter.ActiveRenderPath.Should().Be(VideoRenderBackend.Gpu);
        });
    }

    [Fact]
    public void GpuNoFallback_is_satisfied_by_a_real_context()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics)
            {
                RenderPath = VideoRenderPath.GpuNoFallback,
            };

            presenter.ResolveRenderPath().Should().Be(VideoRenderBackend.Gpu);
        });
    }

    [Fact]
    public void An_effect_chain_is_active_on_the_graphics_path_without_being_allowed_on_the_processor()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics);
            presenter.Effects.Add(new LutEffect(Lut3D.CreateIdentity(9)));
            presenter.ResolveRenderPath();

            presenter.AllowEffectsOnCpu.Should().BeFalse();
            presenter.EffectsActive.Should().BeTrue();
        });
    }

    [Fact]
    public void The_graphics_path_composes_what_the_core_converter_would_have()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics);
            using VideoFrame frame =
                TestFrames.CreatePattern(pool, 64, 32, VideoPixelLayout.I420, TestFrames.Bt709Limited, 4);

            byte[] reference = new byte[VideoFrameConverter.GetBgraBufferSize(frame.Width, frame.Height)];
            VideoFrameConverter.ToBgra32(frame, reference, VideoFrameConverter.GetBgraStride(frame.Width));

            presenter.Present(frame);
            presenter.Update();

            presenter.ActiveRenderPath.Should().Be(VideoRenderBackend.Gpu);

            using SKImage composed = presenter.CaptureComposedFrame();
            byte[] actual = ReadPixels(composed, frame.Width, frame.Height);

            int worst = WorstDifference(reference, actual, frame.Width * frame.Height);
            output.WriteLine($"graphics path versus the core converter: worst channel difference {worst}.");
            worst.Should().BeLessThanOrEqualTo(3);
        });
    }

    [Fact]
    public void The_graphics_path_leaves_a_signalled_fence_on_the_buffer_it_read()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics);

            // The test keeps its own reference, so the buffer cannot go back to the pool and its tag can
            // still be read after the composition.
            using VideoFrame frame =
                TestFrames.CreateFlat(pool, 32, 16, 120, 128, 128, TestFrames.Bt709Limited);

            presenter.Present(frame);
            presenter.Update();

            (frame.Buffer.Tag is IVideoFrameFence).Should().BeTrue();
            ((IVideoFrameFence)frame.Buffer.Tag).IsSignaled.Should().BeTrue();
            frame.Buffer.IsFenceSignaled().Should().BeTrue();
        });
    }

    [Fact]
    public void An_inverting_lookup_table_inverts_the_picture_on_the_graphics_path()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
            using VideoFrame frame =
                TestFrames.CreatePattern(pool, 32, 16, VideoPixelLayout.I420, TestFrames.Bt709Limited, 6);

            byte[] plain;
            using (SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics))
            {
                presenter.Present(frame);
                presenter.Update();
                using SKImage composed = presenter.CaptureComposedFrame();
                plain = ReadPixels(composed, frame.Width, frame.Height);
            }

            byte[] inverted;
            using (SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics))
            {
                presenter.Effects.Add(new LutEffect(EffectComposerTests.Invert(33), "invert"));
                presenter.Present(frame);
                presenter.Update();
                presenter.EffectsActive.Should().BeTrue();
                using SKImage composed = presenter.CaptureComposedFrame();
                inverted = ReadPixels(composed, frame.Width, frame.Height);
            }

            int worst = 0;
            for (int pixel = 0; pixel < frame.Width * frame.Height; pixel++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int index = (pixel * 4) + channel;
                    int difference = Math.Abs((255 - plain[index]) - inverted[index]);
                    if (difference > worst) worst = difference;
                }
            }

            output.WriteLine($"inverting lookup table on the graphics path: worst difference {worst}.");
            worst.Should().BeLessThanOrEqualTo(3);
        });
    }

    [Fact]
    public void Taking_a_context_away_moves_the_presenter_back_to_the_processor()
    {
        //Arrange
        RequireGraphics();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
            using SkiaVideoPresenter presenter = new SkiaVideoPresenter(graphics);
            using VideoFrame frame =
                TestFrames.CreateFlat(pool, 32, 16, 120, 128, 128, TestFrames.Bt709Limited);

            presenter.Present(frame);
            presenter.Update();

            presenter.UseGpu(null);
            presenter.Present(frame);
            presenter.Update();

            presenter.ActiveRenderPath.Should().Be(VideoRenderBackend.Cpu);
            presenter.GetStatistics().SurfaceAllocations.Should().Be(2L);
            presenter.GetStatistics().FramesComposed.Should().Be(2L);
        });
    }

    private static void RequireGraphics() =>
        Assert.SkipUnless(
            HeadlessGraphicsContext.IsAvailable,
            "No headless graphics context: " + HeadlessGraphicsContext.UnavailableReason);

    private static byte[] ReadPixels(SKImage image, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];

        using SKPixmap map = image.PeekPixels();
        for (int row = 0; row < height; row++)
        {
            map.GetPixelSpan().Slice(row * map.RowBytes, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
        }

        return pixels;
    }

    private static int WorstDifference(byte[] expected, byte[] actual, int pixels)
    {
        int worst = 0;
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int index = (pixel * 4) + channel;
                int difference = Math.Abs(expected[index] - actual[index]);
                if (difference > worst) worst = difference;
            }
        }

        return worst;
    }
}
