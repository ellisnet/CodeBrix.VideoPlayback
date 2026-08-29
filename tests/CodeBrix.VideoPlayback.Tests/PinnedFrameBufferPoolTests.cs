using System;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the frame-buffer pool's promises: the layout a software decoder and a graphics upload both need,
/// no allocation once playback is warm, a new generation when the frame size changes, and a buffer that does
/// not come back until every reader and every fence has finished with it.
/// </summary>
public class PinnedFrameBufferPoolTests
{
    [Fact]
    public void Rent_returns_planes_aligned_to_64_bytes()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(1920, 1080, VideoPixelLayout.I420, 8);

        //Act
        VideoFrameBuffer buffer = pool.Rent(descriptor);

        //Assert
        ((long)buffer.Y.Data % 64).Should().Be(0);
        ((long)buffer.U.Data % 64).Should().Be(0);
        ((long)buffer.V.Data % 64).Should().Be(0);
    }

    [Fact]
    public void Rent_returns_strides_that_are_a_multiple_of_64_bytes()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(37, 21, VideoPixelLayout.I420, 8));

        //Assert
        (buffer.Y.Stride % 64).Should().Be(0);
        (buffer.U.Stride % 64).Should().Be(0);
        buffer.V.Stride.Should().Be(buffer.U.Stride);
    }

    [Fact]
    public void Rent_rounds_both_dimensions_up_to_a_multiple_of_128()
    {
        //Arrange
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(37, 21, VideoPixelLayout.I420, 8);

        //Act
        int paddedWidth = descriptor.PaddedWidth;
        int paddedHeight = descriptor.PaddedHeight;

        //Assert
        paddedWidth.Should().Be(128);
        paddedHeight.Should().Be(128);
    }

    [Fact]
    public void Rent_leaves_64_bytes_of_tail_padding_after_every_plane()
    {
        //Arrange
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8);

        //Act
        long expected = (128L * 128 + 64) + 2 * (64L * 64 + 64);

        //Assert
        descriptor.AllocationBytes.Should().Be(expected);
    }

    [Fact]
    public void Rent_stores_more_than_8_bit_samples_as_16_bit_words()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        VideoFrameBuffer ten = pool.Rent(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 10));

        //Assert
        ten.Y.BytesPerSample.Should().Be(2);
        ten.Y.Stride.Should().Be(256);
    }

    [Fact]
    public void Rent_gives_a_monochrome_buffer_no_chroma_planes()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.Gray, 8));

        //Assert
        buffer.U.IsEmpty.Should().BeTrue();
        buffer.V.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Rent_never_allocates_in_the_steady_state()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(640, 360, VideoPixelLayout.I420, 8);

        for (int i = 0; i < 8; i++) pool.Return(pool.Rent(descriptor));

        long allocationsAfterWarmUp = pool.GetStatistics().Allocations;
        long managedBytesBefore = GC.GetAllocatedBytesForCurrentThread();

        //Act
        for (int i = 0; i < 500; i++) pool.Return(pool.Rent(descriptor));

        long managedBytesAfter = GC.GetAllocatedBytesForCurrentThread();
        VideoFrameBufferPoolStatistics statistics = pool.GetStatistics();

        //Assert
        statistics.Allocations.Should().Be(allocationsAfterWarmUp);
        statistics.Rents.Should().Be(508);
        (managedBytesAfter - managedBytesBefore).Should().Be(0);
    }

    [Fact]
    public void Rent_starts_a_new_generation_when_the_frame_size_changes()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        pool.Return(pool.Rent(new VideoFrameBufferDescriptor(320, 180, VideoPixelLayout.I420, 8)));
        int firstGeneration = pool.Generation;

        //Act
        VideoFrameBuffer larger = pool.Rent(new VideoFrameBufferDescriptor(640, 360, VideoPixelLayout.I420, 8));

        //Assert
        pool.Generation.Should().Be(firstGeneration + 1);
        larger.Generation.Should().Be(pool.Generation);
        pool.GetStatistics().Pooled.Should().Be(0);
    }

    [Fact]
    public void Return_frees_a_buffer_from_an_older_generation_instead_of_pooling_it()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBuffer small = pool.Rent(new VideoFrameBufferDescriptor(320, 180, VideoPixelLayout.I420, 8));
        pool.Rent(new VideoFrameBufferDescriptor(640, 360, VideoPixelLayout.I420, 8));

        //Act
        pool.Return(small);

        //Assert
        pool.GetStatistics().Pooled.Should().Be(0);
        ((PinnedVideoFrameBuffer)small).IsFreed.Should().BeTrue();
    }

    [Fact]
    public void Return_holds_a_buffer_back_until_its_fence_signals()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8));
        TestFence fence = new TestFence();
        buffer.Tag = fence;

        //Act
        pool.Return(buffer);
        VideoFrameBufferPoolStatistics whileFenced = pool.GetStatistics();
        fence.IsSignaled = true;
        int collected = pool.PumpFences();

        //Assert
        whileFenced.WaitingOnFences.Should().Be(1);
        whileFenced.Pooled.Should().Be(0);
        collected.Should().Be(1);
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void Return_accepts_a_predicate_in_the_tag_as_a_fence()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8));
        bool done = false;
        buffer.Tag = (Func<bool>)(() => done);

        //Act
        pool.Return(buffer);
        int beforeSignal = pool.GetStatistics().WaitingOnFences;
        done = true;
        pool.PumpFences();

        //Assert
        beforeSignal.Should().Be(1);
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void Rent_clears_the_tag_it_hands_out()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8);
        VideoFrameBuffer first = pool.Rent(descriptor);
        first.Tag = "something the presenter left behind";
        pool.Return(first);

        //Act
        VideoFrameBuffer second = pool.Rent(descriptor);

        //Assert
        ReferenceEquals(first, second).Should().BeTrue();
        (second.Tag == null).Should().BeTrue();
    }

    [Fact]
    public void Rent_after_dispose_throws()
    {
        //Arrange
        PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        pool.Dispose();

        //Act
        Action act = () => pool.Rent(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8));

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Return_refuses_a_buffer_from_another_pool()
    {
        //Arrange
        using PinnedFrameBufferPool first = new PinnedFrameBufferPool();
        VideoFrameBuffer foreign = new ForeignBuffer();

        //Act
        Action act = () => first.Return(foreign);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 64, 8)]
    [InlineData(64, 0, 8)]
    [InlineData(64, 64, 9)]
    public void Descriptor_refuses_impossible_shapes(int width, int height, int bitDepth)
    {
        //Arrange
        Action act = () => _ = new VideoFrameBufferDescriptor(width, height, VideoPixelLayout.I420, bitDepth);

        //Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class TestFence : IVideoFrameFence
    {
        public bool IsSignaled { get; set; }
    }

    private sealed class ForeignBuffer : VideoFrameBuffer
    {
        public ForeignBuffer()
            : base(new VideoFrameBufferDescriptor(64, 64, VideoPixelLayout.I420, 8), VideoFrameStorage.HostMemory)
        {
        }
    }
}
