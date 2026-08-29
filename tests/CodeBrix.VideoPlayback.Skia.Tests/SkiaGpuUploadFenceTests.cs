using System;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Checks that the presenter's upload fence does what the pool expects of it: park a buffer until the
/// graphics work that was reading it has been submitted, and release it the moment that happens.
/// </summary>
public class SkiaGpuUploadFenceTests
{
    [Fact]
    public void A_new_fence_is_not_signalled_and_signalling_is_final()
    {
        //Arrange
        SkiaGpuUploadFence fence = new SkiaGpuUploadFence();

        //Act
        bool before = fence.IsSignaled;
        fence.Signal();
        fence.Signal();

        //Assert
        before.Should().BeFalse();
        fence.IsSignaled.Should().BeTrue();
        fence.ToString().Should().Contain("signalled");
    }

    [Fact]
    public void The_pool_parks_a_buffer_whose_fence_has_not_signalled()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        SkiaGpuUploadFence fence = new SkiaGpuUploadFence();

        VideoFrameBufferDescriptor descriptor =
            new VideoFrameBufferDescriptor(32, 16, VideoPixelLayout.I420, 8);

        VideoFrameBuffer buffer = pool.Rent(descriptor);
        buffer.Tag = fence;

        //Act
        pool.Return(buffer);
        VideoFrameBufferPoolStatistics parked = pool.GetStatistics();

        VideoFrameBuffer another = pool.Rent(descriptor);
        bool aDifferentBufferWasHandedOut = !ReferenceEquals(another, buffer);

        fence.Signal();
        int released = pool.PumpFences();

        //Assert
        parked.WaitingOnFences.Should().Be(1);
        parked.Pooled.Should().Be(0);
        aDifferentBufferWasHandedOut.Should().BeTrue();
        released.Should().Be(1);
        pool.GetStatistics().WaitingOnFences.Should().Be(0);
        pool.GetStatistics().Pooled.Should().Be(1);

        pool.Return(another);
    }

    [Fact]
    public void A_signalled_fence_lets_the_buffer_go_straight_back()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        SkiaGpuUploadFence fence = new SkiaGpuUploadFence();
        fence.Signal();

        VideoFrameBufferDescriptor descriptor =
            new VideoFrameBufferDescriptor(32, 16, VideoPixelLayout.I420, 8);

        VideoFrameBuffer buffer = pool.Rent(descriptor);
        buffer.Tag = fence;

        //Act
        pool.Return(buffer);

        //Assert
        pool.GetStatistics().WaitingOnFences.Should().Be(0);
        pool.GetStatistics().Pooled.Should().Be(1);
        ReferenceEquals(pool.Rent(descriptor), buffer).Should().BeTrue();
    }

    [Fact]
    public void Renting_clears_a_tag_so_a_recycled_buffer_never_carries_somebody_elses_fence()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBufferDescriptor descriptor =
            new VideoFrameBufferDescriptor(32, 16, VideoPixelLayout.I420, 8);

        VideoFrameBuffer buffer = pool.Rent(descriptor);
        SkiaGpuUploadFence fence = new SkiaGpuUploadFence();
        fence.Signal();
        buffer.Tag = fence;
        pool.Return(buffer);

        //Act
        VideoFrameBuffer recycled = pool.Rent(descriptor);

        //Assert
        (recycled.Tag == null).Should().BeTrue();
        recycled.IsFenceSignaled().Should().BeTrue();
    }
}
