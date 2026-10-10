using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Internal;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The master (Mode3, format version 1) form of the bespoke container: the alpha-plane track and its lock-step
/// rules, the FLAC track, the version stamp, and seeking with two video tracks.
/// </summary>
public class CbvMasterContainerTests
{
    #region Version stamp

    [Fact]
    public void A_file_without_master_features_is_still_version_0()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-v0", "clip.cbv"), withAlpha: false, flacPackets: 0);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Version.Should().Be((ushort)0);
        reader.AlphaPlaneTrack.Should().BeNull();
    }

    [Fact]
    public void A_file_with_an_alpha_plane_is_version_1()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("cbv-master-alpha", "clip.cbvmaster"));

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.HeaderChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public void A_file_with_flac_audio_and_no_alpha_is_version_1()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-flac", "clip.cbvmaster"), withAlpha: false, flacPackets: 5);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Version.Should().Be(CbvFormat.MasterVersion);
        reader.AlphaPlaneTrack.Should().BeNull();
    }

    [Fact]
    public void The_muxer_reports_the_version_it_will_stamp()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using CbvMuxer muxer = new CbvMuxer(output, leaveOutputOpen: true);
        SyntheticMasterMedia.DeclareBoth(muxer, out _, out _);

        //Act
        ushort version = muxer.FormatVersion;

        //Assert
        version.Should().Be(CbvFormat.MasterVersion);
    }

    [Fact]
    public void A_file_from_a_newer_writer_is_refused_with_a_clear_message()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("cbv-master-newer", "clip.cbvmaster"));
        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 2);
        RewriteHeaderChecksum(bytes);
        File.WriteAllBytes(path, bytes);

        //Act
        Action open = () => new CbvReader(new FileMediaSource(path)).Dispose();

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*version 2*A newer writer produced it*");
    }

    #endregion

    #region Track roles and two-track reading

    [Fact]
    public void Both_video_tracks_read_back_with_their_roles()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(SyntheticMedia.ScratchPath("cbv-master-roles", "clip.cbvmaster"));

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.Tracks.Count.Should().Be(2);
        reader.Tracks[0].Kind.Should().Be(MediaTrackKind.Video);
        reader.Tracks[0].VideoRole.Should().Be(VideoTrackRole.Picture);
        reader.Tracks[0].IsAlphaPlane.Should().BeFalse();
        reader.Tracks[1].Kind.Should().Be(MediaTrackKind.Video);
        reader.Tracks[1].VideoRole.Should().Be(VideoTrackRole.AlphaPlane);
        reader.Tracks[1].IsAlphaPlane.Should().BeTrue();
        reader.Tracks[1].IsAlphaPremultiplied.Should().BeFalse();
        reader.Tracks[1].Layout.Should().Be(VideoPixelLayout.Gray);
        reader.Tracks[1].Color.Range.Should().Be(VideoColorRange.Full);
        reader.AlphaPlaneTrack.Should().BeSameAs(reader.Tracks[1]);
        reader.Tracks[1].ToString().Should().Contain("alpha plane");
    }

    [Fact]
    public void Both_video_tracks_read_back_packet_for_packet_in_lock_step()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-packets", "clip.cbvmaster"), frameCount: 12, keyFrameInterval: 4);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        List<(int Track, TimeSpan Timestamp, bool Key, byte[] Data)> packets = SyntheticMasterMedia.ReadAll(reader);

        //Assert
        packets.Count.Should().Be(24);
        for (int i = 0; i < 12; i++)
        {
            (int pictureTrack, TimeSpan pictureTime, bool pictureKey, byte[] picture) = packets[2 * i];
            (int alphaTrack, TimeSpan alphaTime, bool alphaKey, byte[] alpha) = packets[(2 * i) + 1];

            pictureTrack.Should().Be(1);
            alphaTrack.Should().Be(2);
            alphaTime.Should().Be(pictureTime);
            alphaKey.Should().Be(pictureKey);
            pictureKey.Should().Be(i % 4 == 0);
            picture.Should().Equal(SyntheticMedia.MakeFrame(i));
            alpha.Should().Equal(SyntheticMasterMedia.MakeAlphaFrame(i));
        }
    }

    [Fact]
    public void A_premultiplied_alpha_plane_is_recorded_and_read_back()
    {
        //Arrange
        string path = SyntheticMedia.ScratchPath("cbv-master-premultiplied", "clip.cbvmaster");
        using (CbvMuxer muxer = CbvMuxer.Create(path))
        {
            RawVideoDescriptor video = SyntheticMedia.Video;
            int picture = muxer.AddVideoTrack(
                VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(video), video.Width, video.Height,
                bitDepth: 8, layout: video.Layout, color: video.Color);
            int alpha = muxer.AddVideoTrack(
                VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(SyntheticMasterMedia.Alpha), video.Width, video.Height,
                bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color,
                flags: CbvTrackFlags.AlphaPlane | CbvTrackFlags.PremultipliedAlpha);
            muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
            muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
            muxer.Complete();
        }

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Assert
        reader.AlphaPlaneTrack.IsAlphaPremultiplied.Should().BeTrue();
    }

    #endregion

    #region FLAC track

    [Fact]
    public void A_flac_track_round_trips_byte_exact()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-flac-bytes", "clip.cbvmaster"),
            frameCount: 25,
            withAlpha: true,
            flacPackets: 11);

        //Act
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo audio = null;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Audio) audio = track;
        }

        List<(int Track, TimeSpan Timestamp, bool Key, byte[] Data)> packets = SyntheticMasterMedia.ReadAll(reader);
        List<byte[]> flac = new List<byte[]>();
        List<TimeSpan> times = new List<TimeSpan>();
        foreach ((int track, TimeSpan timestamp, bool _, byte[] data) in packets)
        {
            if (track != audio.Id) continue;
            flac.Add(data);
            times.Add(timestamp);
        }

        //Assert
        audio.CodecId.Should().Be(VideoCodecIds.Flac);
        audio.SampleRate.Should().Be(SyntheticMasterMedia.FlacSampleRate);
        audio.Channels.Should().Be(SyntheticMasterMedia.FlacChannels);
        audio.CodecPrivate.ToArray().Should().Equal(SyntheticMasterMedia.MakeFlacCodecPrivate());
        flac.Count.Should().Be(11);
        for (int i = 0; i < flac.Count; i++)
        {
            flac[i].Should().Equal(SyntheticMasterMedia.MakeFlacPacket(i));
            times[i].Should().Be(TimeSpan.FromTicks(
                (long)SyntheticMasterMedia.FlacBlockSize * TimeSpan.TicksPerSecond * i / SyntheticMasterMedia.FlacSampleRate));
        }
    }

    [Fact]
    public void A_flac_track_whose_header_lacks_the_stream_marker_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        byte[] header = SyntheticMasterMedia.MakeFlacCodecPrivate();
        header[0] = (byte)'X';

        //Act
        Action declare = () => muxer.AddAudioTrack(VideoCodecIds.Flac, header, 48000, 2);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*'fLaC'*");
    }

    [Fact]
    public void A_flac_track_whose_first_block_is_not_streaminfo_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        byte[] header = SyntheticMasterMedia.MakeFlacCodecPrivate();
        header[4] = 0x04;

        //Act
        Action declare = () => muxer.AddAudioTrack(VideoCodecIds.Flac, header, 48000, 2);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*STREAMINFO*type 4*");
    }

    [Fact]
    public void A_flac_track_whose_header_disagrees_with_the_track_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());

        //Act
        Action declare = () => muxer.AddAudioTrack(
            VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 44100, 2);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*44100 Hz*48000 Hz*");
    }

    #endregion

    #region Lock-step and declaration refusals

    [Fact]
    public void An_alpha_plane_with_fewer_frames_than_its_picture_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        SyntheticMasterMedia.DeclareBoth(muxer, out int picture, out int alpha);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(1), TimeSpan.FromMilliseconds(40), TimeSpan.Zero, false);

        //Act
        Action complete = () => muxer.Complete();

        //Assert
        complete.Should().Throw<VideoPlaybackException>()
            .WithMessage("The alpha-plane track 2 has 1 frame(s) and its picture track 1 has 2*");
    }

    [Fact]
    public void An_alpha_plane_at_different_timestamps_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        SyntheticMasterMedia.DeclareBoth(muxer, out int picture, out int alpha);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(1), TimeSpan.FromMilliseconds(40), TimeSpan.Zero, false);
        muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(1), TimeSpan.FromMilliseconds(41), TimeSpan.Zero, false);

        //Act
        Action complete = () => muxer.Complete();

        //Assert
        complete.Should().Throw<VideoPlaybackException>()
            .WithMessage("Frame 1 of the picture track 1 is at 00:00:00.0400000*00:00:00.0410000*identical timestamps*");
    }

    [Fact]
    public void An_alpha_plane_with_key_frames_in_other_places_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        SyntheticMasterMedia.DeclareBoth(muxer, out int picture, out int alpha);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(1), TimeSpan.FromMilliseconds(40), TimeSpan.Zero, false);
        muxer.WriteChunk(alpha, SyntheticMasterMedia.MakeAlphaFrame(1), TimeSpan.FromMilliseconds(40), TimeSpan.Zero, true);

        //Act
        Action complete = () => muxer.Complete();

        //Assert
        complete.Should().Throw<VideoPlaybackException>()
            .WithMessage("Frame 1 at 00:00:00.0400000 is not a key frame in the picture track but is one in the alpha-plane track 2*");
    }

    [Fact]
    public void A_refused_file_writes_nothing_to_its_output()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using CbvMuxer muxer = new CbvMuxer(output, leaveOutputOpen: true);
        SyntheticMasterMedia.DeclareBoth(muxer, out int picture, out _);
        muxer.WriteChunk(picture, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);

        //Act
        Action complete = () => muxer.Complete();

        //Assert
        complete.Should().Throw<VideoPlaybackException>();
        output.Length.Should().Be(0);
    }

    [Fact]
    public void An_alpha_plane_of_another_size_is_refused_when_declared()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        DeclarePicture(muxer);

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 32, 18,
            bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("The alpha-plane track is 32x18 and its picture track 1 is 64x36*");
    }

    [Fact]
    public void An_alpha_plane_of_another_bit_depth_is_refused_when_declared()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        DeclarePicture(muxer);

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36,
            bitDepth: 10, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*10-bit*8-bit*");
    }

    [Fact]
    public void An_alpha_plane_that_is_not_monochrome_is_refused_when_declared()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        DeclarePicture(muxer);

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36,
            bitDepth: 8, layout: VideoPixelLayout.I420, color: SyntheticMasterMedia.Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*I420 layout*monochrome*");
    }

    [Fact]
    public void An_alpha_plane_in_limited_range_is_refused_when_declared()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        DeclarePicture(muxer);

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36,
            bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMedia.Video.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*Limited sample range*FULL range*");
    }

    [Fact]
    public void An_alpha_plane_with_no_picture_before_it_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36,
            bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("*picture track must be declared first*");
    }

    [Fact]
    public void A_second_alpha_plane_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        SyntheticMasterMedia.DeclareBoth(muxer, out _, out _);

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36,
            bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("Track 2 is already the alpha-plane track*");
    }

    [Fact]
    public void A_second_picture_beside_an_alpha_plane_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());
        SyntheticMasterMedia.DeclareBoth(muxer, out _, out _);

        //Act
        Action declare = () => DeclarePicture(muxer);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("Track 2 is an alpha-plane track*");
    }

    [Fact]
    public void Premultiplied_without_alpha_plane_is_refused()
    {
        //Arrange
        using CbvMuxer muxer = new CbvMuxer(new MemoryStream());

        //Act
        Action declare = () => muxer.AddVideoTrack(
            VideoCodecIds.Raw, ReadOnlyMemory<byte>.Empty, 64, 36, flags: CbvTrackFlags.PremultipliedAlpha);

        //Assert
        declare.Should().Throw<VideoPlaybackException>().WithMessage("PremultipliedAlpha describes an alpha-plane track*");
    }

    [Fact]
    public void A_file_whose_alpha_plane_drifted_is_refused_by_the_reader()
    {
        //Arrange - two ordinary picture tracks of different lengths, then the second one re-flagged as an
        // alpha plane behind the muxer's back.
        string path = SyntheticMedia.ScratchPath("cbv-master-drifted", "clip.cbvmaster");
        using (CbvMuxer muxer = CbvMuxer.Create(path))
        {
            RawVideoDescriptor video = SyntheticMedia.Video;
            int first = muxer.AddVideoTrack(VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(video), 64, 36,
                bitDepth: 8, layout: video.Layout, color: video.Color);
            int second = muxer.AddVideoTrack(VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(SyntheticMasterMedia.Alpha),
                64, 36, bitDepth: 8, layout: VideoPixelLayout.Gray, color: SyntheticMasterMedia.Alpha.Color);
            muxer.WriteChunk(first, SyntheticMedia.MakeFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
            muxer.WriteChunk(second, SyntheticMasterMedia.MakeAlphaFrame(0), TimeSpan.Zero, TimeSpan.Zero, true);
            muxer.WriteChunk(first, SyntheticMedia.MakeFrame(1), TimeSpan.FromMilliseconds(40), TimeSpan.Zero, true);
            muxer.Complete();
        }

        byte[] bytes = File.ReadAllBytes(path);
        bytes[FindTrackFlagsOffset(bytes, 2)] = (byte)CbvTrackFlags.AlphaPlane;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), CbvFormat.MasterVersion);
        RewriteHeaderChecksum(bytes);
        File.WriteAllBytes(path, bytes);

        //Act
        Action open = () => new CbvReader(new FileMediaSource(path)).Dispose();

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*cannot be played: The alpha-plane track 2 has 1 frame(s)*");
    }

    #endregion

    #region Seeking

    [Fact]
    public void A_seek_lands_on_the_same_key_frame_in_both_tracks()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-seek", "clip.cbvmaster"), frameCount: 30, keyFrameInterval: 10);
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromSeconds(0.5), 1);
        reader.TryReadPacket(out MediaPacket first);
        int firstTrack = first.TrackId;
        bool firstKey = first.IsKeyFrame;
        TimeSpan firstTime = first.Timestamp;
        reader.TryReadPacket(out MediaPacket second);

        //Assert
        landed.Should().Be(TimeSpan.FromSeconds(0.4));
        firstTrack.Should().Be(1);
        firstKey.Should().BeTrue();
        firstTime.Should().Be(landed);
        second.TrackId.Should().Be(2);
        second.IsKeyFrame.Should().BeTrue();
        second.Timestamp.Should().Be(landed);
    }

    [Fact]
    public void A_seek_backs_up_to_an_alpha_key_frame_stored_before_its_picture()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-seek-order", "clip.cbvmaster"),
            frameCount: 30,
            keyFrameInterval: 10,
            alphaChunkFirst: true);
        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromSeconds(0.5), 1);
        reader.TryReadPacket(out MediaPacket first);
        int firstTrack = first.TrackId;
        bool firstKey = first.IsKeyFrame;
        reader.TryReadPacket(out MediaPacket second);

        //Assert
        landed.Should().Be(TimeSpan.FromSeconds(0.4));
        firstTrack.Should().Be(2);
        firstKey.Should().BeTrue();
        second.TrackId.Should().Be(1);
        second.IsKeyFrame.Should().BeTrue();
        second.Timestamp.Should().Be(landed);
    }

    #endregion

    #region Authoring requests

    [Fact]
    public void An_authoring_request_with_two_audio_sources_is_refused()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-master-two-audio", "clip.cbvmaster"),
            AudioOggPath = TestAssets.Path("vorbis-audio.ogg"),
            PacketAudio = new CbvPacketAudioInput(VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 48000, 2),
        };

        //Act
        Action write = () => CbvAuthoring.Write(request);

        //Assert
        write.Should().Throw<ArgumentException>().WithMessage("*both as an Ogg file and as packets*");
    }

    [Fact]
    public void An_authoring_request_with_an_alpha_plane_and_no_picture_is_refused()
    {
        //Arrange
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-master-alpha-only", "clip.cbvmaster"),
            AlphaIvfPath = TestAssets.Path("av1-video-only.ivf"),
        };

        //Act
        Action write = () => CbvAuthoring.Write(request);

        //Assert
        write.Should().Throw<ArgumentException>().WithMessage("*alpha plane and no picture*");
    }

    [Fact]
    public void Packet_audio_alone_is_written_as_a_master_file()
    {
        //Arrange
        CbvPacketAudioInput audio = new CbvPacketAudioInput(
            VideoCodecIds.Flac, SyntheticMasterMedia.MakeFlacCodecPrivate(), 48000, 2);
        audio.Packets.Add(new CbvAudioPacket(SyntheticMasterMedia.MakeFlacPacket(0), TimeSpan.Zero, TimeSpan.FromMilliseconds(96)));
        audio.Packets.Add(new CbvAudioPacket(SyntheticMasterMedia.MakeFlacPacket(1), TimeSpan.FromMilliseconds(96), TimeSpan.FromMilliseconds(96)));
        CbvAuthoringRequest request = new CbvAuthoringRequest
        {
            OutputPath = SyntheticMedia.ScratchPath("cbv-master-packets-only", "clip.cbvmaster"),
            PacketAudio = audio,
            AudioLanguage = "en",
        };

        //Act
        CbvAuthoringResult result = CbvAuthoring.Write(request);

        //Assert
        result.FormatVersion.Should().Be(CbvFormat.MasterVersion);
        result.AudioPacketCount.Should().Be(2);
        result.AlphaTrackId.Should().Be(0);
        using CbvReader reader = new CbvReader(new FileMediaSource(result.Path));
        reader.Duration.Should().Be(TimeSpan.FromMilliseconds(192));
        reader.Tracks[0].Language.Should().Be("en");
    }

    #endregion

    #region Profile

    [Fact]
    public void A_master_file_with_flac_passes_the_streamable_profile_rules_it_can()
    {
        //Arrange
        string path = SyntheticMasterMedia.Write(
            SyntheticMedia.ScratchPath("cbv-master-profile", "clip.cbvmaster"), flacPackets: 8);

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        StreamableProfileRule audio = null;
        int recommendations = 0;
        foreach (StreamableProfileRule rule in report.Rules)
        {
            if (rule.Rule.StartsWith("audio codec", StringComparison.Ordinal)) audio = rule;
            if (rule.Rule.StartsWith("video is 8-bit", StringComparison.Ordinal)) recommendations++;
        }

        audio.Rule.Should().Be("audio codec is FLAC (master file)");
        audio.Outcome.Should().Be(StreamableProfileOutcome.Pass);
        recommendations.Should().Be(1);
    }

    #endregion

    private static void DeclarePicture(CbvMuxer muxer)
    {
        RawVideoDescriptor video = SyntheticMedia.Video;
        muxer.AddVideoTrack(
            VideoCodecIds.Raw, RawVideoFormat.CreateDescriptor(video), video.Width, video.Height,
            bitDepth: video.BitDepth, layout: video.Layout, color: video.Color);
    }

    private static void RewriteHeaderChecksum(byte[] bytes)
    {
        int headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CbvFormat.HeaderCrcOffset, 4), 0);
        uint crc = Crc32.Compute(bytes.AsSpan(0, headerLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CbvFormat.HeaderCrcOffset, 4), crc);
    }

    private static int FindTrackFlagsOffset(byte[] bytes, int trackId)
    {
        int position = CbvFormat.FixedHeaderLength;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
        position += 2;

        for (int i = 0; i < count; i++)
        {
            int entryLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
            int entry = position + 4;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(entry + 2 + CbvFormat.CodecIdFieldLength + CbvFormat.LanguageFieldLength, 2));
            int flags = entry + 2 + CbvFormat.CodecIdFieldLength + CbvFormat.LanguageFieldLength + 2 + nameLength;

            if (bytes[entry] == trackId) return flags;
            position = entry + entryLength;
        }

        throw new InvalidOperationException("No such track.");
    }
}
