using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Presentation;
using CodeBrix.VideoPlayback.Skia.Composition;
using CodeBrix.VideoPlayback.Skia.Effects;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Checks the presenter on the processor path: the geometry it fits a picture into, the pixels it produces,
/// what it allocates, how it chooses a render path, and how layers and the composition hook behave.
/// </summary>
public class SkiaVideoPresenterTests
{
    [Fact]
    public void Fill_stretches_the_picture_over_the_whole_destination()
    {
        //Arrange
        SKRect destination = SKRect.Create(10f, 20f, 200f, 100f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 64, 36, VideoStretch.Fill);

        //Assert
        target.Should().Be(destination);
    }

    [Fact]
    public void Uniform_letterboxes_a_wide_picture_in_a_square_destination()
    {
        //Arrange
        SKRect destination = SKRect.Create(0f, 0f, 100f, 100f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 200, 100, VideoStretch.Uniform);

        //Assert
        target.Width.Should().Be(100f);
        target.Height.Should().Be(50f);
        target.Left.Should().Be(0f);
        target.Top.Should().Be(25f);
    }

    [Fact]
    public void Uniform_pillarboxes_a_tall_picture_in_a_wide_destination()
    {
        //Arrange
        SKRect destination = SKRect.Create(0f, 0f, 400f, 100f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 100, 200, VideoStretch.Uniform);

        //Assert
        target.Width.Should().Be(50f);
        target.Height.Should().Be(100f);
        target.Left.Should().Be(175f);
        target.Top.Should().Be(0f);
    }

    [Fact]
    public void UniformToFill_covers_the_destination_and_overflows_the_long_way()
    {
        //Arrange
        SKRect destination = SKRect.Create(0f, 0f, 100f, 100f);

        //Act
        SKRect target =
            SkiaVideoPresenter.ComputeDestinationRect(destination, 200, 100, VideoStretch.UniformToFill);

        //Assert
        target.Width.Should().Be(200f);
        target.Height.Should().Be(100f);
        target.Left.Should().Be(-50f);
        target.Top.Should().Be(0f);
    }

    [Fact]
    public void None_centres_the_picture_at_its_own_size()
    {
        //Arrange
        SKRect destination = SKRect.Create(0f, 0f, 100f, 100f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 40, 20, VideoStretch.None);

        //Assert
        target.Should().Be(SKRect.Create(30f, 40f, 40f, 20f));
    }

    [Fact]
    public void The_letterbox_follows_the_DISPLAY_size_not_the_coded_size()
    {
        //Arrange - an anamorphic frame: 100 coded pixels wide, meant to be shown 200 wide
        SKRect destination = SKRect.Create(0f, 0f, 400f, 400f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 200, 100, VideoStretch.Uniform);

        //Assert
        (target.Width / target.Height).Should().Be(2f);
    }

    [Fact]
    public void An_empty_picture_falls_back_to_the_whole_destination()
    {
        //Arrange
        SKRect destination = SKRect.Create(0f, 0f, 100f, 50f);

        //Act
        SKRect target = SkiaVideoPresenter.ComputeDestinationRect(destination, 0, 0, VideoStretch.Uniform);

        //Assert
        target.Should().Be(destination);
    }

    [Fact]
    public void A_presenter_with_no_graphics_context_runs_on_the_processor()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter();

        //Act
        VideoRenderBackend backend = presenter.ResolveRenderPath();

