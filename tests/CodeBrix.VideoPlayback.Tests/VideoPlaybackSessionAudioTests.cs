using System;
using System.Diagnostics;
using System.Threading;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.RawCodec;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Plays sound. Every test here opens the shared audio device, so they are all opt-in: set
/// <c>CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1</c> to run them.
/// </summary>
/// <remarks>
/// <para>
/// This is the same gate the audio package's own audible tests use, and for the same reason: a build machine
/// with no sound device would otherwise fail on something that is not a defect. What they prove is the part
/// no device-free test can - that a container's audio packets reach the mixer through the packet player and
/// that the position it reports is the clock everything else follows.
/// </para>
/// <para>
/// They run one at a time: the shared output is a process-wide singleton, and two sessions fighting over it
/// tells you nothing. They share their collection with every other class that depends on a process-wide
/// registry, because these tests START the shared output and REGISTER the Opus packet codec for the rest of
/// the process, and the tests that check a refusal need neither to have happened yet.
/// </para>
/// </remarks>
[Collection("Process-wide registries")]
public class VideoPlaybackSessionAudioTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_vorbis_track_plays_through_the_packet_player()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-vorbis", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 12,
            frameRate: 12,
            keyFrameInterval: 4,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        session.AudioTrack.CodecId.Should().Be(VideoCodecIds.Vorbis);
        session.AudioTrack.SampleRate.Should().Be(48000);
        finished.Should().BeTrue();
        ended.Should().Be(1);
    }

    [Fact]
    public void The_position_a_session_reports_is_the_audio_clock()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-clock", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 12,
            frameRate: 12,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Play();
        bool advanced = WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        TimeSpan seen = session.Position;
        WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        advanced.Should().BeTrue();
        (seen < TimeSpan.FromSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void An_opus_track_plays_once_the_opus_package_is_registered()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();

        string path = SyntheticMedia.ScratchPath("audio-opus", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
            AudioLanguage = "en",
        });

        using VideoPlaybackSession session = new VideoPlaybackSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        session.AudioTrack.CodecId.Should().Be(VideoCodecIds.Opus);
        session.AudioTrack.PreSkipSamples.Should().Be(312);
        finished.Should().BeTrue();
        ended.Should().Be(1);
    }

    [Fact]
    public void Seeking_with_audio_re_bases_the_clock_to_where_it_was_asked_to_go()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-seek", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 12,
            frameRate: 12,
            keyFrameInterval: 4,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(100));

        //Act
        session.Seek(TimeSpan.FromMilliseconds(500));
        Thread.Sleep(150);
        TimeSpan afterSeek = session.Position;

        //Assert
        (afterSeek >= TimeSpan.FromMilliseconds(400)).Should().BeTrue();
        (afterSeek < TimeSpan.FromMilliseconds(1200)).Should().BeTrue();
    }

    [Fact]
    public void Muting_does_not_stop_the_clock()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-mute", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 12,
            frameRate: 12,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Volume = 0.5f;
        session.IsMuted = true;

        //Act
        session.Play();
        bool advanced = WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        advanced.Should().BeTrue();
        session.Volume.Should().Be(0.5f);
        session.IsMuted.Should().BeTrue();
    }

    [Fact]
    public void A_clip_whose_sound_runs_out_before_its_picture_still_plays_to_the_end()
    {
        //Arrange - 2.4 seconds of picture over one second of sound, at the DEFAULT queue sizes
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-short-sound", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 60,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();

        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);
        TimeSpan reached = session.Position;

        //Assert - the bespoke container's index says where the audio track stops, so the session learns of it
        // a second and a half before the file runs out, hands the clock to its stopwatch, and plays the
        // picture to its own end.
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(2.4));
        reached.Should().BeGreaterThan(TimeSpan.FromSeconds(2.3));
    }

    [Fact]
    public void A_clip_whose_picture_runs_out_before_its_sound_is_heard_to_the_end()
    {
        //Arrange - half a second of picture under a second of sound, at the DEFAULT queue sizes
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-long-sound", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 12,
            frameRate: 25,
            keyFrameInterval: 4,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession();

        int ended = 0;
        TimeSpan endedAt = TimeSpan.Zero;
        session.PlaybackEnded += (s, e) =>
        {
            // The count goes up LAST, so a test thread that has seen it has necessarily also been
            // handed the position written beside it.
            endedAt = session.Position;
            Interlocked.Increment(ref ended);
        };

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert - the end is the LATER of the two, so the sound is heard out; and the picture's last frame
        // stays on screen rather than being cleared when the video track ends.
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(0.9));
        endedAt.Should().BeGreaterThan(TimeSpan.FromSeconds(0.9));
        // Nothing is drawing in this test, so the picture's last frame is still sitting in the mailbox
        // rather than having been collected - which is the same thing a view would still be showing.
        session.Presenter.HasFrame.Should().BeTrue();
        session.Presenter.GetStatistics().Posted.Should().Be(12L);
    }

    [Fact]
    public void A_matroska_file_with_no_cues_and_a_short_sound_track_still_reaches_its_end()
    {
        //Arrange - THREE seconds of uncompressed picture over one second of Vorbis, no cues anywhere in the
        // file, at the DEFAULT queue sizes. The picture outlasts the sound by fifty packets and the video
        // queue holds thirty-two, so the demultiplexer must park the overflow and keep reading; Matroska
        // cannot say where a track stops, so reaching the end of the file is the only way this one finishes.
        SkipUnlessAudioIsEnabled();
        string path = TestAssets.Path("raw-vorbis-nocues.mkv");

        using VideoPlaybackSession session = NewSession();

        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);
        TimeSpan reached = session.Position;

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(3));
        reached.Should().BeGreaterThan(TimeSpan.FromSeconds(2.9));
        session.Notices.Should().NotContain(notice => notice.Contains("parking budget"));
    }

    [Fact]
    public void A_bespoke_files_trailing_trim_is_applied_by_the_audio_engine_and_the_clip_still_ends_at_its_duration()
    {
        //Arrange - two seconds of picture over a second of sound, with a tenth of a second of the sound's own
        // tail declared as encoder padding in the track header. The audio engine holds those frames back and
        // discards them; the container's Duration is unchanged and is still the outer bound.
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-trailing-trim", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 50,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 4800);

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        TimeSpan trimAtOpen = session.AudioTrailingTrim;
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);
        TimeSpan reached = session.Position;

        //Assert - the trim reaches the player BEFORE anything is heard (the header states it, so the session
        // does not have to wait for the last packet to learn it), and 4800 frames at 48 kHz is 100 ms.
        trimAtOpen.Should().Be(TimeSpan.FromMilliseconds(100));
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(2));
        reached.Should().BeGreaterThan(TimeSpan.FromSeconds(1.9));
    }

    [Fact]
    public void The_trimmed_frames_are_never_handed_to_the_device()
    {
        //Arrange - the same clip twice: once with a tenth of a second declared as encoder padding, once with
        // none. What is measured is the audio player's own clock after the sound has finished, which counts
        // only what actually reached the mixer.
        SkipUnlessAudioIsEnabled();
        string untrimmedPath = SyntheticMedia.ScratchPath("audio-untrimmed", "clip.cbv");
        string trimmedPath = SyntheticMedia.ScratchPath("audio-trimmed", "clip.cbv");

        SyntheticMedia.WriteRawCbv(
            untrimmedPath,
            frameCount: 50,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 0);

        SyntheticMedia.WriteRawCbv(
            trimmedPath,
            frameCount: 50,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 4800);

        //Act
        TimeSpan untrimmed = PlayAndMeasureDeliveredAudio(untrimmedPath);
        TimeSpan trimmed = PlayAndMeasureDeliveredAudio(trimmedPath);

        //Assert - 4800 frames at 48 kHz is exactly 100 ms, and Vorbis has no pre-skip to complicate it. The
        // millisecond of slack is the audio player's own rounding to a whole frame at the device rate.
        TimeSpan difference = untrimmed - trimmed;
        (difference >= TimeSpan.FromMilliseconds(99)).Should().BeTrue();
        (difference <= TimeSpan.FromMilliseconds(101)).Should().BeTrue();
    }

    [Fact]
    public void A_trim_longer_than_the_sound_leaves_nothing_to_hear_and_still_reaches_the_end()
    {
        //Arrange - the degenerate case, which is the one that would hang if the trim were applied by stopping
        // the clock rather than by holding audio back: the whole sound track is padding.
        SkipUnlessAudioIsEnabled();
        string path = SyntheticMedia.ScratchPath("audio-trim-everything", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 50,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"),
            audioTrailingTrimSamples: 480000);

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        session.AudioTrailingTrim.Should().Be(TimeSpan.FromSeconds(10));
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_matroska_files_discard_padding_becomes_the_tracks_trailing_trim()
    {
        //Arrange - Matroska states the trim on the LAST block rather than in the track header, so the session
        // cannot know it in advance; it arms it the moment the reader proves that block was the last.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();

        string path = TestAssets.Path("raw-opus.mkv");

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);
        TimeSpan trimAtEnd = session.AudioTrailingTrim;

        //Assert - 13.5 ms is what the authoring tool wrote on the last Opus block of this file. The track
        // header states nothing, which is pinned device-free by
        // AudioTrailingTrimTests.A_matroska_file_states_its_trim_as_discard_padding_on_the_last_block_of_the_track;
        // there is deliberately no assertion here about the trim at Open, because this file is half a second
        // long and the demultiplexing thread can reach its end before Open has even returned.
        trimAtEnd.Should().Be(TimeSpan.FromTicks(135_000));
        finished.Should().BeTrue();
        ended.Should().Be(1);
    }

    /// <summary>
    /// Plays a clip to its end and reports how much audio the packet player actually handed to the device.
    /// </summary>
    /// <param name="path">The clip to play.</param>
    /// <returns>The audio player's own position once the sound has finished.</returns>
    private static TimeSpan PlayAndMeasureDeliveredAudio(string path)
    {
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.State == VideoPlaybackState.Ended);
        return session.AudioPlayerPosition;
    }

    private static VideoPlaybackSession NewSession()
    {
        VideoPlaybackSession session = new VideoPlaybackSession();
        session.RegisterDecoderFactory(new RawVideoDecoderFactory());
        return session;
    }

    private static void SkipUnlessAudioIsEnabled() =>
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS"),
                "1",
                StringComparison.Ordinal),
            "Set CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1 to run the tests that open the audio device and make a noise.");

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout = default)
    {
        TimeSpan limit = timeout == default ? Timeout : timeout;
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < limit)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }
}
