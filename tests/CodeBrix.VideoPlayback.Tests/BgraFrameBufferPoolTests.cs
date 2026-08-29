using System;
using CodeBrix.VideoPlayback.Color;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Pins the promises the BGRA surface pool makes to the GPU-free render path: aligned memory, packed rows,
/// reuse rather than reallocation, and a clean release.
/// </summary>
public class BgraFrameBufferPoolTests
{
    [Fact]
    public void Rent_returns_a_surface_aligned_to_sixty_four_bytes()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();

        //Act
        BgraFrameBuffer buffer = pool.Rent(1920, 1080);

        //Assert
        ((long)buffer.Data % BgraFrameBuffer.Alignment).Should().Be(0L);
    }

    [Fact]
    public void Rent_returns_a_surface_whose_rows_are_packed()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();

        //Act
        BgraFrameBuffer buffer = pool.Rent(37, 11);

        //Assert
        buffer.Stride.Should().Be(37 * 4);
        buffer.SizeInBytes.Should().Be(37L * 4 * 11);
        buffer.Width.Should().Be(37);
        buffer.Height.Should().Be(11);
    }

    [Fact]
    public void Rent_reuses_a_returned_surface_of_the_same_size()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer first = pool.Rent(64, 36);
        pool.Return(first);

        //Act
        BgraFrameBuffer second = pool.Rent(64, 36);

        //Assert
        second.Should().BeSameAs(first);
        pool.Allocations.Should().Be(1L);
    }

    [Fact]
    public void Rent_allocates_again_for_a_different_size()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        pool.Return(pool.Rent(64, 36));

        //Act
        BgraFrameBuffer bigger = pool.Rent(128, 72);

        //Assert
        bigger.Width.Should().Be(128);
        pool.Allocations.Should().Be(2L);
    }

    [Fact]
    public void Rent_and_Return_stop_allocating_in_the_steady_state()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        pool.Return(pool.Rent(320, 180));
        long allocationsAfterWarmUp = pool.Allocations;

        //Act
        for (int iteration = 0; iteration < 200; iteration++)
        {
            pool.Return(pool.Rent(320, 180));
        }

        //Assert
        pool.Allocations.Should().Be(allocationsAfterWarmUp);
    }

    [Fact]
    public void Pooled_counts_the_surfaces_waiting_to_be_reused()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer first = pool.Rent(16, 16);
        BgraFrameBuffer second = pool.Rent(16, 16);

        //Act
        pool.Return(first);
        pool.Return(second);

        //Assert
        pool.Pooled.Should().Be(2);
    }

    [Fact]
    public void Trim_frees_every_size_but_the_one_kept()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer small = pool.Rent(16, 16);
        BgraFrameBuffer large = pool.Rent(64, 64);
        pool.Return(small);
        pool.Return(large);

        //Act
        int freed = pool.Trim(64, 64);

        //Assert
        freed.Should().Be(1);
        pool.Pooled.Should().Be(1);
        small.IsFreed.Should().BeTrue();
        large.IsFreed.Should().BeFalse();
    }

    [Fact]
    public void AsSpan_covers_the_whole_surface()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(8, 4);

        //Act
        Span<byte> span = buffer.AsSpan();
        span.Fill(0x5A);

        //Assert
        span.Length.Should().Be(8 * 4 * 4);
        buffer.GetRow(3)[0].Should().Be(0x5A);
    }

    [Fact]
    public void Clear_zeroes_the_surface()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(8, 4);
        buffer.AsSpan().Fill(0xFF);

        //Act
        buffer.Clear();

        //Assert
        buffer.AsSpan()[0].Should().Be(0);
        buffer.AsSpan()[^1].Should().Be(0);
    }

    [Fact]
    public void GetRow_throws_for_a_row_outside_the_surface()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(8, 4);

        //Act
        Action act = () => buffer.GetRow(4);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Rent_throws_for_a_dimension_of_zero()
    {
        //Arrange
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();

        //Act
        Action act = () => pool.Rent(0, 16);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Dispose_frees_every_pooled_surface()
    {
        //Arrange
        BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(32, 32);
        pool.Return(buffer);

        //Act
        pool.Dispose();

        //Assert
        buffer.IsFreed.Should().BeTrue();
        pool.Pooled.Should().Be(0);
    }

    [Fact]
    public void Return_after_Dispose_frees_the_surface_instead_of_keeping_it()
    {
        //Arrange
        BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(32, 32);
        pool.Dispose();

        //Act
        pool.Return(buffer);

        //Assert
        buffer.IsFreed.Should().BeTrue();
    }

    [Fact]
    public void Rent_after_Dispose_throws()
    {
        //Arrange
        BgraFrameBufferPool pool = new BgraFrameBufferPool();
        pool.Dispose();

        //Act
        Action act = () => pool.Rent(16, 16);

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void AsSpan_throws_once_the_surface_has_been_freed()
    {
        //Arrange
        BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer buffer = pool.Rent(16, 16);
        pool.Return(buffer);
        pool.Dispose();

        //Act
        Action act = () => buffer.AsSpan();

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }
}
