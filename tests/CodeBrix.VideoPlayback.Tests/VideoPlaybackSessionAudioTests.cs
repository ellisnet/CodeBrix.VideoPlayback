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
/// tells you nothing.
/// </para>
/// </remarks>
[Collection("Shared audio output")]
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
        session.PlaybackEnded += (s, e) => ended++;

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);

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
        session.PlaybackEnded += (s, e) => ended++;

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);

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
        session.PlaybackEnded += (s, e) => ended++;

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);
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
            ended++;
            endedAt = session.Position;
        };

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);

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
        session.PlaybackEnded += (s, e) => ended++;

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);
        TimeSpan reached = session.Position;

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(3));
        reached.Should().BeGreaterThan(TimeSpan.FromSeconds(2.9));
        session.Notices.Should().NotContain(notice => notice.Contains("parking budget"));
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
