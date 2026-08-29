using System;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the reference-counting contract a decoded frame lives by: one reference at birth, one more per
/// retain, the buffer back to the pool at zero, and no reading a frame after it has gone.
/// </summary>
public class VideoFrameTests
{
    [Fact]
    public void Create_takes_one_reference()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        using VideoFrame frame = NewFrame(pool);

        //Assert
        frame.ReferenceCount.Should().Be(1);
    }

    [Fact]
    public void Retain_returns_the_same_frame_with_one_more_reference()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);

        //Act
        VideoFrame retained = frame.Retain();

        //Assert
        ReferenceEquals(frame, retained).Should().BeTrue();
        frame.ReferenceCount.Should().Be(2);

        retained.Dispose();
        frame.Dispose();
    }

    [Fact]
    public void Dispose_returns_the_buffer_only_when_the_last_reference_goes()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);
        VideoFrame second = frame.Retain();

        //Act
        frame.Dispose();
        int pooledAfterFirst = pool.GetStatistics().Pooled;
        second.Dispose();
        int pooledAfterSecond = pool.GetStatistics().Pooled;

        //Assert
        pooledAfterFirst.Should().Be(0);
        pooledAfterSecond.Should().Be(1);
    }

    [Fact]
    public void Dispose_more_times_than_retained_is_ignored()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);

        //Act
        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        //Assert
        pool.GetStatistics().Returns.Should().Be(1);
    }

    [Fact]
    public void Buffer_after_the_last_dispose_throws()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);
        frame.Dispose();

        //Act
        Action act = () => _ = frame.Buffer;

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Retain_after_the_last_dispose_throws()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);
        frame.Dispose();

        //Act
        Action act = () => frame.Retain();

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Create_reuses_frame_objects_so_a_decode_loop_allocates_nothing()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        for (int i = 0; i < 8; i++) NewFrame(pool).Dispose();

        long before = GC.GetAllocatedBytesForCurrentThread();

        //Act
        for (int i = 0; i < 500; i++) NewFrame(pool).Dispose();

        long after = GC.GetAllocatedBytesForCurrentThread();

        //Assert
        (after - before).Should().Be(0);
    }

    [Fact]
    public async Task Retain_and_dispose_are_safe_from_many_threads()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = NewFrame(pool);
        int workers = 8;
        int perWorker = 2000;

        //Act
        Task[] tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(
                () =>
                {
                    for (int n = 0; n < perWorker; n++) frame.Retain().Dispose();
                },
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);
        int remaining = frame.ReferenceCount;
        frame.Dispose();

        //Assert
        remaining.Should().Be(1);
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void Frame_reports_the_derived_sample_facts()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        using VideoFrame frame = NewFrame(pool, VideoPixelLayout.I420, 10);

        //Assert
        frame.MaxSampleValue.Should().Be(1023);
        frame.ChromaShiftX.Should().Be(1);
        frame.ChromaShiftY.Should().Be(1);
    }

    private static VideoFrame NewFrame(
        PinnedFrameBufferPool pool,
        VideoPixelLayout layout = VideoPixelLayout.I420,
        int bitDepth = 8)
    {
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(64, 36, layout, bitDepth);
        VideoFrameBuffer buffer = pool.Rent(descriptor);

        VideoFrameInfo info = new VideoFrameInfo(
            64,
            36,
            64,
            36,
            layout,
            bitDepth,
            TimeSpan.Zero,
            0,
            0,
            true,
            VideoColorInfo.Unspecified,
            null);

        return VideoFrame.Create(buffer, info, pool);
    }
}
