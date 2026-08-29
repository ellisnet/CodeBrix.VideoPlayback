using System;
using System.IO;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.RawCodec;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks that a file this library cannot play fails with a message naming the piece that is missing and the
/// package that supplies it - not with "unsupported".
/// </summary>
/// <remarks>
/// These messages are contractual. An application shows them to a developer who has to work out which NuGet
/// package to add, and the words are the whole point.
/// </remarks>
public class VideoPlaybackFailureTests
{
    [Fact]
    public void An_av1_file_with_no_video_decoder_names_the_package_that_supplies_one()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("missing-video", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        });

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        //Act
        Action act = () => session.Open(path);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage(
            "video codec 'av01' has no registered decoder — reference CodeBrix.VideoPlayback.Dav1d and call "
            + "CodeBrixVideoPlaybackDav1d.Register()");
    }

    [Fact]
    public void An_opus_file_with_no_audio_decoder_names_the_package_that_supplies_one()
    {
        //Arrange
        Assert.SkipWhen(
            OpusPacketDecoderIsRegistered(),
            "The Opus packet codec is already registered in this process, so the missing-decoder path cannot be "
            + "reached. It is registered only by the opt-in audible tests.");

        string path = SyntheticMedia.ScratchPath("missing-audio", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            AudioOggPath = TestAssets.Path("opus-audio.ogg"),
        });

        using VideoPlaybackSession session = new VideoPlaybackSession();

        //Act
        Action act = () => session.Open(path);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage(
            "audio codec 'opus' has no registered decoder — reference CodeBrix.Audio.Opus and call "
            + "CodeBrixAudioOpus.Register()");
    }

    [Fact]
    public void The_failure_also_reaches_the_MediaFailed_event()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("failed-event", "clip.cbv");
        CbvAuthoring.Write(new CbvAuthoringRequest
        {
            OutputPath = path,
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
        });

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

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
        reported.Should().Contain("CodeBrixVideoPlaybackDav1d.Register()");
        session.State.Should().Be(VideoPlaybackState.Failed);
    }

    [Fact]
    public void A_file_that_is_neither_container_says_what_it_actually_begins_with()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("not-media", "clip.cbv");
        File.WriteAllBytes(path, new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 });

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        //Act
        Action act = () => session.Open(path);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*52 49 46 46*");
    }

    [Fact]
    public void A_matroska_file_carrying_a_codec_this_library_does_not_read_is_refused_by_name()
    {
        //Arrange
        Assert.SkipUnless(
            TestAssets.Exists("av1-opus.webm"),
            "The golden corpus has not been generated.");

        string path = TestAssets.Path("av1-opus.webm");
        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        //Act
        Action act = () => session.Open(path);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*av01*");
    }

    [Fact]
    public void Seeking_a_source_that_cannot_seek_says_which_half_is_the_problem()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("no-seek", "clip.cbv");
        SyntheticMedia.WriteRawCbv(path, frameCount: 10);

        using VideoPlaybackSession session = new VideoPlaybackSession(
            new VideoPlaybackOptions { PlayAudio = false });

        session.RegisterDecoderFactory(new RawVideoDecoderFactory());
        session.Open(path);
        session.Close();

        //Act
        Action act = () => session.Seek(TimeSpan.Zero);

        //Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*No media is open*");
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
