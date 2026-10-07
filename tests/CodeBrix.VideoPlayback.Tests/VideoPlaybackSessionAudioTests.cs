using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;
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

    [Theory]
    [InlineData("opus-audio.ogg")]
    [InlineData("vorbis-audio.ogg")]
    public void A_paused_exact_seek_reports_exactly_the_sought_position_every_time(string audioFile)
    {
        //Arrange - four seconds of picture over four seconds of sound, played for a moment and paused, the
        // way a viewer reaches for the scrub bar. The seeks alternate between one and two seconds, enough of
        // them that a race between the seek and the demultiplexing thread shows.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-paused-seek-" + audioFile, audioFile);

        using VideoPlaybackSession session = NewSession();
        long shownTicks = -1;
        session.FrameReady += (s, e) => Interlocked.Exchange(ref shownTicks, e.Timestamp.Ticks);
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(300));
        session.Pause();
        Thread.Sleep(100);

        List<string> wrong = new List<string>();

        //Act
        for (int i = 0; i < 16; i++)
        {
            TimeSpan target = TimeSpan.FromSeconds(i % 2 == 0 ? 1 : 2);
            Interlocked.Exchange(ref shownTicks, -1);

            session.Seek(target);
            TimeSpan atReturn = session.Position;
            bool shown = WaitFor(() => Interlocked.Read(ref shownTicks) == target.Ticks, TimeSpan.FromSeconds(5));
            Thread.Sleep(50);
            TimeSpan afterFrame = session.Position;

            if (!shown || atReturn != target || afterFrame != target)
            {
                wrong.Add($"seek {i} to {target}: shown={shown}, at return {atReturn}, after the frame {afterFrame}");
            }
        }

        //Assert
        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    [Fact]
    public void Resuming_after_a_paused_seek_never_steps_the_position_backwards()
    {
        //Arrange - the audio player re-bases to the first packet it is given, which is a little BEFORE the
        // sought position; the position must not dip to it when the audio clock takes over.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-seek-resume", "opus-audio.ogg");
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        session.Pause();
        session.Seek(TimeSpan.FromSeconds(1));
        Thread.Sleep(100);

        //Act
        session.Play();
        List<TimeSpan> seen = Watch(session, TimeSpan.FromMilliseconds(600));

        //Assert
        FirstStepBackwards(seen, TimeSpan.FromSeconds(1)).Should().BeEmpty();
        seen[seen.Count - 1].Should().BeGreaterThan(TimeSpan.FromSeconds(1.3));
    }

    [Fact]
    public void A_seek_while_playing_reports_the_sought_position_at_once_and_never_less()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-seek-playing", "opus-audio.ogg");
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        List<string> wrong = new List<string>();

        //Act
        foreach (double seconds in new[] { 2.0, 1.0, 2.5, 0.6 })
        {
            TimeSpan target = TimeSpan.FromSeconds(seconds);
            session.Seek(target);
            TimeSpan atReturn = session.Position;
            List<TimeSpan> seen = Watch(session, TimeSpan.FromMilliseconds(400));

            if (atReturn != target) wrong.Add($"seek to {target}: at return {atReturn}");
            string backwards = FirstStepBackwards(seen, target);
            if (backwards.Length > 0) wrong.Add($"seek to {target}: {backwards}");
            if (seen[seen.Count - 1] < target + TimeSpan.FromMilliseconds(150))
            {
                wrong.Add($"seek to {target}: the clock did not run on, it reads {seen[seen.Count - 1]}");
            }
        }

        //Assert
        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    [Fact]
    public void Back_to_back_paused_seeks_report_the_last_one()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-seek-back-to-back", "vorbis-audio.ogg");
        using VideoPlaybackSession session = NewSession();
        long shownTicks = -1;
        session.FrameReady += (s, e) => Interlocked.Exchange(ref shownTicks, e.Timestamp.Ticks);
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        session.Pause();
        List<string> wrong = new List<string>();

        //Act
        for (int round = 0; round < 6; round++)
        {
            Interlocked.Exchange(ref shownTicks, -1);
            session.Seek(TimeSpan.FromSeconds(3));
            session.Seek(TimeSpan.FromSeconds(0.5));
            session.Seek(TimeSpan.FromSeconds(2));
            TimeSpan atReturn = session.Position;
            bool shown = WaitFor(() => Interlocked.Read(ref shownTicks) == TimeSpan.FromSeconds(2).Ticks);
            Thread.Sleep(50);
            TimeSpan afterFrame = session.Position;

            if (!shown || atReturn != TimeSpan.FromSeconds(2) || afterFrame != TimeSpan.FromSeconds(2))
            {
                wrong.Add($"round {round}: shown={shown}, at return {atReturn}, after the frame {afterFrame}");
            }
        }

        //Assert
        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    [Fact]
    public void A_paused_key_frame_seek_reports_the_key_frame_it_landed_on()
    {
        //Arrange - a key frame every ten frames at 25 a second is one every 0.4 s, so 1.1 s lands on 0.8 s.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-seek-key-frame", "opus-audio.ogg");
        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { SeekMode = VideoSeekMode.KeyFrameOnly });
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        session.Pause();

        //Act
        session.Seek(TimeSpan.FromSeconds(1.1));
        TimeSpan atReturn = session.Position;
        Thread.Sleep(200);
        TimeSpan settled = session.Position;
        session.Play();
        List<TimeSpan> seen = Watch(session, TimeSpan.FromMilliseconds(400));

        //Assert
        atReturn.Should().Be(TimeSpan.FromSeconds(0.8));
        settled.Should().Be(TimeSpan.FromSeconds(0.8));
        FirstStepBackwards(seen, TimeSpan.FromSeconds(0.8)).Should().BeEmpty();
    }

    [Fact]
    public void Stop_reads_zero_and_stays_there_until_played_again()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-stop", "opus-audio.ogg");
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(500));

        //Act
        session.Stop();
        TimeSpan atReturn = session.Position;
        Thread.Sleep(300);
        TimeSpan later = session.Position;
        session.Play();
        bool runs = WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));

        //Assert
        session.State.Should().Be(VideoPlaybackState.Playing);
        atReturn.Should().Be(TimeSpan.Zero);
        later.Should().Be(TimeSpan.Zero);
        runs.Should().BeTrue();
    }

    [Fact]
    public void After_a_paused_seek_PositionChanged_never_reports_the_old_position()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = WriteFourSecondClip("audio-seek-event", "opus-audio.ogg");
        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PositionUpdateInterval = TimeSpan.FromMilliseconds(10) });
        List<TimeSpan> reported = new List<TimeSpan>();
        session.PositionChanged += (s, e) =>
        {
            lock (reported) reported.Add(e.Position);
        };

        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        List<string> wrong = new List<string>();

        //Act
        for (int i = 0; i < 8; i++)
        {
            session.Pause();
            TimeSpan target = TimeSpan.FromSeconds(i % 2 == 0 ? 1 : 2);
            session.Seek(target);
            lock (reported) reported.Clear();
            Thread.Sleep(150);

            TimeSpan[] after;
            lock (reported) after = reported.ToArray();

            foreach (TimeSpan value in after)
            {
                if (value != target) wrong.Add($"seek {i} to {target}: PositionChanged reported {value}");
            }

            if (after.Length == 0) wrong.Add($"seek {i} to {target}: PositionChanged reported nothing");

            // Play a moment between seeks, so the position the clock thread last reported is not the target.
            session.Play();
            Thread.Sleep(120);
        }

        //Assert
        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    [Fact]
    public void A_seek_past_the_end_of_the_sound_holds_the_position_and_plays_the_picture_to_its_end()
    {
        //Arrange - four seconds of picture over one second of sound. A seek to three seconds finds no sound
        // at all, so the audio player is never re-based and the stopwatch must take over from three seconds.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = SyntheticMedia.WriteRawCbv(
            SyntheticMedia.ScratchPath("audio-seek-past-sound", "clip.cbv"),
            frameCount: 100,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("opus-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(200));
        session.Pause();

        //Act
        session.Seek(TimeSpan.FromSeconds(3));
        TimeSpan atReturn = session.Position;
        Thread.Sleep(200);
        TimeSpan settled = session.Position;
        session.Play();
        List<TimeSpan> seen = Watch(session, TimeSpan.FromMilliseconds(400));
        bool ended = WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        atReturn.Should().Be(TimeSpan.FromSeconds(3));
        settled.Should().Be(TimeSpan.FromSeconds(3));
        FirstStepBackwards(seen, TimeSpan.FromSeconds(3)).Should().BeEmpty();
        seen[seen.Count - 1].Should().BeGreaterThan(TimeSpan.FromSeconds(3.2));
        ended.Should().BeTrue();
        session.Position.Should().BeGreaterThan(TimeSpan.FromSeconds(3.9));
    }

    [Fact]
    public void A_seek_back_into_the_sound_after_it_has_ended_gives_the_clock_back_to_the_audio()
    {
        //Arrange - the sound runs out at one second and the stopwatch takes over; a seek back to half a second
        // has sound again, and then a seek to three seconds has none.
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = SyntheticMedia.WriteRawCbv(
            SyntheticMedia.ScratchPath("audio-seek-after-sound-ended", "clip.cbv"),
            frameCount: 100,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("opus-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromSeconds(1.5));

        //Act
        session.Seek(TimeSpan.FromSeconds(0.5));
        TimeSpan backInTheSound = session.Position;
        List<TimeSpan> soundSeen = Watch(session, TimeSpan.FromMilliseconds(300));
        session.Pause();
        session.Seek(TimeSpan.FromSeconds(3));
        TimeSpan pastTheSound = session.Position;
        Thread.Sleep(100);
        TimeSpan pastTheSoundSettled = session.Position;
        session.Play();
        bool ended = WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        backInTheSound.Should().Be(TimeSpan.FromSeconds(0.5));
        FirstStepBackwards(soundSeen, TimeSpan.FromSeconds(0.5)).Should().BeEmpty();
        soundSeen[soundSeen.Count - 1].Should().BeGreaterThan(TimeSpan.FromSeconds(0.65));
        pastTheSound.Should().Be(TimeSpan.FromSeconds(3));
        pastTheSoundSettled.Should().Be(TimeSpan.FromSeconds(3));
        ended.Should().BeTrue();
    }

    [Fact]
    public void A_looping_clip_with_sound_starts_again_instead_of_ending()
    {
        //Arrange
        SkipUnlessAudioIsEnabled();
        CodeBrixAudioOpus.Register();
        string path = SyntheticMedia.WriteRawCbv(
            SyntheticMedia.ScratchPath("audio-loop", "clip.cbv"),
            frameCount: 25,
            frameRate: 25,
            keyFrameInterval: 5,
            audioOggPath: TestAssets.Path("opus-audio.ogg"));

        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);
        session.Open(path);
        session.IsLooping = true;

        //Act
        session.Play();
        bool late = WaitFor(() => session.Position > TimeSpan.FromSeconds(0.8));
        bool wrapped = late && WaitFor(() => session.Position < TimeSpan.FromSeconds(0.5));
        bool runsAgain = wrapped && WaitFor(() => session.Position > TimeSpan.FromSeconds(0.6));

        //Assert
        late.Should().BeTrue();
        wrapped.Should().BeTrue();
        runsAgain.Should().BeTrue();
        ended.Should().Be(0);
        session.State.Should().Be(VideoPlaybackState.Playing);
    }

    /// <summary>Reads the position every few milliseconds for a while, and returns what it read.</summary>
    private static List<TimeSpan> Watch(VideoPlaybackSession session, TimeSpan howLong)
    {
        List<TimeSpan> seen = new List<TimeSpan>();
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < howLong)
        {
            seen.Add(session.Position);
            Thread.Sleep(2);
        }

        seen.Add(session.Position);
        return seen;
    }

    /// <summary>
    /// Describes the first reading that is lower than the one before it or lower than a floor, or returns
    /// an empty string when the readings never went backwards.
    /// </summary>
    private static string FirstStepBackwards(List<TimeSpan> seen, TimeSpan floor)
    {
        for (int i = 0; i < seen.Count; i++)
        {
            if (seen[i] < floor) return $"reading {i} was {seen[i]}, below {floor}";
            if (i > 0 && seen[i] < seen[i - 1]) return $"reading {i} was {seen[i]}, after {seen[i - 1]}";
        }

        return string.Empty;
    }

    /// <summary>
    /// Writes four seconds of uncompressed picture at 25 frames a second, a key frame every ten, over the
    /// named corpus sound file laid end to end until it lasts as long.
    /// </summary>
    private static string WriteFourSecondClip(string name, string audioFile) =>
        SyntheticMedia.WriteRawCbv(
            SyntheticMedia.ScratchPath(name, "clip.cbv"),
            frameCount: 100,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path(audioFile),
            audioRepeat: 4);

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

    private static VideoPlaybackSession NewSession() => new VideoPlaybackSession();

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
