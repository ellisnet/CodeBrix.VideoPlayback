using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The built-in uncompressed codec, proved from the outside: nothing is registered anywhere in this class,
/// and an uncompressed file still opens, decodes and plays to its end.
/// </summary>
/// <remarks>
/// <para>
/// This is the promise the package makes to an application that has installed no codec at all - the same one
/// Vorbis audio makes. Every other test that plays an uncompressed clip now leans on it too, so if the
/// built-in registration were ever lost this class would say so first and in one sentence.
/// </para>
/// <para>
/// It shares the process-wide registry collection because it READS that registry, and
/// <see cref="VideoDecodersTests" /> is writing to it: the two must not run at the same time.
/// </para>
/// </remarks>
[Collection("Process-wide registries")]
public class RawVideoDecoderFactoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void The_uncompressed_codec_is_supported_before_anything_is_registered()
    {
        //Arrange & Act
        bool supported = VideoDecoders.IsCodecSupported(VideoCodecIds.Raw);
        IReadOnlyList<IVideoDecoderFactory> factories = VideoDecoders.RegisteredFactories;

        //Assert
        supported.Should().BeTrue();
        VideoDecoders.SupportedCodecIds.Should().Contain(VideoCodecIds.Raw);
        Contains<RawVideoDecoderFactory>(factories).Should().BeTrue();
    }

    [Fact]
    public void The_registry_builds_an_uncompressed_decoder_with_no_registration_call()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        //Act
        using IVideoDecoder decoder = VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(SyntheticMedia.Video),
            options);

        //Assert
        (decoder is RawVideoDecoder).Should().BeTrue();
        decoder.CodecId.Should().Be(VideoCodecIds.Raw);
        decoder.Info.Width.Should().Be(64);
        decoder.Info.Height.Should().Be(36);
    }

    [Fact]
    public void An_uncompressed_clip_plays_to_its_end_with_no_registration_call()
    {
        //Arrange
        VideoDecoders.IsCodecSupported(VideoCodecIds.Raw).Should().BeTrue();

        string path = SyntheticMedia.ScratchPath("built-in-raw", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 40, frameRate: 25, keyFrameInterval: 10);

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.VideoTrack.CodecId.Should().Be(VideoCodecIds.Raw);
        session.VideoStreamInfo.Width.Should().Be(64);
        session.Duration.Should().Be(TimeSpan.FromTicks(TimeSpan.FromSeconds(1.0 / 25).Ticks * 40));
        session.Presenter.GetStatistics().Posted.Should().BeGreaterThan(30L);
    }

    [Fact]
    public void A_factory_registered_on_the_session_is_asked_before_the_built_in_one()
    {
        //Arrange - the explicit registration path, which is what an external codec package uses. A session
        // factory is tried first even at the same priority, so this one decorates the built-in decoder rather
        // than being shadowed by it.
        string path = SyntheticMedia.ScratchPath("built-in-raw-override", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 10, frameRate: 25);

        CountingRawFactory counting = new CountingRawFactory();

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        session.RegisterDecoderFactory(counting);

        //Act
        session.Open(path);
        bool arrived = WaitFor(() => session.Presenter.HasFrame);

        //Assert
        arrived.Should().BeTrue();
        counting.Asked.Should().Be(1);
    }

    private static bool Contains<T>(IReadOnlyList<IVideoDecoderFactory> factories)
    {
        foreach (IVideoDecoderFactory factory in factories)
        {
            if (factory is T) return true;
        }

        return false;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < Timeout)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>Serves the uncompressed codec by asking the built-in factory, and counts the requests.</summary>
    private sealed class CountingRawFactory : IVideoDecoderFactory
    {
        private readonly RawVideoDecoderFactory inner = new RawVideoDecoderFactory();

        public string FactoryId => "CodeBrix.VideoPlayback.Tests.CountingRawVideo";

        public IReadOnlyCollection<string> SupportedCodecIds { get; } = new[] { VideoCodecIds.Raw };

        public int Priority => 0;

        internal int Asked { get; private set; }

        public IVideoDecoder CreateDecoder(
            string codecId,
            ReadOnlyMemory<byte> codecPrivate,
            VideoDecoderOptions options)
        {
            Asked++;
            return inner.CreateDecoder(codecId, codecPrivate, options);
        }
    }
}
