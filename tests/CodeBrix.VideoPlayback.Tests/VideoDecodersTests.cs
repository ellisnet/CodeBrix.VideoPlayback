using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the decoder registry: registration is idempotent on the instance, priority decides who is asked
/// first, a factory that declines lets the next one try, and a factory that throws does not end the search.
/// </summary>
/// <remarks>
/// The registry is process-wide, so every test here clears it first and clears it again afterwards. Nothing
/// else in the suite REGISTERS a process-wide video decoder - sessions get theirs through
/// <see cref="VideoPlaybackSession.RegisterDecoderFactory" /> instead - but
/// <see cref="VideoPlaybackFailureTests" /> READS the registry and needs it empty, so every class that
/// depends on a process-wide registry shares the one collection and none of them run at the same time.
/// </remarks>
[Collection("Process-wide registries")]
public class VideoDecodersTests : IDisposable
{
    public VideoDecodersTests() => VideoDecoders.Clear();

    public void Dispose() => VideoDecoders.Clear();

    [Fact]
    public void Register_ignores_the_same_instance_twice()
    {
        //Arrange
        FakeFactory factory = new FakeFactory("one", 0, VideoCodecIds.Av1);

        //Act
        VideoDecoders.Register(factory);
        VideoDecoders.Register(factory);

        //Assert
        VideoDecoders.RegisteredFactories.Count.Should().Be(1);
    }

    [Fact]
    public void Register_keeps_two_different_instances()
    {
        //Arrange & Act
        VideoDecoders.Register(new FakeFactory("one", 0, VideoCodecIds.Av1));
        VideoDecoders.Register(new FakeFactory("two", 0, VideoCodecIds.Av1));

        //Assert
        VideoDecoders.RegisteredFactories.Count.Should().Be(2);
    }

    [Fact]
    public void The_highest_priority_factory_is_asked_first()
    {
        //Arrange
        FakeFactory low = new FakeFactory("low", -10, VideoCodecIds.Av1);
        FakeFactory high = new FakeFactory("high", 10, VideoCodecIds.Av1);
        VideoDecoders.Register(low);
        VideoDecoders.Register(high);

        //Act
        IVideoDecoder decoder = VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Av1,
            ReadOnlyMemory<byte>.Empty,
            new VideoDecoderOptions());

        //Assert
        decoder.CodecId.Should().Be(VideoCodecIds.Av1);
        high.Asked.Should().Be(1);
        low.Asked.Should().Be(0);
    }

    [Fact]
    public void A_factory_that_declines_lets_the_next_one_try()
    {
        //Arrange
        FakeFactory declines = new FakeFactory("declines", 10, VideoCodecIds.Av1) { Decline = true };
        FakeFactory serves = new FakeFactory("serves", 0, VideoCodecIds.Av1);
        VideoDecoders.Register(declines);
        VideoDecoders.Register(serves);

        //Act
        IVideoDecoder decoder = VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Av1,
            ReadOnlyMemory<byte>.Empty,
            new VideoDecoderOptions());

        //Assert
        (decoder != null).Should().BeTrue();
        declines.Asked.Should().Be(1);
        serves.Asked.Should().Be(1);
    }

    [Fact]
    public void A_factory_that_throws_does_not_end_the_search()
    {
        //Arrange
        FakeFactory broken = new FakeFactory("broken", 10, VideoCodecIds.Av1) { Throw = true };
        FakeFactory serves = new FakeFactory("serves", 0, VideoCodecIds.Av1);
        VideoDecoders.Register(broken);
        VideoDecoders.Register(serves);

        //Act
        IVideoDecoder decoder = VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Av1,
            ReadOnlyMemory<byte>.Empty,
            new VideoDecoderOptions());

        //Assert
        (decoder != null).Should().BeTrue();
    }

    [Fact]
    public void When_only_a_broken_factory_serves_the_codec_its_reason_reaches_the_caller()
    {
        //Arrange
        VideoDecoders.Register(new FakeFactory("broken", 0, VideoCodecIds.Av1) { Throw = true });

        //Act
        Action act = () => VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Av1,
            ReadOnlyMemory<byte>.Empty,
            new VideoDecoderOptions());

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*this factory is broken*");
    }

    [Fact]
    public void An_unregistered_codec_produces_null_rather_than_an_exception()
    {
        //Arrange
        VideoDecoders.Register(new FakeFactory("one", 0, VideoCodecIds.Av1));

        //Act
        IVideoDecoder decoder = VideoDecoders.TryCreateDecoder(
            VideoCodecIds.Vp9,
            ReadOnlyMemory<byte>.Empty,
            new VideoDecoderOptions());

        //Assert
        (decoder == null).Should().BeTrue();
    }

    [Fact]
    public void Codec_identifiers_are_matched_without_regard_to_case()
    {
        //Arrange
        VideoDecoders.Register(new FakeFactory("one", 0, "AV01"));

        //Act
        bool supported = VideoDecoders.IsCodecSupported("av01");

        //Assert
        supported.Should().BeTrue();
    }

    [Fact]
    public void Unregister_removes_the_instance_it_is_given()
    {
        //Arrange
        FakeFactory factory = new FakeFactory("one", 0, VideoCodecIds.Av1);
        VideoDecoders.Register(factory);

        //Act
        bool removed = VideoDecoders.Unregister(factory);
        bool removedAgain = VideoDecoders.Unregister(factory);

        //Assert
        removed.Should().BeTrue();
        removedAgain.Should().BeFalse();
        VideoDecoders.RegisteredFactories.Count.Should().Be(0);
    }

    [Fact]
    public void SupportedCodecIds_gathers_what_every_factory_claims()
    {
        //Arrange
        VideoDecoders.Register(new FakeFactory("one", 0, VideoCodecIds.Av1));
        VideoDecoders.Register(new FakeFactory("two", 0, VideoCodecIds.Vp9));

        //Act
        IReadOnlyCollection<string> ids = VideoDecoders.SupportedCodecIds;

        //Assert
        ids.Count.Should().Be(2);
    }

    [Fact]
    public void Register_refuses_a_null_factory()
    {
        //Arrange
        Action act = () => VideoDecoders.Register(null);

        //Act & Assert
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class FakeFactory : IVideoDecoderFactory
    {
        private readonly string codecId;

        internal FakeFactory(string id, int priority, string codecId)
        {
            FactoryId = id;
            Priority = priority;
            this.codecId = codecId;
            SupportedCodecIds = new[] { codecId };
        }

        public string FactoryId { get; }

        public IReadOnlyCollection<string> SupportedCodecIds { get; }

        public int Priority { get; }

        internal bool Decline { get; set; }

        internal bool Throw { get; set; }

        internal int Asked { get; private set; }

        public IVideoDecoder CreateDecoder(string requestedCodecId, ReadOnlyMemory<byte> codecPrivate, VideoDecoderOptions options)
        {
            Asked++;
            if (Throw) throw new VideoPlaybackException("this factory is broken");
            if (Decline) return null;
            return new FakeDecoder(codecId);
        }
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        internal FakeDecoder(string codecId) => CodecId = codecId;

        public VideoStreamInfo Info => VideoStreamInfo.Unknown;

        public bool SupportsExternalBuffers => false;

        public string CodecId { get; }

        public bool SendPacket(VideoPacket packet) => true;

        public bool TryReceiveFrame(out VideoFrame frame)
        {
            frame = null;
            return false;
        }

        public void Flush()
        {
        }

        public void Drain()
        {
        }

        public void Dispose()
        {
        }
    }
}
