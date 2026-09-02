using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CodeBrix.Audio.Playback;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Audio;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Internal;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the whole trailing-trim path with no audio device in sight: what each container states, how the
/// session converts it, and how it reaches the audio player - through the track-level trim for a container
/// that states it once, and on the packets themselves for a container that states it per block.
/// </summary>
/// <remarks>
/// The trimming itself - frames held back and then discarded - is the audio package's own behaviour and is
/// pinned by its tests. What is checked here is that this library states the right number, in the right unit,
/// at the right moment. The audible half is in <see cref="VideoPlaybackSessionAudioTests" />.
/// </remarks>
public class AudioTrailingTrimTests
{
    [Fact]
    public void A_bespoke_file_states_its_trailing_trim_once_in_the_track_header()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("trim-header", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 24,
            frameRate: 25,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 4410);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo audio = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        audio.TrailingTrimSamples.Should().Be(4410);
        audio.SampleRate.Should().Be(48000);
    }

    [Fact]
    public void A_bespoke_files_trim_is_carried_to_the_player_in_frames_at_the_decoders_own_rate()
    {
        //Arrange - the trim is stated in samples per channel at the TRACK's rate; the player counts frames
        // per channel at the DECODER's rate. Equal rates pass the number straight through.
        MediaTrackInfo track = new MediaTrackInfo
        {
            Kind = MediaTrackKind.Audio,
            CodecId = VideoCodecIds.Vorbis,
            SampleRate = 48000,
            TrailingTrimSamples = 4410,
        };

        //Act
        int same = VideoPlaybackSession.ResolveTrailingTrimFrames(track, 48000);
        int halved = VideoPlaybackSession.ResolveTrailingTrimFrames(track, 24000);
        int unstated = VideoPlaybackSession.ResolveTrailingTrimFrames(track, 0);

        //Assert
        same.Should().Be(4410);
        halved.Should().Be(2205);
        unstated.Should().Be(4410);
    }

    [Fact]
    public void A_track_with_no_trim_asks_the_player_for_nothing()
    {
        //Arrange
        MediaTrackInfo track = new MediaTrackInfo
        {
            Kind = MediaTrackKind.Audio,
            CodecId = VideoCodecIds.Vorbis,
            SampleRate = 48000,
        };

        //Act
        int frames = VideoPlaybackSession.ResolveTrailingTrimFrames(track, 48000);

        //Assert
        frames.Should().Be(0);
        VideoPlaybackSession.ResolveTrailingTrimFrames(null, 48000).Should().Be(0);
    }

    [Fact]
    public void A_matroska_file_states_its_trim_as_discard_padding_on_the_last_block_of_the_track()
    {
        //Arrange
        string path = TestAssets.Path("raw-opus.mkv");

        //Act
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(path), true);
        MediaTrackInfo audio = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        List<MediaPacket> audioPackets = new List<MediaPacket>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == audio.Id) audioPackets.Add(packet);
        }

        //Assert - nothing in the track header says where the sound stops, and exactly one block carries a
        // padding: the last one. That is the value the session arms as the track-level trim.
        audio.TrailingTrimSamples.Should().Be(0);
        audioPackets.Count(p => p.DiscardPadding > TimeSpan.Zero).Should().Be(1);
        audioPackets[audioPackets.Count - 1].DiscardPadding.Should().Be(TimeSpan.FromTicks(135_000));
    }

    [Fact]
    public void The_audio_packet_source_hands_the_containers_per_packet_padding_to_the_player()
    {
        //Arrange
        PacketRing ring = new PacketRing(4);
        SessionAudioPacketSource source = new SessionAudioPacketSource(ring);

        ring.TryEnqueue(
            new byte[] { 1, 2, 3 },
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            true,
            TimeSpan.Zero,
            1);

        ring.TryEnqueue(
            new byte[] { 4, 5, 6 },
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(20),
            true,
            TimeSpan.FromTicks(135_000),
            1);

        //Act
        source.TryReadPacket(out AudioPacket first);
        TimeSpan firstPadding = first.DiscardPadding;
        source.TryReadPacket(out AudioPacket second);

        //Assert
        firstPadding.Should().Be(TimeSpan.Zero);
        second.DiscardPadding.Should().Be(TimeSpan.FromTicks(135_000));
        second.Timestamp.HasValue.Should().BeTrue();
        second.Timestamp.Value.Should().Be(TimeSpan.FromMilliseconds(40));
        second.Data.ToArray().Should().Equal(new byte[] { 4, 5, 6 });
    }

    [Fact]
    public void The_audio_packet_source_never_reports_a_loss()
    {
        //Arrange - a file source cannot lose a packet, so nothing here may ever look like one to the player.
        // An EMPTY packet is the one shape that comes close, and it must still not be a loss: it is the audio
        // package's lengthless "one packet went missing" convention, which the decoder answers for itself.
        PacketRing ring = new PacketRing(4);
        SessionAudioPacketSource source = new SessionAudioPacketSource(ring);

        ring.TryEnqueue(new byte[] { 9 }, TimeSpan.Zero, TimeSpan.FromMilliseconds(20), true, TimeSpan.Zero, 1);
        ring.TryEnqueue(
            ReadOnlySpan<byte>.Empty,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            true,
            TimeSpan.Zero,
            1);

        //Act
        source.TryReadPacket(out AudioPacket ordinary);
        bool ordinaryIsLoss = ordinary.IsLoss;
        source.TryReadPacket(out AudioPacket empty);

        //Assert
        ordinaryIsLoss.Should().BeFalse();
        empty.IsLoss.Should().BeFalse();
        empty.LossFrames.Should().Be(0);
        empty.LossDuration.Should().Be(TimeSpan.Zero);
        empty.Data.Length.Should().Be(0);
    }

    [Fact]
    public void A_loss_packet_from_a_future_source_would_travel_the_queue_intact()
    {
        //Arrange - the seam, proved rather than assumed. Nothing in this library emits one today, so what is
        // checked is that the type the session's audio path is built on carries the shape without altering it
        // and without needing a byte of payload.
        AudioPacket byDuration = AudioPacket.Loss(TimeSpan.FromMilliseconds(60));
        AudioPacket byFrames = AudioPacket.Loss(2880, TimeSpan.FromSeconds(1));

        //Act
        PacketRing ring = new PacketRing(4);
        bool queued = ring.TryEnqueue(
            byDuration.Data.Span,
            TimeSpan.Zero,
            byDuration.LossDuration,
            false,
            TimeSpan.Zero,
            1);

        //Assert
        byDuration.IsLoss.Should().BeTrue();
        byDuration.LossDuration.Should().Be(TimeSpan.FromMilliseconds(60));
        byFrames.IsLoss.Should().BeTrue();
        byFrames.LossFrames.Should().Be(2880);
        byFrames.Timestamp.HasValue.Should().BeTrue();
        byFrames.Timestamp.Value.Should().Be(TimeSpan.FromSeconds(1));

        // A zero-length payload goes through the ring unchanged, which is what a loss packet would need.
        queued.Should().BeTrue();
        ring.TryBeginRead(out RingPacket read).Should().BeTrue();
        read.Data.Length.Should().Be(0);
        read.Duration.Should().Be(TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public void A_clip_whose_audio_carries_a_trailing_trim_still_plays_to_exactly_its_duration()
    {
        //Arrange - no device: the picture is what is played here, and the trim rides along in the header. The
        // outer bound is the container's stated Duration, which is unchanged by the trim.
        string path = SyntheticMedia.ScratchPath("trim-duration", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 50,
            frameRate: 25,
            keyFrameInterval: 5,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 4410);

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        // The state is published a moment BEFORE the event is raised, so waiting on the state alone can
        // return between the two and read a count that has not been incremented yet.
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(2));
        session.Tracks
            .First(t => t.Kind == MediaTrackKind.Audio)
            .TrailingTrimSamples.Should().Be(4410);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }
}
