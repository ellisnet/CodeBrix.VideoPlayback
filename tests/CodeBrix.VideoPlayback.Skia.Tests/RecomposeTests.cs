using System;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Effects;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Rendering;
using CodeBrix.VideoPlayback.Tests;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Checks that an effect chain edited while playback is PAUSED reaches the screen: the presenter keeps the
/// frame it composed, and <c>Recompose</c> builds the picture again from it.
/// </summary>
/// <remarks>
/// Without this entry point the composed picture is only ever rebuilt when a frame arrives, so a grade
/// dialled in on a paused player showed nothing until the user pressed play - which is what made the whole
/// lookup-table panel feel broken. Everything here runs on the processor path, so no graphics device is
/// needed to prove it.
/// </remarks>
public class RecomposeTests
{
    [Fact]
    public void Recomposing_before_the_first_frame_does_nothing_at_all()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        var invalidations = 0;
        presenter.Invalidated += (_, _) => invalidations++;

        //Act
        presenter.Recompose();

        //Assert
        presenter.HasComposedFrame.Should().BeFalse();
        presenter.CurrentImage.Should().BeNull();
        presenter.GetStatistics().FramesComposed.Should().Be(0L);
        invalidations.Should().Be(0);
    }

    [Fact]
    public void An_effect_added_while_paused_changes_the_picture_and_raises_Invalidated()
    {
        //Arrange - one frame presented, then nothing else ever arrives: the paused player
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.Cpu,
            AllowEffectsOnCpu = true,
            EffectLutSize = 9,
        };

        using (VideoFrame frame = TestFrames.CreatePattern(
            pool,
            16,
            16,
            VideoPixelLayout.I420,
            TestFrames.Bt709Limited))
        {
            presenter.Present(frame);
        }

        presenter.Update().Should().BeTrue();

        uint[] before = ReadPixels(presenter);
        long composedBefore = presenter.GetStatistics().FramesComposed;

        var invalidations = 0;
        presenter.Invalidated += (_, _) => invalidations++;

        //Act - the edit alone reaches nothing; the recompose is what puts it on the surface
        presenter.Effects.Add(new LutEffect(TestLuts.Invert(9), "invert"));
        uint[] afterTheEditAlone = ReadPixels(presenter);

        presenter.Recompose();
        uint[] afterTheRecompose = ReadPixels(presenter);

        //Assert
        afterTheEditAlone.Should().Equal(before);
        afterTheRecompose.Should().NotEqual(before);
        invalidations.Should().Be(1);

        VideoCompositionStatistics statistics = presenter.GetStatistics();
        statistics.FramesComposed.Should().Be(composedBefore + 1);
        statistics.EffectCompositions.Should().Be(1L);

        //Assert - an inversion, so every channel is the complement of what it was
        for (var i = 0; i < before.Length; i++)
        {
            Complement(before[i], afterTheRecompose[i]).Should().BeTrue();
        }
    }

    [Fact]
    public void Recomposing_the_same_chain_twice_costs_one_composition_and_no_refold()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.Cpu,
            AllowEffectsOnCpu = true,
            EffectLutSize = 9,
        };

        using (VideoFrame frame = TestFrames.CreatePattern(
            pool,
            16,
            16,
            VideoPixelLayout.I420,
            TestFrames.Bt709Limited))
        {
            presenter.Present(frame);
        }

        presenter.Update();
        presenter.Effects.Add(new LutEffect(TestLuts.Scale(9, 0.5f), "halve"));
        presenter.Recompose();
        uint[] first = ReadPixels(presenter);

        //Act
        presenter.Recompose();
        uint[] second = ReadPixels(presenter);

        //Assert - the chain was folded once, and composing it again gives the same picture
        second.Should().Equal(first);
        presenter.GetStatistics().EffectCompositions.Should().Be(1L);
        presenter.GetStatistics().FramesComposed.Should().Be(3L);
    }

    [Fact]
    public void The_retained_frame_is_released_when_the_presenter_is_disposed()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };

        using (VideoFrame frame = TestFrames.CreatePattern(
            pool,
            16,
            16,
            VideoPixelLayout.I420,
            TestFrames.Bt709Limited))
        {
            presenter.Present(frame);
        }

        presenter.Update();
        pool.GetStatistics().Live.Should().Be(1);

        //Act
        presenter.Dispose();
        pool.PumpFences();

        //Assert - the buffer the presenter was holding for a recompose is back in the pool
        pool.GetStatistics().Live.Should().Be(0);
        pool.GetStatistics().Returns.Should().Be(1L);
    }

    [Fact]
    public void Recomposing_a_disposed_presenter_is_refused()
    {
        //Arrange
        SkiaVideoPresenter presenter = new SkiaVideoPresenter();
        presenter.Dispose();

        //Act
        Action recomposing = presenter.Recompose;

        //Assert
        recomposing.Should().Throw<ObjectDisposedException>();
    }

    private static bool Complement(uint before, uint after)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            int one = (int)((before >> shift) & 0xFF);
            int other = (int)((after >> shift) & 0xFF);
            if (Math.Abs(255 - one - other) > 2) return false;
        }

        return true;
    }

    private static uint[] ReadPixels(SkiaVideoPresenter presenter)
    {
        using SKImage image = presenter.CaptureComposedFrame();
        image.Should().NotBeNull();

        SKImageInfo info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        uint[] pixels = new uint[image.Width * image.Height];

        unsafe
        {
            fixed (uint* target = pixels)
            {
                image.ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0).Should().BeTrue();
            }
        }

        return pixels;
    }
}
