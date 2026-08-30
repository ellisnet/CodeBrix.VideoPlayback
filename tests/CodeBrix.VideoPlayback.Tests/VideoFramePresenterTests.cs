using System;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Presentation;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the mailbox between the decoding thread and the drawing thread: newest frame wins, superseded
/// frames are released, the taker owns what it takes, and nothing allocates per frame.
/// </summary>
public class VideoFramePresenterTests
{
    [Fact]
    public void TryTakeLatest_on_an_empty_presenter_returns_false()
    {
        //Arrange
        using VideoFramePresenter presenter = new VideoFramePresenter();

        //Act
        bool taken = presenter.TryTakeLatest(out VideoFrame frame);

        //Assert
        taken.Should().BeFalse();
        (frame == null).Should().BeTrue();
    }

    [Fact]
    public void Post_takes_its_own_reference_and_leaves_the_caller_theirs()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();
        VideoFrame frame = NewFrame(pool, TimeSpan.Zero);

        //Act
        presenter.Post(frame);
        int afterPost = frame.ReferenceCount;
        frame.Dispose();

        //Assert
        afterPost.Should().Be(2);
        presenter.HasFrame.Should().BeTrue();
        pool.GetStatistics().Pooled.Should().Be(0);
    }

    [Fact]
    public void Post_replaces_a_frame_nobody_collected_and_counts_it_as_superseded()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();

        //Act
        using (VideoFrame first = NewFrame(pool, TimeSpan.Zero)) presenter.Post(first);
        using (VideoFrame second = NewFrame(pool, TimeSpan.FromSeconds(1))) presenter.Post(second);

        presenter.TryTakeLatest(out VideoFrame taken);
        TimeSpan takenTimestamp = taken.Timestamp;
        taken.Dispose();

        //Assert
        takenTimestamp.Should().Be(TimeSpan.FromSeconds(1));
        presenter.GetStatistics().Superseded.Should().Be(1);
        presenter.GetStatistics().Presented.Should().Be(1);
        presenter.GetStatistics().Posted.Should().Be(2);
    }

    [Fact]
    public void TryTakeLatest_hands_the_mailbox_reference_to_the_caller()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();
        using (VideoFrame frame = NewFrame(pool, TimeSpan.Zero)) presenter.Post(frame);

        //Act
        presenter.TryTakeLatest(out VideoFrame taken);
        int beforeDispose = pool.GetStatistics().Pooled;
        taken.Dispose();

        //Assert
        beforeDispose.Should().Be(0);
        presenter.HasFrame.Should().BeFalse();
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void Invalidated_fires_once_per_post()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();
        int raised = 0;
        presenter.Invalidated += (s, e) => raised++;

        //Act
        using (VideoFrame first = NewFrame(pool, TimeSpan.Zero)) presenter.Post(first);
        using (VideoFrame second = NewFrame(pool, TimeSpan.FromSeconds(1))) presenter.Post(second);

        //Assert
        raised.Should().Be(2);
    }

    [Fact]
    public void Clear_releases_the_waiting_frame()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();
        using (VideoFrame frame = NewFrame(pool, TimeSpan.Zero)) presenter.Post(frame);

        //Act
        presenter.Clear();

        //Assert
        presenter.HasFrame.Should().BeFalse();
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void NotifyLateFrameDropped_counts_separately_from_superseded()
    {
        //Arrange
        using VideoFramePresenter presenter = new VideoFramePresenter();

        //Act
        presenter.NotifyLateFrameDropped();
        presenter.NotifyLateFrameDropped(3);

        //Assert
        presenter.GetStatistics().Late.Should().Be(4);
        presenter.GetStatistics().Superseded.Should().Be(0);
        presenter.GetStatistics().Dropped.Should().Be(4);
    }

    [Fact]
    public void Post_and_take_allocate_nothing_per_frame()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFramePresenter presenter = new VideoFramePresenter();

        for (int i = 0; i < 8; i++)
        {
            using (VideoFrame warm = NewFrame(pool, TimeSpan.Zero)) presenter.Post(warm);
            presenter.TryTakeLatest(out VideoFrame taken);
            taken.Dispose();
        }

        //Act
        long allocated = SteadyStateAllocation.MeasureSmallest(
            () =>
            {
                for (int i = 0; i < 500; i++)
                {
                    VideoFrame frame = NewFrame(pool, TimeSpan.Zero);
                    presenter.Post(frame);
                    frame.Dispose();
                    presenter.TryTakeLatest(out VideoFrame taken);
                    taken.Dispose();
                }
            });

        //Assert
        allocated.Should().Be(0);
    }

    [Fact]
    public void Dispose_releases_the_waiting_frame_and_refuses_further_posts()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFramePresenter presenter = new VideoFramePresenter();
        using (VideoFrame frame = NewFrame(pool, TimeSpan.Zero)) presenter.Post(frame);

        //Act
        presenter.Dispose();
        using (VideoFrame after = NewFrame(pool, TimeSpan.Zero)) presenter.Post(after);

        //Assert
        presenter.HasFrame.Should().BeFalse();
        pool.GetStatistics().Live.Should().Be(0);
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    private static VideoFrame NewFrame(PinnedFrameBufferPool pool, TimeSpan timestamp)
    {
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(64, 36, VideoPixelLayout.I420, 8);
        VideoFrameBuffer buffer = pool.Rent(descriptor);

        VideoFrameInfo info = new VideoFrameInfo(
            64,
            36,
            64,
            36,
            VideoPixelLayout.I420,
            8,
            timestamp,
            timestamp.Ticks,
            0,
            true,
            VideoColorInfo.Unspecified,
            null);

        return VideoFrame.Create(buffer, info, pool);
    }
}
