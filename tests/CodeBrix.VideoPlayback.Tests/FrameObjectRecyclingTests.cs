using System;
using System.Collections.Generic;
using System.Threading;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the other half of the "playback allocates nothing per frame" promise: the small
/// <see cref="VideoFrame" /> OBJECT that describes a buffer is recycled too, and a pool that does not want
/// to recycle it still works.
/// </summary>
/// <remarks>
/// <para>
/// Buffer pooling was always here. Frame-object pooling was, until now, reachable only by
/// <see cref="PinnedFrameBufferPool" /> through an internal seam, so any OTHER implementation of
/// <see cref="IVideoFrameBufferPool" /> allocated one object per decoded picture. That mattered more than it
/// looked: a real decoder has to interpose a pool of its own between the frame and the session's pool - it
/// is the only hook it has for "the application has finished with this picture" - and so the one pool in
/// the family that recycled frame objects was never the one a frame was created with.
/// </para>
/// <para>
/// <see cref="IVideoFrameBufferPool.TakeFrame" /> and
/// <see cref="IVideoFrameBufferPool.ReturnFrame" /> close that. They have default implementations - allocate
/// one, keep none - so every existing implementation keeps working exactly as it did; a pool that cares
/// overrides them.
/// </para>
/// </remarks>
public class FrameObjectRecyclingTests
{
    /// <summary>
    /// A pool that implements ONLY the two original members, so the default implementations of the two new
    /// ones are what run. Buffers come from a real pool underneath, so the memory behaviour is the shipped
    /// behaviour and only the frame-object policy is under test.
    /// </summary>
    private sealed class MinimalPool : IVideoFrameBufferPool, IDisposable
    {
        private readonly PinnedFrameBufferPool inner = new PinnedFrameBufferPool();

        public int Rents { get; private set; }

        public int Returns { get; private set; }

        public VideoFrameBuffer Rent(VideoFrameBufferDescriptor descriptor)
        {
            Rents++;
            return inner.Rent(descriptor);
        }