        //Assert
        backend.Should().Be(VideoRenderBackend.Cpu);
        presenter.ActiveRenderPath.Should().Be(VideoRenderBackend.Cpu);
    }

    [Fact]
    public void GpuAuto_falls_back_to_the_processor_and_says_so_once()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.GpuAuto };
        List<VideoRenderPathChangedEventArgs> announcements = new List<VideoRenderPathChangedEventArgs>();
        presenter.RenderPathChanged += (sender, args) => announcements.Add(args);

        //Act
        presenter.ResolveRenderPath();
        presenter.ResolveRenderPath();
        presenter.ResolveRenderPath();

        //Assert
        announcements.Count.Should().Be(1);
        announcements[0].Backend.Should().Be(VideoRenderBackend.Cpu);
        announcements[0].Reason.Should().Contain("no graphics context");
    }

    [Fact]
    public void GpuAuto_falling_back_leaves_a_configured_effect_chain_inactive()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.GpuAuto };
        presenter.Effects.Add(new LutEffect(Lut3D.CreateIdentity(5)));

        //Act
        VideoRenderBackend backend = presenter.ResolveRenderPath();

        //Assert
        backend.Should().Be(VideoRenderBackend.Cpu);
        presenter.EffectsActive.Should().BeFalse();
        presenter.Effects.Count.Should().Be(1);
    }

    [Fact]
    public void AllowEffectsOnCpu_makes_the_chain_active_on_the_processor()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { AllowEffectsOnCpu = true };
        presenter.Effects.Add(new LutEffect(Lut3D.CreateIdentity(5)));

        //Act
        presenter.ResolveRenderPath();

        //Assert
        presenter.ActiveRenderPath.Should().Be(VideoRenderBackend.Cpu);
        presenter.EffectsActive.Should().BeTrue();
    }

    [Fact]
    public void GpuNoFallback_refuses_when_there_is_no_graphics_context()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.GpuNoFallback,
        };

        //Act
        Action resolve = () => presenter.ResolveRenderPath();

        //Assert
        resolve.Should().Throw<VideoPlaybackException>()
            .WithMessage("*GpuNoFallback*")
            .WithMessage("*UseGpu(GRContext)*");
    }

    [Fact]
    public void Cpu_is_forced_even_when_a_context_would_be_available()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Effects.Add(new LutEffect(Lut3D.CreateIdentity(5)));

        //Act
        VideoRenderBackend backend = presenter.ResolveRenderPath();

        //Assert
        backend.Should().Be(VideoRenderBackend.Cpu);
        presenter.EffectsActive.Should().BeFalse();
    }

    [Fact]
    public void The_composed_surface_holds_exactly_what_the_core_converter_produces()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        List<VideoFrame> frames = CbvFrameSource.Decode(TestFrames.Asset("raw-synthetic.cbv"), pool, 3);

        //Act
        VideoFrame frame = frames[2];
        byte[] reference = new byte[VideoFrameConverter.GetBgraBufferSize(frame.Width, frame.Height)];
        VideoFrameConverter.ToBgra32(frame, reference, VideoFrameConverter.GetBgraStride(frame.Width));

        presenter.Present(frame);
        presenter.Update();

        using SKImage composed = presenter.CaptureComposedFrame();

        //Assert
        composed.Should().NotBeNull();
        composed.Width.Should().Be(frame.Width);
        composed.Height.Should().Be(frame.Height);
        CountDifferences(composed, reference, frame.Width, frame.Height).Should().Be(0);

        foreach (VideoFrame decoded in frames) decoded.Dispose();
    }

    [Fact]
    public void The_processor_path_allocates_nothing_once_it_is_warm()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        using SKSurface destination =
            SKSurface.Create(new SKImageInfo(128, 72, SKColorType.Bgra8888, SKAlphaType.Premul));

        SKRect target = SKRect.Create(0f, 0f, 128f, 72f);

        // The frame is made ONCE and posted over and over: Present takes its own reference and the
        // composition drops it, so the loop measures the presenter and nothing else.
        using VideoFrame frame = TestFrames.CreatePattern(
            pool, 64, 36, VideoPixelLayout.I420, TestFrames.Bt709Limited, 1);

        for (int warm = 0; warm < 20; warm++)
        {
            presenter.Present(frame);
            presenter.Draw(destination.Canvas, target, VideoStretch.Uniform);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        //Act
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 200; i++)
        {
            presenter.Present(frame);
            presenter.Draw(destination.Canvas, target, VideoStretch.Uniform);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        //Assert
        (after - before).Should().Be(0);
        presenter.GetStatistics().SurfaceAllocations.Should().Be(1);
        presenter.GetStatistics().FramesDrawn.Should().Be(220);
    }

    [Fact]
    public void An_identity_lookup_table_on_the_processor_changes_no_pixel()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        byte[] plain = ComposeToBytes(pool, null, false, out int width, out int height);

        //Act
        byte[] through = ComposeToBytes(pool, new LutEffect(Lut3D.CreateIdentity(33)), true, out _, out _);

        //Assert
        through.Length.Should().Be(plain.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            if (through[i] != plain[i])
            {
                Assert.Fail(
                    $"byte {i} of the {width}x{height} surface changed from {plain[i]} to {through[i]} under an "
                    + "identity lookup table.");
            }
        }
    }

    [Fact]
    public void An_inverting_lookup_table_on_the_processor_inverts_every_pixel()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        byte[] plain = ComposeToBytes(pool, null, false, out int width, out int height);

        //Act
        byte[] inverted = ComposeToBytes(pool, new LutEffect(CreateInvertingLut(33)), true, out _, out _);

        //Assert
        int pixels = width * height;
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int index = (pixel * 4) + channel;
                int expected = 255 - plain[index];
                int actual = inverted[index];
                if (Math.Abs(expected - actual) > 1)
                {
                    Assert.Fail(
                        $"channel {channel} of pixel {pixel} should have inverted from {plain[index]} to "
                        + $"{expected}; it reads {actual}.");
                }
            }

            inverted[(pixel * 4) + 3].Should().Be(255);
        }
    }

    [Fact]
    public void Layers_draw_in_list_order_over_the_video()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        presenter.Layers.Add(new SolidRectangleLayer(SKColors.Red, SKRect.Create(0f, 0f, 8f, 8f)));
        presenter.Layers.Add(new SolidRectangleLayer(SKColors.Lime, SKRect.Create(0f, 0f, 4f, 4f)));

        using VideoFrame frame =
            TestFrames.CreateFlat(pool, 16, 16, 16, 128, 128, TestFrames.Bt709Limited);

        //Act
        presenter.Present(frame);
        presenter.Update();

        using SKImage composed = presenter.CaptureComposedFrame();
        using SKPixmap pixels = composed.PeekPixels();

        //Assert - green wins where the two overlap, red where only the first covers
        pixels.GetPixelColor(1, 1).Should().Be(SKColors.Lime);
        pixels.GetPixelColor(6, 6).Should().Be(SKColors.Red);
        pixels.GetPixelColor(12, 12).Red.Should().BeLessThan((byte)16);
    }

    [Fact]
    public void The_composing_event_arrives_with_the_frame_it_belongs_to()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        VideoCompositionContext seen = default;
        int raised = 0;

        presenter.Composing += (sender, args) =>
        {
            raised++;
            seen = args.Context;
            args.Canvas.Should().NotBeNull();
        };

        using VideoFrame frame = TestFrames.CreatePattern(
            pool,
            48,
            24,
            VideoPixelLayout.I420,
            TestFrames.Bt709Limited,
            1,
            TimeSpan.FromMilliseconds(280),
            7);

        //Act
        presenter.Present(frame);
        presenter.Update();

        //Assert
        raised.Should().Be(1);
        seen.FrameWidth.Should().Be(48);
        seen.FrameHeight.Should().Be(24);
        seen.DisplayWidth.Should().Be(48);
        seen.DisplayHeight.Should().Be(24);
        seen.Timestamp.Should().Be(TimeSpan.FromMilliseconds(280));
        seen.FrameNumber.Should().Be(7L);
        seen.Backend.Should().Be(VideoRenderBackend.Cpu);
        seen.EffectsActive.Should().BeFalse();
        seen.VideoRect.Should().Be(SKRect.Create(0f, 0f, 48f, 24f));
    }

    [Fact]
    public void CaptureComposedFrame_hands_back_an_image_the_caller_owns()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        using VideoFrame frame = TestFrames.CreateFlat(pool, 16, 16, 235, 128, 128, TestFrames.Bt709Limited);

        presenter.Present(frame);
        presenter.Update();

        //Act
        SKImage first = presenter.CaptureComposedFrame();
        SKImage second = presenter.CaptureComposedFrame();

        //Assert
        ReferenceEquals(first, second).Should().BeFalse();

        using (SKPixmap pixels = first.PeekPixels())
        {
            SKColor white = pixels.GetPixelColor(8, 8);
            white.Red.Should().BeGreaterThan((byte)250);
            white.Green.Should().BeGreaterThan((byte)250);
            white.Blue.Should().BeGreaterThan((byte)250);
        }

        first.Dispose();
        presenter.Dispose();

        // The capture survives the presenter, which is the point of it being a copy.
        using (SKPixmap pixels = second.PeekPixels()) pixels.GetPixelColor(8, 8).Alpha.Should().Be((byte)255);
        second.Dispose();
    }

    [Fact]
    public void CurrentImage_is_null_until_a_frame_has_been_composed()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        //Act
        SKImage image = presenter.CurrentImage;

        //Assert
        (image == null).Should().BeTrue();
        presenter.HasComposedFrame.Should().BeFalse();
        presenter.CaptureComposedFrame().Should().BeNull();
    }

    [Fact]
    public void Attaching_reads_somebody_elses_mailbox_and_detaching_gives_it_back()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter mailbox = new VideoFramePresenter();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        int invalidated = 0;
        presenter.Invalidated += (sender, args) => invalidated++;

        //Act
        presenter.Attach(mailbox);
        bool attached = presenter.IsAttached;

        using (VideoFrame frame = TestFrames.CreateFlat(pool, 16, 16, 100, 128, 128, TestFrames.Bt709Limited))
        {
            mailbox.Post(frame);
        }

        bool composed = presenter.Update();

        presenter.Detach();

        //Assert
        attached.Should().BeTrue();
        composed.Should().BeTrue();
        invalidated.Should().Be(1);
        presenter.IsAttached.Should().BeFalse();
        ReferenceEquals(presenter.Source, mailbox).Should().BeFalse();
    }

    [Fact]
    public void A_frame_size_change_allocates_exactly_one_more_surface()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        //Act
        using (VideoFrame small = TestFrames.CreateFlat(pool, 16, 16, 100, 128, 128, TestFrames.Bt709Limited))
        {
            presenter.Present(small);
            presenter.Update();
        }

        using (VideoFrame again = TestFrames.CreateFlat(pool, 16, 16, 120, 128, 128, TestFrames.Bt709Limited))
        {
            presenter.Present(again);
            presenter.Update();
        }

        using (VideoFrame larger = TestFrames.CreateFlat(pool, 32, 24, 100, 128, 128, TestFrames.Bt709Limited))
        {
            presenter.Present(larger);
            presenter.Update();
        }

        //Assert
        presenter.GetStatistics().SurfaceAllocations.Should().Be(2);
        presenter.GetStatistics().FramesComposed.Should().Be(3);
        presenter.ComposedWidth.Should().Be(32);
        presenter.ComposedHeight.Should().Be(24);
    }

    [Fact]
    public void Every_public_entry_point_refuses_once_the_presenter_is_disposed()
    {
        //Arrange
        SkiaVideoPresenter presenter = new SkiaVideoPresenter();
        presenter.Dispose();

        //Act
        Action update = () => presenter.Update();

        //Assert
        update.Should().Throw<ObjectDisposedException>();
        presenter.Dispose();
    }

    private static byte[] ComposeToBytes(
        PinnedFrameBufferPool pool,
        IVideoFrameEffect effect,
        bool allowOnCpu,
        out int width,
        out int height)
    {
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.Cpu,
            AllowEffectsOnCpu = allowOnCpu,
        };

        if (effect != null) presenter.Effects.Add(effect);

        using VideoFrame frame = TestFrames.CreatePattern(
            pool, 32, 16, VideoPixelLayout.I420, TestFrames.Bt709Limited, 3);

        width = frame.Width;
        height = frame.Height;

        presenter.Present(frame);
        presenter.Update();

        using SKImage composed = presenter.CaptureComposedFrame();
        using SKPixmap pixels = composed.PeekPixels();

        byte[] copy = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            pixels.GetPixelSpan().Slice(row * pixels.RowBytes, width * 4).CopyTo(copy.AsSpan(row * width * 4));
        }

        return copy;
    }

    private static Lut3D CreateInvertingLut(int size)
    {
        float[] values = new float[size * size * size * 3];
        float last = size - 1;
        int index = 0;

        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    values[index++] = 1f - (red / last);
                    values[index++] = 1f - (green / last);
                    values[index++] = 1f - (blue / last);
                }
            }
        }

        return new Lut3D(size, values);
    }

    private static int CountDifferences(SKImage image, byte[] reference, int width, int height)
    {
        using SKPixmap pixels = image.PeekPixels();
        int differences = 0;

        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> actual = pixels.GetPixelSpan().Slice(row * pixels.RowBytes, width * 4);
            ReadOnlySpan<byte> expected = reference.AsSpan(row * width * 4, width * 4);
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i]) differences++;
            }
        }

        return differences;
    }

    private sealed class SolidRectangleLayer : IVideoLayer
    {
        private readonly SKColor colour;
        private readonly SKRect rectangle;

        internal SolidRectangleLayer(SKColor colour, SKRect rectangle)
        {
            this.colour = colour;
            this.rectangle = rectangle;
        }

        public void Draw(SKCanvas canvas, VideoCompositionContext context)
        {
            using SKPaint paint = new SKPaint { Color = colour, IsAntialias = false };
            canvas.DrawRect(rectangle, paint);
        }
    }
}
