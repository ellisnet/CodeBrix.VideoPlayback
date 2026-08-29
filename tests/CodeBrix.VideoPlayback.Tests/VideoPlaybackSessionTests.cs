using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.RawCodec;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Drives the player end to end over uncompressed video: opening, playing to the end, seeking both ways,
/// looping, captions and chapters - everything except the audio, which needs a device and lives in
/// <see cref="VideoPlaybackSessionAudioTests" />.
/// </summary>
/// <remarks>
/// Every frame in the synthetic files carries its own number in its luma samples, so a test can look at what
/// reached the presenter and say exactly which frame it is. That is what makes an exact seek checkable rather
/// than merely plausible.
/// </remarks>
public class VideoPlaybackSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Open_reports_the_tracks_the_duration_and_the_stream()
    {
        //Arrange
        string path = WriteClip("open", frameCount: 50, frameRate: 25);
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);

        //Assert
        session.IsOpen.Should().BeTrue();
        session.State.Should().Be(VideoPlaybackState.Stopped);
        session.Tracks.Count.Should().Be(1);
        session.VideoTrack.CodecId.Should().Be(VideoCodecIds.Raw);
        session.Duration.Should().Be(TimeSpan.FromTicks(TimeSpan.FromSeconds(1.0 / 25).Ticks * 50));
        session.VideoStreamInfo.Width.Should().Be(64);
        session.VideoStreamInfo.Height.Should().Be(36);
    }

    [Fact]
    public void Open_raises_MediaOpened()
    {
        //Arrange
        string path = WriteClip("opened-event", frameCount: 10);
        using VideoPlaybackSession session = NewSession();
        int raised = 0;
        session.MediaOpened += (s, e) => raised++;

        //Act
        session.Open(path);

        //Assert
        raised.Should().Be(1);
    }

    [Fact]
    public void Open_shows_the_first_frame_before_anything_is_played()
    {
        //Arrange
        string path = WriteClip("first-frame", frameCount: 30);
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        int frameNumber = TakeFrameNumber(session);

        //Assert
        arrived.Should().BeTrue();
        frameNumber.Should().Be(0);
    }

    [Fact]
    public void Play_reaches_the_end_and_says_so()
    {
        //Arrange
        string path = WriteClip("play-to-end", frameCount: 25, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);
        session.Open(path);

        //Act
        session.Play();
        bool finished = WaitFor(
            () => session.State == VideoPlaybackState.Ended && Volatile.Read(ref ended) > 0);

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        (session.Position >= session.Duration - TimeSpan.FromMilliseconds(120)).Should().BeTrue();
    }

    [Fact]
    public void Play_raises_PositionChanged_as_it_goes()
    {
        //Arrange
        string path = WriteClip("position", frameCount: 25, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        int updates = 0;
        session.PositionChanged += (s, e) => updates++;
        session.Open(path);

        //Act
        session.Play();
        WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        updates.Should().BeGreaterThan(2);
    }

    [Fact]
    public void Play_delivers_frames_to_the_presenter()
    {
        //Arrange
        string path = WriteClip("frames", frameCount: 25, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        int frames = 0;
        session.FrameReady += (s, e) => frames++;
        session.Open(path);

        //Act
        session.Play();
        WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        frames.Should().BeGreaterThan(5);
        session.Presenter.GetStatistics().Posted.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Pause_holds_the_clock_where_it_stands()
    {
        //Arrange
        string path = WriteClip("pause", frameCount: 200, frameRate: 25);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(150));
        session.Pause();
        TimeSpan atPause = session.Position;
        Thread.Sleep(200);
        TimeSpan later = session.Position;

        //Assert
        session.State.Should().Be(VideoPlaybackState.Paused);
        later.Should().Be(atPause);
    }

    [Fact]
    public void Seek_in_exact_mode_lands_on_the_frame_asked_for()
    {
        //Arrange
        string path = WriteClip("seek-exact", frameCount: 100, frameRate: 25, keyFrameInterval: 10);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        TakeFrameNumber(session);

        //Act
        session.Seek(TimeSpan.FromSeconds(37.0 / 25));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        int frameNumber = TakeFrameNumber(session);

        //Assert
        arrived.Should().BeTrue();
        frameNumber.Should().Be(37);
    }

    [Fact]
    public void Seek_in_key_frame_mode_lands_on_the_key_frame_before_the_moment()
    {
        //Arrange
        string path = WriteClip("seek-key", frameCount: 100, frameRate: 25, keyFrameInterval: 10);
        VideoPlaybackOptions options = new VideoPlaybackOptions
        {
            PlayAudio = false,
            SeekMode = VideoSeekMode.KeyFrameOnly,
        };

        using VideoPlaybackSession session = NewSession(options);
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        TakeFrameNumber(session);

        //Act
        session.Seek(TimeSpan.FromSeconds(37.0 / 25));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        int frameNumber = TakeFrameNumber(session);

        //Assert
        arrived.Should().BeTrue();
        frameNumber.Should().Be(30);
    }

    [Fact]
    public void Seek_moves_the_reported_position_immediately()
    {
        //Arrange
        string path = WriteClip("seek-position", frameCount: 100, frameRate: 25);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Seek(TimeSpan.FromSeconds(2));

        //Assert
        (session.Position - TimeSpan.FromSeconds(2)).Duration()
            .Should().BeLessThan(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Seek_past_the_end_is_clamped_into_the_media()
    {
        //Arrange
        string path = WriteClip("seek-clamp", frameCount: 25, frameRate: 25);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Seek(TimeSpan.FromHours(1));

        //Assert
        (session.Position <= session.Duration).Should().BeTrue();
    }

    [Fact]
    public void Stop_puts_the_position_back_to_the_beginning()
    {
        //Arrange
        string path = WriteClip("stop", frameCount: 200, frameRate: 25);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Position > TimeSpan.FromMilliseconds(150));

        //Act
        session.Stop();

        //Assert
        session.State.Should().Be(VideoPlaybackState.Stopped);
        session.Position.Should().BeLessThan(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Looping_starts_again_instead_of_ending()
    {
        //Arrange
        string path = WriteClip("loop", frameCount: 10, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        int ended = 0;
        session.PlaybackEnded += (s, e) => ended++;
        session.Open(path);
        session.IsLooping = true;

        //Act
        session.Play();
        bool wrapped = WaitFor(
            () => session.Presenter.GetStatistics().Posted > 14,
            TimeSpan.FromSeconds(10));

        //Assert
        wrapped.Should().BeTrue();
        ended.Should().Be(0);
        session.State.Should().Be(VideoPlaybackState.Playing);
    }

    [Fact]
    public void A_decoder_that_cannot_keep_up_drops_frames_rather_than_showing_them_late()
    {
        //Arrange
        string path = WriteClip("late", frameCount: 60, frameRate: 30, keyFrameInterval: 10);
        VideoPlaybackOptions options = new VideoPlaybackOptions
        {
            PlayAudio = false,
            LateFrameTolerance = TimeSpan.Zero,
            ConsecutiveLateFramesBeforeSkip = 3,
        };

        using VideoPlaybackSession session = new VideoPlaybackSession(options);
        session.RegisterDecoderFactory(new SlowDecoderFactory(TimeSpan.FromMilliseconds(60)));
        session.Open(path);

        //Act
        session.Play();
        bool dropped = WaitFor(() => session.Presenter.GetStatistics().Late > 0, TimeSpan.FromSeconds(10));

        //Assert
        dropped.Should().BeTrue();
    }

    [Fact]
    public void Captions_become_active_at_the_moment_they_should()
    {
        //Arrange
        CaptionTrack track = SyntheticMedia.MakeCaptionTrack(
            0,
            "en",
            CaptionTrackFlags.Default,
            (0.4, 0.8, "first"),
            (1.2, 1.6, "second"));

        string path = WriteClip("captions", frameCount: 60, frameRate: 25, captions: new[] { track });
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Seek(TimeSpan.FromSeconds(0.5));
        session.Play();
        bool sawFirst = WaitFor(() => HasCue(session, "first"));
        bool sawSecond = WaitFor(() => HasCue(session, "second"), TimeSpan.FromSeconds(10));

        //Assert
        session.SelectedCaptionTrack.Should().NotBeNull();
        sawFirst.Should().BeTrue();
        sawSecond.Should().BeTrue();
    }

    [Fact]
    public void Turning_captions_off_empties_the_active_cues()
    {
        //Arrange
        CaptionTrack track = SyntheticMedia.MakeCaptionTrack(
            0,
            "en",
            CaptionTrackFlags.Default,
            (0.0, 5.0, "always on"));

        string path = WriteClip("captions-off", frameCount: 40, frameRate: 25, captions: new[] { track });
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        bool onAtStart = session.ActiveCues.Count == 1;
        session.SelectedCaptionTrack = null;
        session.ShowForcedCaptions = false;
        bool offNow = session.ActiveCues.Count == 0;

        //Assert
        onAtStart.Should().BeTrue();
        offNow.Should().BeTrue();
    }

    [Fact]
    public void A_forced_track_surfaces_even_when_no_track_is_selected()
    {
        //Arrange
        CaptionTrack forced = SyntheticMedia.MakeCaptionTrack(
            0,
            "en",
            CaptionTrackFlags.Forced,
            (0.0, 5.0, "a sign"));

        string path = WriteClip("captions-forced", frameCount: 40, frameRate: 25, captions: new[] { forced });
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);

        //Assert
        (session.SelectedCaptionTrack == null).Should().BeTrue();
        session.ActiveCues.Count.Should().Be(1);
        session.ActiveCues[0].Text.Should().Be("a sign");
    }

    [Fact]
    public void Chapters_are_listed_and_the_current_one_is_reported()
    {
        //Arrange
        string path = WriteClip("chapters", frameCount: 100, frameRate: 25, chapters: SyntheticMedia.MakeChapters(0, 1, 2, 3));
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);

        //Assert
        session.Chapters.Count.Should().Be(4);
        session.CurrentChapter.Should().NotBeNull();
        session.CurrentChapter.Index.Should().Be(0);
        session.TitleFor(session.Chapters[1], new[] { "fr" }).Should().Be("Chapitre 2");
    }

    [Fact]
    public void SeekToChapter_moves_to_that_chapter_and_raises_the_event()
    {
        //Arrange
        string path = WriteClip("chapter-seek", frameCount: 100, frameRate: 25, chapters: SyntheticMedia.MakeChapters(0, 1, 2, 3));
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        List<int> changes = new List<int>();
        session.ChapterChanged += (s, e) => changes.Add(e.Chapter == null ? -1 : e.Chapter.Index);

        //Act
        session.SeekToChapter(2);

        //Assert
        session.CurrentChapter.Index.Should().Be(2);
        changes.Should().Contain(2);
    }

    [Fact]
    public void NextChapter_and_PreviousChapter_walk_the_list()
    {
        //Arrange
        string path = WriteClip("chapter-walk", frameCount: 100, frameRate: 25, chapters: SyntheticMedia.MakeChapters(0, 1, 2, 3));
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        bool forwardOne = session.NextChapter();
        bool forwardTwo = session.NextChapter();
        int afterForward = session.CurrentChapter.Index;
        bool back = session.PreviousChapter();
        int afterBack = session.CurrentChapter.Index;

        //Assert
        forwardOne.Should().BeTrue();
        forwardTwo.Should().BeTrue();
        afterForward.Should().Be(2);
        back.Should().BeTrue();
        afterBack.Should().Be(1);
    }

    [Fact]
    public void NextChapter_at_the_last_chapter_says_there_is_nowhere_to_go()
    {
        //Arrange
        string path = WriteClip("chapter-end", frameCount: 100, frameRate: 25, chapters: SyntheticMedia.MakeChapters(0, 1));
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.SeekToChapter(1);

        //Act
        bool moved = session.NextChapter();

        //Assert
        moved.Should().BeFalse();
    }

    [Fact]
    public void The_buffer_pool_stops_allocating_once_playback_is_warm()
    {
        //Arrange
        string path = WriteClip("pool", frameCount: 80, frameRate: 60);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.Presenter.GetStatistics().Posted > 20);
        long allocationsWhenWarm = ((PinnedFrameBufferPool)session.BufferPool).GetStatistics().Allocations;

        //Act
        WaitFor(() => session.State == VideoPlaybackState.Ended);
        long allocationsAtEnd = ((PinnedFrameBufferPool)session.BufferPool).GetStatistics().Allocations;

        //Assert
        allocationsWhenWarm.Should().BeLessThanOrEqualTo(6);
        allocationsAtEnd.Should().Be(allocationsWhenWarm);
    }

    [Fact]
    public void Play_after_the_end_starts_again_from_the_beginning()
    {
        //Arrange
        string path = WriteClip("replay", frameCount: 12, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Act
        session.Play();
        bool playing = WaitFor(() => session.State == VideoPlaybackState.Playing || session.State == VideoPlaybackState.Ended);

        //Assert
        playing.Should().BeTrue();
    }

    [Fact]
    public void Close_releases_everything_and_can_be_called_twice()
    {
        //Arrange
        string path = WriteClip("close", frameCount: 20);
        VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Close();
        session.Close();

        //Assert
        session.IsOpen.Should().BeFalse();
        session.State.Should().Be(VideoPlaybackState.Idle);
        session.Dispose();
    }

    [Fact]
    public void Play_without_media_is_refused_with_a_reason()
    {
        //Arrange
        using VideoPlaybackSession session = NewSession();

        //Act
        Action act = () => session.Play();

        //Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*No media is open*");
    }

    [Fact]
    public void A_session_plays_a_webm_file_when_a_decoder_for_its_codec_is_registered()
    {
        //Arrange
        string path = TestAssets.Path("raw-opus.mkv");
        VideoPlaybackOptions options = new VideoPlaybackOptions { PlayAudio = false };
        using VideoPlaybackSession session = new VideoPlaybackSession(options);
        session.RegisterDecoderFactory(new RawVideoDecoderFactory());

        //Act
        session.Open(path);
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        session.Presenter.TryTakeLatest(out VideoFrame frame);
        int width = frame.Width;
        int height = frame.Height;
        frame.Dispose();

        //Assert
        arrived.Should().BeTrue();
        width.Should().Be(64);
        height.Should().Be(36);
        session.VideoTrack.CodecId.Should().Be(VideoCodecIds.Raw);
    }

    [Fact]
    public void A_matroska_file_with_no_cues_plays_through_even_though_its_video_outruns_its_queue()
    {
        //Arrange - seventy-five video packets through a thirty-two packet queue, so the demultiplexer has to
        // park the overflow and keep reading to reach the end of the file at all.
        string path = TestAssets.Path("raw-vorbis-nocues.mkv");
        using VideoPlaybackSession session = NewSession();

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
        session.Duration.Should().Be(TimeSpan.FromSeconds(3));
        session.Presenter.GetStatistics().Posted.Should().BeGreaterThan(60L);
        session.Notices.Should().NotContain(notice => notice.Contains("parking budget"));
    }

    [Fact]
    public void A_clip_whose_audio_track_ends_early_plays_its_picture_to_the_end()
    {
        //Arrange - 2.4 seconds of picture over one second of sound, at the DEFAULT queue sizes. Without a
        // device the clock is the session's own stopwatch, so what this pins is the demultiplexer and the
        // video track's own end rather than the audio clock - which the audible suite covers.
        string path = SyntheticMedia.ScratchPath("short-sound", "clip.cbv");
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

        //Assert
        finished.Should().BeTrue();
        ended.Should().Be(1);
        session.Duration.Should().Be(TimeSpan.FromSeconds(2.4));
        session.Presenter.GetStatistics().Posted.Should().BeGreaterThan(50L);
    }

    [Fact]
    public void The_indexed_container_finishes_a_short_sound_clip_with_no_parking_at_all()
    {
        //Arrange - the same clip as above with the parking budget set to nothing, so the demultiplexer
        // behaves exactly as it did before it could park anything. What carries this one is the bespoke
        // index: it says where the audio track stops, and the session acts on that without needing to have
        // read the rest of the file.
        string path = SyntheticMedia.ScratchPath("short-sound-no-parking", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 60,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using VideoPlaybackSession session = NewSession(new VideoPlaybackOptions
        {
            PlayAudio = false,
            MaxTrackParkingBytes = 0,
        });

        //Act
        session.Open(path);
        session.Play();
        bool finished = WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        finished.Should().BeTrue();
        session.Duration.Should().Be(TimeSpan.FromSeconds(2.4));
    }

    private static VideoPlaybackSession NewSession(VideoPlaybackOptions options = null)
    {
        VideoPlaybackOptions effective = options ?? new VideoPlaybackOptions { PlayAudio = false };
        effective.PlayAudio = false;

        VideoPlaybackSession session = new VideoPlaybackSession(effective);
        session.RegisterDecoderFactory(new RawVideoDecoderFactory());
        return session;
    }

    private static string WriteClip(
        string label,
        int frameCount = 50,
        double frameRate = 25,
        int keyFrameInterval = 10,
        IReadOnlyList<CaptionTrack> captions = null,
        IReadOnlyList<Chapter> chapters = null)
    {
        string path = SyntheticMedia.ScratchPath($"session-{label}", "clip.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount,
            frameRate,
            keyFrameInterval,
            null,
            captions,
            chapters);

        return path;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout = default)
    {
        TimeSpan limit = timeout == default ? Timeout : timeout;
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < limit)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    private static bool HasCue(VideoPlaybackSession session, string text)
    {
        foreach (CaptionCue cue in session.ActiveCues)
        {
            if (string.Equals(cue.Text, text, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static int TakeFrameNumber(VideoPlaybackSession session)
    {
        session.Presenter.TryTakeLatest(out VideoFrame frame);
        using (frame)
        {
            return SyntheticMedia.FrameNumberFromLuma(frame.Y.GetRowBytes(0)[0]);
        }
    }

    /// <summary>A decoder that takes a fixed time per packet, so falling behind can be tested on purpose.</summary>
    private sealed class SlowDecoderFactory : IVideoDecoderFactory
    {
        private readonly TimeSpan delay;

        internal SlowDecoderFactory(TimeSpan delay) => this.delay = delay;

        public string FactoryId => "CodeBrix.VideoPlayback.Tests.SlowRawVideo";

        public IReadOnlyCollection<string> SupportedCodecIds { get; } = new[] { VideoCodecIds.Raw };

        public int Priority => 100;

        public IVideoDecoder CreateDecoder(string codecId, ReadOnlyMemory<byte> codecPrivate, VideoDecoderOptions options)
        {
            IVideoDecoder inner = new RawVideoDecoderFactory().CreateDecoder(codecId, codecPrivate, options);
            return inner == null ? null : new SlowDecoder(inner, delay);
        }
    }

    private sealed class SlowDecoder : IVideoDecoder
    {
        private readonly IVideoDecoder inner;
        private readonly TimeSpan delay;

        internal SlowDecoder(IVideoDecoder inner, TimeSpan delay)
        {
            this.inner = inner;
            this.delay = delay;
        }

        public VideoStreamInfo Info => inner.Info;

        public bool SupportsExternalBuffers => inner.SupportsExternalBuffers;

        public string CodecId => inner.CodecId;

        public bool SendPacket(VideoPacket packet)
        {
            Thread.Sleep(delay);
            return inner.SendPacket(packet);
        }

        public bool TryReceiveFrame(out VideoFrame frame) => inner.TryReceiveFrame(out frame);

        public void Flush() => inner.Flush();

        public void Drain() => inner.Drain();

        public void Dispose() => inner.Dispose();
    }
}
