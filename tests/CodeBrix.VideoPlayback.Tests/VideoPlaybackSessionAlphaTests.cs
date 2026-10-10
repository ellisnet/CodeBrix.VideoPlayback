using System;
using System.Diagnostics;
using System.Threading;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Drives the player over master (Mode3) files made of uncompressed frames: a picture track and an alpha-plane
/// track decoded side by side, paired by timestamp, through playback and seeking.
/// </summary>
/// <remarks>
/// Every picture frame names its own number in its luma, and every alpha frame names the same number in its
/// gradient (see <see cref="SyntheticMasterMedia.AlphaSample" />), so a test can see that each picture frame
/// arrived with ITS alpha frame and not a neighbour's.
/// </remarks>
public class VideoPlaybackSessionAlphaTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Open_reports_the_alpha_track_beside_the_picture()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("alpha-open", "clip.cbvmaster"));
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);

        //Assert
        session.HasAlpha.Should().BeTrue();
        session.AlphaTrack.Should().NotBeNull();
        session.AlphaTrack.VideoRole.Should().Be(VideoTrackRole.AlphaPlane);
        session.VideoTrack.VideoRole.Should().Be(VideoTrackRole.Picture);
        session.VideoTrack.Id.Should().Be(1);
        session.Tracks.Count.Should().Be(2);
    }

    [Fact]
    public void The_first_frame_arrives_with_its_own_alpha_plane()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("alpha-first", "clip.cbvmaster"));
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        (int picture, int alphaFrame, bool hasAlpha) = TakeFrame(session);

        //Assert
        arrived.Should().BeTrue();
        hasAlpha.Should().BeTrue();
        picture.Should().Be(0);
        alphaFrame.Should().Be(0);
    }

    [Fact]
    public void Every_presented_frame_is_paired_with_the_alpha_frame_of_the_same_timestamp()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("alpha-every", "clip.cbvmaster"), frameCount: 25, frameRate: 50);
        using VideoPlaybackSession session = NewSession();
        int mismatches = 0;
        int withoutAlpha = 0;
        int checkedFrames = 0;

        session.FrameReady += (s, e) =>
        {
            if (!session.Presenter.TryTakeLatest(out VideoFrame frame)) return;
            using (frame)
            {
                if (!frame.HasAlpha)
                {
                    Interlocked.Increment(ref withoutAlpha);
                    return;
                }

                int picture = SyntheticMedia.FrameNumberFromLuma(frame.Y.GetRowBytes(0)[0]);
                if (AlphaFrameNumber(frame) != picture) Interlocked.Increment(ref mismatches);
                Interlocked.Increment(ref checkedFrames);
            }
        };

        session.Open(path);

        //Act
        session.Play();
        bool ended = WaitFor(() => session.State == VideoPlaybackState.Ended);

        //Assert
        ended.Should().BeTrue();
        checkedFrames.Should().BeGreaterThan(5);
        mismatches.Should().Be(0);
        withoutAlpha.Should().Be(0);
    }

    [Fact]
    public void An_exact_seek_lands_on_the_same_frame_in_both_tracks()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("alpha-seek", "clip.cbvmaster"), frameCount: 50, frameRate: 25, keyFrameInterval: 10);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        session.Presenter.Clear();

        //Act
        session.Seek(TimeSpan.FromSeconds(1.0));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        (int picture, int alphaFrame, bool hasAlpha) = TakeFrame(session);

        //Assert
        arrived.Should().BeTrue();
        hasAlpha.Should().BeTrue();
        picture.Should().Be(25);
        alphaFrame.Should().Be(25);
    }

    [Fact]
    public void A_key_frame_seek_lands_on_the_key_frame_in_both_tracks()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("alpha-seek-key", "clip.cbvmaster"), frameCount: 50, frameRate: 25, keyFrameInterval: 10);
        using VideoPlaybackSession session = NewSession(new VideoPlaybackOptions { SeekMode = VideoSeekMode.KeyFrameOnly });
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        session.Presenter.Clear();

        //Act
        session.Seek(TimeSpan.FromSeconds(1.0));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        (int picture, int alphaFrame, bool hasAlpha) = TakeFrame(session);

        //Assert
        arrived.Should().BeTrue();
        hasAlpha.Should().BeTrue();
        picture.Should().Be(20);
        alphaFrame.Should().Be(20);
    }

    [Fact]
    public void A_seek_backwards_after_playing_pairs_correctly_again()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("alpha-seek-back", "clip.cbvmaster"), frameCount: 50, frameRate: 50, keyFrameInterval: 10);
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        session.Play();
        WaitFor(() => session.State == VideoPlaybackState.Ended);
        session.Pause();
        session.Presenter.Clear();

        //Act
        session.Seek(TimeSpan.FromSeconds(0.26));
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        (int picture, int alphaFrame, bool hasAlpha) = TakeFrame(session);

        //Assert
        arrived.Should().BeTrue();
        hasAlpha.Should().BeTrue();
        picture.Should().Be(13);
        alphaFrame.Should().Be(13);
    }

    [Fact]
    public void A_master_file_without_an_alpha_track_plays_opaque_frames()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("alpha-none", "clip.cbvmaster"), withAlpha: false, flacPackets: 4);
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);
        bool arrived = WaitFor(() => session.Presenter.HasFrame);
        (int picture, int _, bool hasAlpha) = TakeFrame(session);

        //Assert
        arrived.Should().BeTrue();
        session.HasAlpha.Should().BeFalse();
        session.AlphaTrack.Should().BeNull();
        hasAlpha.Should().BeFalse();
        picture.Should().Be(0);
    }

    [Fact]
    public void An_ordinary_bespoke_file_has_no_alpha()
    {
        //Arrange
        string path = SyntheticMedia.WriteRawCbv(SyntheticMedia.ScratchPath("alpha-plain", "clip.cbv"), frameCount: 10);
        using VideoPlaybackSession session = NewSession();

        //Act
        session.Open(path);
        WaitFor(() => session.Presenter.HasFrame);
        (int _, int _, bool hasAlpha) = TakeFrame(session);

        //Assert
        session.HasAlpha.Should().BeFalse();
        hasAlpha.Should().BeFalse();
    }

    [Fact]
    public void Closing_releases_the_alpha_track()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("alpha-close", "clip.cbvmaster"));
        using VideoPlaybackSession session = NewSession();
        session.Open(path);

        //Act
        session.Close();

        //Assert
        session.HasAlpha.Should().BeFalse();
        session.AlphaTrack.Should().BeNull();
    }

    private static int AlphaFrameNumber(VideoFrame frame)
    {
        // Column 0 holds (frame * 3) % 256, which names the frame for the first 85 frames.
        ReadOnlySpan<byte> row = frame.A.GetRowBytes(0);
        for (int n = 0; n < 85; n++)
        {
            if (SyntheticMasterMedia.AlphaSample(n, 0) == row[0] && SyntheticMasterMedia.AlphaSample(n, 1) == row[1]) return n;
        }

        return -1;
    }

    private static (int Picture, int Alpha, bool HasAlpha) TakeFrame(VideoPlaybackSession session)
    {
        session.Presenter.TryTakeLatest(out VideoFrame frame);
        using (frame)
        {
            int picture = SyntheticMedia.FrameNumberFromLuma(frame.Y.GetRowBytes(0)[0]);
            return frame.HasAlpha ? (picture, AlphaFrameNumber(frame), true) : (picture, -1, false);
        }
    }

    private static VideoPlaybackSession NewSession(VideoPlaybackOptions options = null)
    {
        VideoPlaybackOptions effective = options ?? new VideoPlaybackOptions();
        effective.PlayAudio = false;
        return new VideoPlaybackSession(effective);
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
}
