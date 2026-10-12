using System;
using System.Collections.Generic;
using CodeBrix.Audio.Engine.Enums;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Engine.Structs;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoPlayback.Containers;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// The export's own packet-codec lookup - the one that lets it decode its source's sound without starting the
/// shared audio output and so without an audio device - follows the audio engine's rule: the factories that claim
/// the codec, matched case-insensitively, highest priority first, equals in the order given, and a null decoder
/// letting the next factory try.
/// </summary>
public class ExportAudioLookupTests
{
    [Fact]
    public void The_highest_priority_factory_that_claims_the_codec_wins()
    {
        //Arrange
        FakeFactory low = new FakeFactory("low", 0, "vorbis");
        FakeFactory high = new FakeFactory("high", 10, "vorbis");
        FakeFactory other = new FakeFactory("other", 100, "opus");

        //Act
        IPacketSoundDecoder decoder = ExportDecoding.CreatePacketDecoder(
            new IPacketCodecFactory[] { low, other, high }, "vorbis", ReadOnlyMemory<byte>.Empty);

        //Assert
        ((FakeDecoder)decoder).Maker.Should().Be("high");
        other.Asked.Should().Be(0);
        low.Asked.Should().Be(0);
    }

    [Fact]
    public void A_factory_that_declines_lets_the_next_one_try()
    {
        //Arrange
        FakeFactory declining = new FakeFactory("declining", 10, "flac") { Declines = true };
        FakeFactory willing = new FakeFactory("willing", 0, "flac");

        //Act
        IPacketSoundDecoder decoder = ExportDecoding.CreatePacketDecoder(
            new IPacketCodecFactory[] { willing, declining }, "flac", ReadOnlyMemory<byte>.Empty);

        //Assert
        ((FakeDecoder)decoder).Maker.Should().Be("willing");
        declining.Asked.Should().Be(1);
    }

    [Fact]
    public void Equal_priorities_are_asked_in_the_order_given()
    {
        //Arrange
        FakeFactory first = new FakeFactory("first", 5, "opus");
        FakeFactory second = new FakeFactory("second", 5, "opus");

        //Act
        IPacketSoundDecoder decoder = ExportDecoding.CreatePacketDecoder(
            new IPacketCodecFactory[] { first, second }, "opus", ReadOnlyMemory<byte>.Empty);

        //Assert
        ((FakeDecoder)decoder).Maker.Should().Be("first");
        second.Asked.Should().Be(0);
    }

    [Fact]
    public void Codec_ids_match_whatever_their_case()
    {
        //Arrange
        FakeFactory factory = new FakeFactory("upper", 0, "VORBIS");

        //Act
        IPacketSoundDecoder decoder = ExportDecoding.CreatePacketDecoder(
            new IPacketCodecFactory[] { factory }, "Vorbis", ReadOnlyMemory<byte>.Empty);

        //Assert
        decoder.Should().NotBeNull();
        factory.LastCodecId.Should().Be("Vorbis");
    }

    [Fact]
    public void Nothing_claiming_the_codec_or_everything_declining_gives_no_decoder()
    {
        //Arrange
        FakeFactory unrelated = new FakeFactory("unrelated", 0, "vorbis");
        FakeFactory declining = new FakeFactory("declining", 0, "aac") { Declines = true };
        IPacketCodecFactory[] factories = { unrelated, declining };

        //Act + Assert
        ExportDecoding.CreatePacketDecoder(factories, "aac", ReadOnlyMemory<byte>.Empty).Should().BeNull();
        ExportDecoding.CreatePacketDecoder(factories, "mp3", ReadOnlyMemory<byte>.Empty).Should().BeNull();
        ExportDecoding.CreatePacketDecoder(factories, null, ReadOnlyMemory<byte>.Empty).Should().BeNull();
        ExportDecoding.CreatePacketDecoder(null, "aac", ReadOnlyMemory<byte>.Empty).Should().BeNull();
        unrelated.Asked.Should().Be(0);
    }

    [Fact]
    public void The_candidates_start_with_the_built_in_vorbis_and_flac_factories()
    {
        //Act
        IReadOnlyList<IPacketCodecFactory> candidates = ExportDecoding.PacketCodecCandidates();

        //Assert
        candidates.Count.Should().BeGreaterThanOrEqualTo(2);
        candidates[0].FactoryId.Should().Be("CodeBrix.Audio.ManagedVorbis.Packets");
        candidates[1].FactoryId.Should().Be("CodeBrix.Audio.ManagedFlac.Packets");
    }

    [Fact]
    public void Opus_is_not_served_unless_its_package_is_registered()
    {
        //Arrange
        CodeBrixAudioOpus.IsRegistered.Should().BeFalse();
        MediaTrackInfo track = new MediaTrackInfo { Kind = MediaTrackKind.Audio, CodecId = "opus" };

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => ExportDecoding.CreateAudioDecoder(track));

        //Assert
        thrown.Message.Should().Contain("CodeBrixAudioOpus.Register()");
    }

    private sealed class FakeFactory : IPacketCodecFactory
    {
        public FakeFactory(string id, int priority, string codecId)
        {
            FactoryId = id;
            Priority = priority;
            SupportedCodecIds = new[] { codecId };
        }

        public string FactoryId { get; }

        public IReadOnlyCollection<string> SupportedCodecIds { get; }

        public int Priority { get; }

        public bool Declines { get; set; }

        public int Asked { get; private set; }

        public string LastCodecId { get; private set; }

        public IPacketSoundDecoder CreateDecoder(string codecId, ReadOnlyMemory<byte> codecPrivate, AudioFormat? hint)
        {
            Asked++;
            LastCodecId = codecId;
            return Declines ? null : new FakeDecoder(FactoryId);
        }
    }

    private sealed class FakeDecoder : IPacketSoundDecoder
    {
        public FakeDecoder(string maker) => Maker = maker;

        public string Maker { get; }

        public int Channels => 1;

        public int SampleRate => 48000;

        public SampleFormat SampleFormat => SampleFormat.F32;

        public int MaxSamplesPerPacket => 960;

        public int PreSkipSamples => 0;

        public int DecodePacket(ReadOnlySpan<byte> packet, Span<float> output) => 0;

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
