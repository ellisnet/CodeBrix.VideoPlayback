using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the non-starting audio codec probe: it answers, it answers about the right registry, and asking it
/// never opens the audio device.
/// </summary>
/// <remarks>
/// This is what makes a headless run honest. Before the audio package grew
/// <c>SharedAudioOutput.IsPacketCodecSupported</c>, the only way to find out whether a codec had a decoder was
/// to ask for the decoder - which starts the shared output and opens a device. Every test in this class runs
/// with no device at all, and several of them prove exactly that by watching
/// <c>SharedAudioOutput.IsRunning</c> across the call.
/// </remarks>
[Collection("Process-wide registries")]
public class AudioDecoderProbeTests
{
    [Fact]
    public void Vorbis_is_always_available_because_the_audio_package_builds_it_in()
    {
        //Arrange
        bool runningBefore = SharedAudioOutput.IsRunning;

        //Act
        bool supported = AudioDecoders.IsCodecSupported(VideoCodecIds.Vorbis);

        //Assert
        supported.Should().BeTrue();
        SharedAudioOutput.IsRunning.Should().Be(runningBefore);
    }

    [Fact]
    public void Asking_about_a_codec_nothing_serves_answers_no_without_starting_anything()
    {
        //Arrange
        Assert.SkipWhen(
            SharedAudioOutput.IsRunning,
            "The shared audio output is already running in this process, so this test cannot show that asking "
            + "did not start it. It is started only by the opt-in audible tests.");

        //Act
        bool supported = AudioDecoders.IsCodecSupported("a-codec-that-does-not-exist");

        //Assert
        supported.Should().BeFalse();
        SharedAudioOutput.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void The_probe_is_case_insensitive_and_refuses_nothing_at_all()
    {
        //Act & Assert
        AudioDecoders.IsCodecSupported("VORBIS").Should().BeTrue();
        AudioDecoders.IsCodecSupported("VoRbIs").Should().BeTrue();
        AudioDecoders.IsCodecSupported(null).Should().BeFalse();
        AudioDecoders.IsCodecSupported(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void Every_codec_the_probe_lists_probes_true()
    {
        //Act
        IReadOnlyCollection<string> ids = AudioDecoders.SupportedCodecIds;

        //Assert
        ids.Count.Should().BeGreaterThan(0);
        ids.Should().Contain(id => string.Equals(id, VideoCodecIds.Vorbis, StringComparison.OrdinalIgnoreCase));
        foreach (string id in ids) AudioDecoders.IsCodecSupported(id).Should().BeTrue();
    }

    [Fact]
    public void An_opus_file_is_refused_without_opening_the_audio_device()
    {
        //Arrange - the missing-decoder refusal used to need a device to produce, because asking for a decoder
        // was the only way to ask the question. It does not any more, and this test is device-free.
        Assert.SkipWhen(
            OpusPacketDecoderIsRegistered(),
            "The Opus packet codec is already registered in this process, so the missing-decoder path cannot be "
            + "reached. It is registered only by the opt-in audible tests.");

        Assert.SkipWhen(
            SharedAudioOutput.IsRunning,
            "The shared audio output is already running in this process, so this test cannot show that the "
            + "refusal did not start it.");

        string path = SyntheticMedia.ScratchPath("probe-missing-opus", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        });

        using VideoPlaybackSession session = new VideoPlaybackSession();

        //Act
        Action act = () => session.Open(path);

        //Assert - the exact contractual message, and no device was opened to produce it.
        act.Should().Throw<VideoPlaybackException>().WithMessage(
            "audio codec 'opus' has no registered decoder — reference CodeBrix.Audio.Opus and call "
            + "CodeBrixAudioOpus.Register()");

        SharedAudioOutput.IsRunning.Should().BeFalse();
        session.State.Should().Be(VideoPlaybackState.Failed);
    }

    [Fact]
    public void The_same_refusal_reaches_the_MediaFailed_event_with_no_device()
    {
        //Arrange
        Assert.SkipWhen(
            OpusPacketDecoderIsRegistered(),
            "The Opus packet codec is already registered in this process, so the missing-decoder path cannot be "
            + "reached. It is registered only by the opt-in audible tests.");

        Assert.SkipWhen(
            SharedAudioOutput.IsRunning,
            "The shared audio output is already running in this process, so this test cannot show that the "
            + "refusal did not start it.");

        string path = SyntheticMedia.ScratchPath("probe-missing-opus-event", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        });

        using VideoPlaybackSession session = new VideoPlaybackSession();
        string reported = null;
        session.MediaFailed += (s, e) => reported = e.Message;

        //Act
        try
        {
            session.Open(path);
        }
        catch (VideoPlaybackException)
        {
            // The caller sees it too; this test is about the event.
        }

        //Assert
        (reported == null).Should().BeFalse();
        reported.Should().Be(
            "audio codec 'opus' has no registered decoder — reference CodeBrix.Audio.Opus and call "
            + "CodeBrixAudioOpus.Register()");

        SharedAudioOutput.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void A_session_that_does_not_play_audio_never_asks_the_device_for_anything()
    {
        //Arrange - the headless case: the file has sound and no decoder for it, and the session opens anyway
        // because it was told not to play any. Nothing touches the audio device, and the track is still there
        // to be asked about.
        Assert.SkipWhen(
            SharedAudioOutput.IsRunning,
            "The shared audio output is already running in this process, so this test cannot show that opening "
            + "did not start it.");

        string path = SyntheticMedia.ScratchPath("probe-no-audio", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 8,
            frameRate: 25,
            audioOggPath: TestAssets.Path("opus-audio.ogg"));

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        session.RegisterDecoderFactory(new CodeBrix.VideoPlayback.RawCodec.RawVideoDecoderFactory());

        //Act
        session.Open(path);
        MediaTrackInfo audio = session.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        session.AudioTrack.Should().BeNull();
        audio.CodecId.Should().Be(VideoCodecIds.Opus);
        SharedAudioOutput.IsRunning.Should().BeFalse();

        // And the caller can still find out whether that track could have been played, without a device.
        AudioDecoders.IsCodecSupported(audio.CodecId).Should().Be(OpusPacketDecoderIsRegistered());
        SharedAudioOutput.IsRunning.Should().BeFalse();
    }

    private static bool OpusPacketDecoderIsRegistered()
    {
        foreach (IPacketCodecFactory factory in SharedAudioOutput.RegisteredPacketCodecFactories)
        {
            foreach (string id in factory.SupportedCodecIds)
            {
                if (string.Equals(id, VideoCodecIds.Opus, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
