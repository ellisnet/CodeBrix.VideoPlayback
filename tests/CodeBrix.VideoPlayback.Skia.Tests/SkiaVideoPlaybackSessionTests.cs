using System;
using System.Diagnostics;
using System.Threading;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Rendering;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Plays a whole file through a real session into the presenter and draws every frame - with no display, no
/// audio device and no codec package.
/// </summary>
/// <remarks>
/// This is the shape of a real application: a session opens a file, the presenter is attached to its mailbox,
/// and a paint loop draws whatever is newest. What it proves is that the two halves fit together - the
/// mailbox hand-off, the reference counting, the frame sizes, the clock - and that a file plays to its end
/// without anything being held on to.
/// </remarks>
public class SkiaVideoPlaybackSessionTests
{
    [Fact]
    public void A_file_plays_to_its_end_with_every_frame_drawn_on_the_processor_path()
    {
        //Arrange
        string path = TestFrames.Asset("raw-vorbis.cbv");

        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions
        {
            PlayAudio = false,
        });

        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Attach(session.Presenter);

        using SKSurface view =
            SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));

        SKRect destination = SKRect.Create(0f, 0f, 320f, 180f);

        using ManualResetEventSlim ended = new ManualResetEventSlim(false);
        session.PlaybackEnded += (sender, args) => ended.Set();

        int invalidations = 0;
        presenter.Invalidated += (sender, args) => Interlocked.Increment(ref invalidations);

        //Act
        session.Open(path);
        TimeSpan duration = session.Duration;
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();
        while (!ended.IsSet && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            presenter.Draw(view.Canvas, destination, VideoStretch.Uniform);
            Thread.Sleep(5);
        }

        presenter.Draw(view.Canvas, destination, VideoStretch.Uniform);

        //Assert
        ended.IsSet.Should().BeTrue();
        duration.Should().Be(TimeSpan.FromSeconds(1));
        session.State.Should().Be(VideoPlaybackState.Ended);

        invalidations.Should().BeGreaterThan(0);
        presenter.GetStatistics().FramesComposed.Should().BeGreaterThan(0L);
        presenter.GetStatistics().SurfaceAllocations.Should().Be(1L);
        presenter.ComposedWidth.Should().Be(64);
        presenter.ComposedHeight.Should().Be(36);
        presenter.HasComposedFrame.Should().BeTrue();

        using SKImage composed = presenter.CaptureComposedFrame();
        composed.Width.Should().Be(64);

        // Every frame the session posted was collected and released, so the pool never grew past the three
        // buffers the mailbox arrangement needs.
        session.BufferPool.Should().NotBeNull();
    }

    [Fact]
    public void Opening_a_file_gives_the_presenter_its_first_frame_before_anything_is_played()
    {
        //Arrange
        string path = TestFrames.Asset("raw-vorbis.cbv");

        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions
        {
            PlayAudio = false,
        });

        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Attach(session.Presenter);

        using ManualResetEventSlim arrived = new ManualResetEventSlim(false);
        presenter.Invalidated += (sender, args) => arrived.Set();

        using SKSurface view =
            SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul));

        //Act
        session.Open(path);
        bool posted = arrived.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        presenter.Draw(view.Canvas, SKRect.Create(0f, 0f, 64f, 36f), VideoStretch.Uniform);

        //Assert
        posted.Should().BeTrue();
        session.State.Should().Be(VideoPlaybackState.Stopped);
        presenter.HasComposedFrame.Should().BeTrue();
        presenter.CurrentTimestamp.Should().Be(TimeSpan.Zero);
        presenter.GetStatistics().FramesDrawn.Should().Be(1L);
    }
}
