using System;
using System.IO;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The bespoke container's Silent header flag (bit 2, a version-1 feature): written by the muxer and the
/// authoring entry point, read back by the reader and the session, and refused beside any audio.
/// </summary>
public class CbvSilentFlagTests
{
    [Fact]
    public void A_silent_file_round_trips_the_flag_and_is_version_1()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using (CbvMuxer muxer = new CbvMuxer(output, leaveOutputOpen: true))
        {
            muxer.Silent = true;
            DeclarePicture(muxer);
            muxer.WriteChunk(1, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.FromMilliseconds(40), true);

            //Act
            muxer.FormatVersion.Should().Be(CbvFormat.MasterVersion);
            muxer.Complete();
        }

        using CbvReader reader = new CbvReader(new MemoryMediaSource(output.ToArray(), "silent.cbvmaster"));

        //Assert
        reader.IsSilent.Should().BeTrue();
        (reader.Flags & CbvHeaderFlags.Silent).Should().Be(CbvHeaderFlags.Silent);
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.HeaderChecksumVerified.Should().BeTrue();
        reader.Tracks.Count.Should().Be(1);
    }

    [Fact]
    public void A_file_without_the_flag_is_not_silent_by_intent_and_stays_version_0()
    {
        //Arrange
        string path = SyntheticMedia.WriteRawCbv(SyntheticMedia.ScratchPath("cbv-not-silent", "clip.cbv"), frameCount: 3);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.IsSilent.Should().BeFalse();
        reader.Version.Should().Be(CbvFormat.Version);
    }

    [Fact]
    public void The_muxer_refuses_an_audio_track_in_a_silent_file()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using CbvMuxer muxer = new CbvMuxer(output, leaveOutputOpen: true);
        muxer.Silent = true;

        //Act
        Action declare = () => muxer.AddAudioTrack(
            VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 48000, 2);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*silent file carries no sound*");
    }

    [Fact]
    public void The_muxer_refuses_the_flag_once_an_audio_track_is_declared()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using CbvMuxer muxer = new CbvMuxer(output, leaveOutputOpen: true);
        muxer.AddAudioTrack(VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 48000, 2);

        //Act
        Action flag = () => muxer.Silent = true;

        //Assert
        flag.Should().Throw<VideoPlaybackException>().WithMessage("*silent file carries no sound*");
        muxer.Silent.Should().BeFalse();
    }

    [Fact]
    public void An_authoring_request_flagged_silent_with_an_ogg_input_is_refused()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-silent-ogg", "clip.cbvmaster"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            AudioOggPath = TestAssets.Path("vorbis-audio.ogg"),
            Silent = true,
        };

        //Act
        Action write = () => CbvAuthoring.Write(request);

        //Assert
        write.Should().Throw<ArgumentException>().WithMessage("*flagged Silent*AudioOggPath*silent file carries no sound*");
        File.Exists(request.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void An_authoring_request_flagged_silent_with_packet_audio_is_refused()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-silent-packets", "clip.cbvmaster"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            PacketAudio = new CbvPacketAudioInput(
                VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 48000, 2),
            Silent = true,
        };

        //Act
        Action write = () => CbvAuthoring.Write(request);

        //Assert
        write.Should().Throw<ArgumentException>().WithMessage("*flagged Silent*PacketAudio*");
    }

    [Fact]
    public void An_authoring_request_flagged_silent_writes_a_version_1_silent_file()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-silent-write", "clip.cbvmaster"),
            VideoIvfPath = TestAssets.Path("av1-video-only.ivf"),
            Silent = true,
        };

        //Act
        CbvAuthoringResult result = CbvAuthoring.Write(request);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.Path));

        //Assert
        result.FormatVersion.Should().Be(CbvFormat.MasterVersion);
        result.AudioTrackId.Should().Be(0);
        reader.IsSilent.Should().BeTrue();
        reader.Version.Should().Be(CbvFormat.MasterVersion);
    }

    [Fact]
    public void A_session_reports_a_flagged_file_as_silent_and_forgets_it_on_close()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-silent-session", "clip.cbvmaster");
        using (CbvMuxer muxer = CbvMuxer.Create(path))
        {
            muxer.Silent = true;
            DeclarePicture(muxer);
            for (int i = 0; i < 3; i++)
            {
                muxer.WriteChunk(
                    1, SyntheticMedia.MakeFrame(i), TimeSpan.FromMilliseconds(40 * i), TimeSpan.FromMilliseconds(40), i == 0);
            }

            muxer.Complete();
        }

        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });

        //Act
        session.Open(path);
        bool openSilent = session.IsSilent;
        bool hadAudio = session.AudioTrack != null;
        session.Close();

        //Assert
        openSilent.Should().BeTrue();
        hadAudio.Should().BeFalse();
        session.IsSilent.Should().BeFalse();
    }

    [Fact]
    public void A_session_never_reports_silent_for_a_bespoke_file_without_the_flag_or_for_matroska()
    {
        //Arrange
        string cbv = SyntheticMedia.WriteRawCbv(SyntheticMedia.ScratchPath("cbv-silent-absent", "clip.cbv"), frameCount: 3);
        using VideoPlaybackSession bespoke = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        using VideoPlaybackSession matroska = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });

        //Act
        bespoke.Open(cbv);
        matroska.Open(TestAssets.Path("raw-opus.mkv"));

        //Assert
        bespoke.AudioTrack.Should().BeNull();
        bespoke.IsSilent.Should().BeFalse();
        matroska.IsSilent.Should().BeFalse();
    }

    private static void DeclarePicture(CbvMuxer muxer)
    {
        RawVideoDescriptor video = SyntheticMedia.Video;
        muxer.AddVideoTrack(
            VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(video), video.Width, video.Height,
            bitDepth: video.BitDepth, layout: video.Layout, color: video.Color);
    }
}