        public void Return(VideoFrameBuffer buffer)
        {
            Returns++;
            inner.Return(buffer);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>A pool that overrides both new members and counts the calls.</summary>
    private sealed class CountingPool : IVideoFrameBufferPool, IDisposable
    {
        private readonly PinnedFrameBufferPool inner = new PinnedFrameBufferPool();
        private readonly Stack<VideoFrame> spare = new Stack<VideoFrame>();

        public int Taken { get; private set; }

        public int GivenBack { get; private set; }

        public int Recycled { get; private set; }

        public VideoFrameBuffer Rent(VideoFrameBufferDescriptor descriptor) => inner.Rent(descriptor);

        public void Return(VideoFrameBuffer buffer) => inner.Return(buffer);

        public VideoFrame TakeFrame()
        {
            Taken++;

            if (spare.Count == 0) return VideoFrame.CreateUninitialized();

            Recycled++;
            return spare.Pop();
        }

        public void ReturnFrame(VideoFrame frame)
        {
            GivenBack++;
            spare.Push(frame);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>A pool that breaks the contract by answering null.</summary>
    private sealed class NullAnsweringPool : IVideoFrameBufferPool, IDisposable
    {
        private readonly PinnedFrameBufferPool inner = new PinnedFrameBufferPool();

        public VideoFrameBuffer Rent(VideoFrameBufferDescriptor descriptor) => inner.Rent(descriptor);

        public void Return(VideoFrameBuffer buffer) => inner.Return(buffer);

        public VideoFrame TakeFrame() => null;

        public void ReturnFrame(VideoFrame frame)
        {
        }

        public void Dispose() => inner.Dispose();
    }

    private static readonly VideoFrameBufferDescriptor Shape =
        new VideoFrameBufferDescriptor(64, 36, VideoPixelLayout.I420, 8);

    [Fact]
    public void A_pool_that_implements_only_the_original_members_still_produces_working_frames()
    {
        //Arrange
        using MinimalPool pool = new MinimalPool();
        VideoFrameBuffer buffer = pool.Rent(Shape);

        //Act
        VideoFrame frame = VideoFrame.Create(buffer, Describe(), pool);
        int width = frame.Width;
        frame.Dispose();

        //Assert
        width.Should().Be(64);
        pool.Rents.Should().Be(1);
        pool.Returns.Should().Be(1);
    }

    [Fact]
    public void The_default_take_frame_hands_out_a_fresh_object_every_time()
    {
        //Arrange
        using MinimalPool pool = new MinimalPool();
        IVideoFrameBufferPool contract = pool;

        //Act
        VideoFrame first = contract.TakeFrame();
        VideoFrame second = contract.TakeFrame();
        contract.ReturnFrame(first);
        VideoFrame third = contract.TakeFrame();

        //Assert
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second);

        // Nothing was kept, so returning one changes nothing about the next answer - which is exactly the
        // behaviour every implementation had before these members existed.
        third.Should().NotBeSameAs(first);
        third.Should().NotBeSameAs(second);
    }

    [Fact]
    public void The_default_return_frame_keeps_nothing_and_refuses_nothing()
    {
        //Arrange
        using MinimalPool pool = new MinimalPool();
        IVideoFrameBufferPool contract = pool;
        Action act = () =>
        {
            contract.ReturnFrame(contract.TakeFrame());
            contract.ReturnFrame(null);
        };

        //Act & Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Creating_and_disposing_a_frame_calls_the_pools_own_take_and_return()
    {
        //Arrange
        using CountingPool pool = new CountingPool();

        //Act
        for (int index = 0; index < 5; index++)
        {
            VideoFrameBuffer buffer = pool.Rent(Shape);
            VideoFrame frame = VideoFrame.Create(buffer, Describe(index), pool);
            frame.Dispose();
        }

        //Assert
        pool.Taken.Should().Be(5);
        pool.GivenBack.Should().Be(5);

        // The first take had nothing to reuse; every one after it did.
        pool.Recycled.Should().Be(4);
    }

    [Fact]
    public void A_frame_object_comes_back_exactly_once_however_often_it_is_disposed()
    {
        //Arrange
        using CountingPool pool = new CountingPool();
        VideoFrameBuffer buffer = pool.Rent(Shape);
        VideoFrame frame = VideoFrame.Create(buffer, Describe(), pool);

        //Act
        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        //Assert
        pool.GivenBack.Should().Be(1);
    }

    [Fact]
    public void A_retained_frame_comes_back_only_when_the_last_reference_goes()
    {
        //Arrange
        using CountingPool pool = new CountingPool();
        VideoFrameBuffer buffer = pool.Rent(Shape);
        VideoFrame frame = VideoFrame.Create(buffer, Describe(), pool);
        VideoFrame second = frame.Retain();

        //Act
        frame.Dispose();
        int afterFirst = pool.GivenBack;
        second.Dispose();

        //Assert
        afterFirst.Should().Be(0);
        pool.GivenBack.Should().Be(1);
    }

    [Fact]
    public void A_pool_that_answers_null_gets_a_frame_anyway_rather_than_an_exception()
    {
        //Arrange
        using NullAnsweringPool pool = new NullAnsweringPool();
        VideoFrameBuffer buffer = pool.Rent(Shape);

        //Act
        VideoFrame frame = VideoFrame.Create(buffer, Describe(), pool);
        int height = frame.Height;
        frame.Dispose();

        //Assert
        // Answering null breaks the contract, but losing the picture would be a worse answer than allocating.
        frame.Should().NotBeNull();
        height.Should().Be(36);
    }

    [Fact]
    public void The_pinned_pool_recycles_frame_objects_through_the_public_members()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        IVideoFrameBufferPool contract = pool;

        //Act
        VideoFrame first = contract.TakeFrame();
        contract.ReturnFrame(first);
        VideoFrame second = contract.TakeFrame();

        //Assert
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void The_pinned_pools_public_members_and_its_internal_seam_are_the_same_free_list()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        IVideoFrameBufferPool contract = pool;

        //Act
        VideoFrame taken = pool.TakeFrameObject();
        contract.ReturnFrame(taken);
        VideoFrame throughInternal = pool.TakeFrameObject();

        contract.ReturnFrame(throughInternal);
        VideoFrame throughPublic = contract.TakeFrame();

        //Assert
        throughInternal.Should().BeSameAs(taken);
        throughPublic.Should().BeSameAs(taken);
    }

    [Fact]
    public void The_pinned_pool_ignores_a_null_frame()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        IVideoFrameBufferPool contract = pool;
        Action act = () => contract.ReturnFrame(null);

        //Act & Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void A_warm_loop_over_the_pinned_pool_allocates_nothing_at_all()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        // Warm up: the first pass allocates the buffer, the frame object and the pool's own free lists.
        for (int index = 0; index < 32; index++) Cycle(pool, index);

        //Act
        long allocated = SteadyStateAllocation.MeasureSmallest(
            () =>
            {
                for (int index = 0; index < 512; index++) Cycle(pool, index);
            });

        //Assert
        // Not "small". None. The buffer comes back to the pool and so does the object over it, so five
        // hundred frames through a warm pool touch the managed heap not at all.
        allocated.Should().Be(0);
        pool.GetStatistics().Allocations.Should().Be(1);
    }

    [Fact]
    public void A_warm_loop_over_a_pool_that_recycles_nothing_allocates_one_object_per_frame()
    {
        //Arrange
        using MinimalPool pool = new MinimalPool();
        for (int index = 0; index < 32; index++) Cycle(pool, index);

        //Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Frames = 512;
        for (int index = 0; index < Frames; index++) Cycle(pool, index);
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / Frames;

        //Assert
        // This is what the default implementations cost, stated rather than assumed: one frame object per
        // picture and nothing else. It is the number a pool avoids by overriding the two members.
        perFrame.Should().BeGreaterThan(0);
        perFrame.Should().BeLessThan(512);
    }

    [Fact]
    public void Frame_objects_can_be_given_back_from_another_thread()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        IVideoFrameBufferPool contract = pool;
        VideoFrameBuffer buffer = pool.Rent(Shape);
        VideoFrame frame = VideoFrame.Create(buffer, Describe(), pool);
        int disposingThread = 0;

        //Act
        Thread worker = new Thread(() =>
        {
            disposingThread = Environment.CurrentManagedThreadId;
            frame.Dispose();
        });

        worker.Start();
        worker.Join();

        VideoFrame reused = contract.TakeFrame();

        //Assert
        disposingThread.Should().NotBe(Environment.CurrentManagedThreadId);
        reused.Should().BeSameAs(frame);
        pool.GetStatistics().Live.Should().Be(0);
    }

    private static void Cycle(IVideoFrameBufferPool pool, int number)
    {
        VideoFrameBuffer buffer = pool.Rent(Shape);
        VideoFrame frame = VideoFrame.Create(buffer, Describe(number), pool);
        frame.Dispose();
    }

    private static VideoFrameInfo Describe(int number = 0) =>
        new VideoFrameInfo(
            64,
            36,
            64,
            36,
            VideoPixelLayout.I420,
            8,
            TimeSpan.FromMilliseconds(number * 40),
            number * 400000L,
            number,
            number == 0,
            VideoColorInfo.Unspecified,
            null);
}
