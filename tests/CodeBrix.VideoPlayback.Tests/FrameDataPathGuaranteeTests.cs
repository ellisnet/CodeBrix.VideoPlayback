using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.RawCodec;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The four promises the frame data path makes to whatever draws, each proved on its own so that a change
/// which quietly breaks one is a named failure rather than a slow application.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><description>
///     A decoder writes its output STRAIGHT into the host's pool, so there is no copy between decoding and
///     the graphics upload.
///   </description></item>
///   <item><description>Nothing allocates per frame once playback is warm.</description></item>
///   <item><description>
///     A frame is immutable once published, so any number of threads may read one at the same time.
///   </description></item>
///   <item><description>
///     A buffer goes back to the pool only when the last reference has dropped AND the presenter's fence says
///     it has finished reading the memory.
///   </description></item>
/// </list>
/// </remarks>
public class FrameDataPathGuaranteeTests
{
    [Fact]
    public void Guarantee_1_the_decoder_writes_into_the_pool_the_session_owns()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("guarantee-1", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 20, frameRate: 25);

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        session.RegisterDecoderFactory(new RawVideoDecoderFactory());
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);

        //Act
        session.Presenter.TryTakeLatest(out VideoFrame frame);
        bool fromThePool;
        using (frame)
        {
            fromThePool = frame.Buffer is PinnedVideoFrameBuffer;
        }

        VideoFrameBufferPoolStatistics statistics = ((PinnedFrameBufferPool)session.BufferPool).GetStatistics();

        //Assert
        fromThePool.Should().BeTrue();
        statistics.Rents.Should().BeGreaterThan(0);
        statistics.Allocations.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Guarantee_1_a_decoder_that_supports_external_buffers_says_so()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        //Act
        using IVideoDecoder decoder = new RawVideoDecoderFactory().CreateDecoder(
            VideoCodecIds.Raw,
            Codecs.RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            options);

        //Assert
        decoder.SupportsExternalBuffers.Should().BeTrue();
    }

    [Fact]
    public void Guarantee_2_a_whole_clip_plays_without_the_pool_allocating_again()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("guarantee-2", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 120, frameRate: 60);

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        session.RegisterDecoderFactory(new RawVideoDecoderFactory());
        session.Open(path);
        session.Play();
        WaitFor(() => session.Presenter.GetStatistics().Posted > 20);

        PinnedFrameBufferPool pool = (PinnedFrameBufferPool)session.BufferPool;
        long allocationsWhenWarm = pool.GetStatistics().Allocations;

        //Act
        WaitFor(() => session.State == VideoPlaybackState.Ended, TimeSpan.FromSeconds(20));
        VideoFrameBufferPoolStatistics atEnd = pool.GetStatistics();

        //Assert
        atEnd.Allocations.Should().Be(allocationsWhenWarm);
        atEnd.Rents.Should().BeGreaterThan(atEnd.Allocations * 4);
    }

    [Fact]
    public void Guarantee_2_the_whole_hand_off_allocates_nothing_per_frame()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using Presentation.VideoFramePresenter presenter = new Presentation.VideoFramePresenter();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        using IVideoDecoder decoder = new RawVideoDecoderFactory().CreateDecoder(
            VideoCodecIds.Raw,
            Codecs.RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            options);

        byte[] packet = SyntheticMedia.MakeFrame(0);

        for (int i = 0; i < 8; i++) PumpOnce(decoder, presenter, packet, i);

        long before = GC.GetAllocatedBytesForCurrentThread();

        //Act
        for (int i = 0; i < 300; i++) PumpOnce(decoder, presenter, packet, i);

        long after = GC.GetAllocatedBytesForCurrentThread();

        //Assert
        (after - before).Should().Be(0);
    }

    [Fact]
    public async Task Guarantee_3_a_published_frame_reads_the_same_from_every_thread()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        using IVideoDecoder decoder = new RawVideoDecoderFactory().CreateDecoder(
            VideoCodecIds.Raw,
            Codecs.RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            options);

        decoder.SendPacket(new VideoPacket(SyntheticMedia.MakeFrame(42), TimeSpan.Zero, true));
        decoder.TryReceiveFrame(out VideoFrame frame);

        byte expected = frame.Y.GetRowBytes(0)[0];
        bool mismatch = false;

        //Act
        Task[] readers = new Task[8];
        for (int i = 0; i < readers.Length; i++)
        {
            VideoFrame mine = frame.Retain();
            readers[i] = Task.Run(
                () =>
                {
                    try
                    {
                        for (int n = 0; n < 2000; n++)
                        {
                            for (int row = 0; row < mine.Y.Height; row++)
                            {
                                if (mine.Y.GetRowBytes(row)[0] != expected) mismatch = true;
                            }
                        }
                    }
                    finally
                    {
                        mine.Dispose();
                    }
                },
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(readers);
        frame.Dispose();

        //Assert
        expected.Should().Be((byte)(16 + 42));
        mismatch.Should().BeFalse();
        pool.GetStatistics().Pooled.Should().Be(1);
    }

    [Fact]
    public void Guarantee_6_a_buffer_waits_for_the_presenters_fence_even_after_the_last_reference_drops()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        using IVideoDecoder decoder = new RawVideoDecoderFactory().CreateDecoder(
            VideoCodecIds.Raw,
            Codecs.RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            options);

        decoder.SendPacket(new VideoPacket(SyntheticMedia.MakeFrame(1), TimeSpan.Zero, true));
        decoder.TryReceiveFrame(out VideoFrame frame);

        UploadFence fence = new UploadFence();
        frame.Buffer.Tag = fence;

        //Act
        frame.Dispose();
        VideoFrameBufferPoolStatistics whileUploading = pool.GetStatistics();

        VideoFrameBuffer rentedMeanwhile = pool.Rent(
            new VideoFrameBufferDescriptor(64, 36, VideoPixelLayout.I420, 8));

        fence.IsSignaled = true;
        pool.PumpFences();
        VideoFrameBufferPoolStatistics afterSignal = pool.GetStatistics();

        //Assert
        whileUploading.WaitingOnFences.Should().Be(1);
        whileUploading.Pooled.Should().Be(0);
        afterSignal.WaitingOnFences.Should().Be(0);
        afterSignal.Pooled.Should().Be(1);
        afterSignal.Allocations.Should().Be(2);
        (rentedMeanwhile != null).Should().BeTrue();
    }

    [Fact]
    public void Guarantee_6_the_fence_is_polled_on_the_next_pool_operation_as_well()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(64, 36, VideoPixelLayout.I420, 8);
        VideoFrameBuffer buffer = pool.Rent(descriptor);
        UploadFence fence = new UploadFence();
        buffer.Tag = fence;
        pool.Return(buffer);

        //Act
        fence.IsSignaled = true;
        VideoFrameBuffer next = pool.Rent(descriptor);

        //Assert
        ReferenceEquals(next, buffer).Should().BeTrue();
        pool.GetStatistics().Allocations.Should().Be(1);
    }

    private static void PumpOnce(
        IVideoDecoder decoder,
        Presentation.VideoFramePresenter presenter,
        byte[] packet,
        int number)
    {
        decoder.SendPacket(new VideoPacket(packet, TimeSpan.FromTicks(number), true));
        decoder.TryReceiveFrame(out VideoFrame frame);
        presenter.Post(frame);
        frame.Dispose();
        presenter.TryTakeLatest(out VideoFrame taken);
        taken.Dispose();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout = default)
    {
        TimeSpan limit = timeout == default ? TimeSpan.FromSeconds(20) : timeout;
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < limit)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>Stands in for the fence a presenter parks in a buffer's tag while a graphics upload runs.</summary>
    private sealed class UploadFence : IVideoFrameFence
    {
        public bool IsSignaled { get; set; }
    }
}
