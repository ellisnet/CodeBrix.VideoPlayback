using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Builds master (Mode3) bespoke files out of uncompressed frames: a picture track, an optional alpha-plane
/// track and an optional FLAC track, so the container, the lock-step rules and the session's alpha pairing can
/// be exercised with no codec involved.
/// </summary>
/// <remarks>
/// The picture frames are <see cref="SyntheticMedia.MakeFrame" />'s, so a decoded frame names its own number in
/// its luma. The alpha plane is a horizontal gradient whose value ALSO carries the frame number - sample
/// <c>x</c> of frame <c>n</c> holds <see cref="AlphaSample" /> - so a test can check that each picture frame was
/// paired with ITS alpha frame and not a neighbour's. The FLAC packets are byte patterns, not audio: the
/// container never decodes them, and the round-trip tests compare them byte for byte.
/// </remarks>
public static class SyntheticMasterMedia
{
    /// <summary>The alpha plane's shape: the picture's size, monochrome, full range.</summary>
    public static RawVideoDescriptor Alpha { get; } = new RawVideoDescriptor(
        SyntheticMedia.Video.Width,
        SyntheticMedia.Video.Height,
        8,
        VideoPixelLayout.Gray,
        new VideoColorInfo(
            VideoColorPrimaries.Unspecified,
            VideoTransferCharacteristics.Unspecified,
            VideoMatrixCoefficients.Unspecified,
            VideoColorRange.Full,
            VideoChromaSiting.Unknown));

    /// <summary>The FLAC track's sample rate.</summary>
    public const int FlacSampleRate = 48000;

    /// <summary>The FLAC track's channel count.</summary>
    public const int FlacChannels = 2;

    /// <summary>The FLAC track's samples per packet.</summary>
    public const int FlacBlockSize = 4608;

    /// <summary>The alpha value of column <paramref name="x" /> in frame <paramref name="frameNumber" />.</summary>
    /// <param name="frameNumber">The frame.</param>
    /// <param name="x">The column.</param>
    /// <returns>A sample 0 to 255.</returns>
    public static byte AlphaSample(int frameNumber, int x) => (byte)(((x * 4) + (frameNumber * 3)) % 256);

    /// <summary>Makes one alpha frame.</summary>
    /// <param name="frameNumber">The frame.</param>
    /// <returns>Width x height samples.</returns>
    public static byte[] MakeAlphaFrame(int frameNumber)
    {
        byte[] plane = new byte[Alpha.Width * Alpha.Height];
        for (int y = 0; y < Alpha.Height; y++)
        {
            for (int x = 0; x < Alpha.Width; x++) plane[(y * Alpha.Width) + x] = AlphaSample(frameNumber, x);
        }

        return plane;
    }

    /// <summary>
    /// A FLAC stream header: <c>fLaC</c>, a STREAMINFO block and a short comment block, last flag on the comment.
    /// </summary>
    /// <param name="sampleRate">The rate STREAMINFO states.</param>
    /// <param name="channels">The channel count STREAMINFO states.</param>
    /// <returns>The codec-private bytes.</returns>
    public static byte[] MakeFlacCodecPrivate(int sampleRate = FlacSampleRate, int channels = FlacChannels)
    {
        byte[] header = new byte[4 + 4 + 34 + 4 + 8];
        "fLaC"u8.CopyTo(header);
        header[4] = 0x00;
        header[7] = 34;

        Span<byte> info = header.AsSpan(8, 34);
        BinaryPrimitives.WriteUInt16BigEndian(info.Slice(0, 2), FlacBlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(info.Slice(2, 2), FlacBlockSize);
        ulong packed = ((ulong)sampleRate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(16 - 1) << 36) | 96000UL;
        BinaryPrimitives.WriteUInt64BigEndian(info.Slice(10, 8), packed);

        header[42] = 0x84;
        header[45] = 8;
        "CodeBrix"u8.CopyTo(header.AsSpan(46, 8));
        return header;
    }

    /// <summary>A FLAC "packet": a byte pattern that names its own index.</summary>
    /// <param name="index">The packet's index.</param>
    /// <returns>The bytes.</returns>
    public static byte[] MakeFlacPacket(int index)
    {
        byte[] packet = new byte[40 + (index % 7)];
        packet[0] = 0xFF;
        packet[1] = 0xF8;
        for (int i = 2; i < packet.Length; i++) packet[i] = (byte)((index * 31) + i);
        return packet;
    }

    /// <summary>Writes a master file.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="frameCount">How many picture frames (and, with alpha, alpha frames).</param>
    /// <param name="frameRate">Frames per second.</param>
    /// <param name="keyFrameInterval">How often a frame is marked as a key frame, in both video tracks.</param>
    /// <param name="withAlpha">True to write an alpha-plane track.</param>
    /// <param name="flacPackets">How many FLAC packets to write, or 0 for no audio track.</param>
    /// <param name="alphaChunkFirst">
    /// True to store each alpha chunk BEFORE the picture chunk that shares its timestamp - legal, and the order
    /// that makes a seek have to back up to find the alpha key frame.
    /// </param>
    /// <returns>The path written.</returns>
    public static string Write(
        string path,
        int frameCount = 30,
        double frameRate = 25.0,
        int keyFrameInterval = 10,
        bool withAlpha = true,
        int flacPackets = 0,
        bool alphaChunkFirst = false)
    {
        TimeSpan frameDuration = TimeSpan.FromSeconds(1.0 / frameRate);
        RawVideoDescriptor video = SyntheticMedia.Video;

        using CbvMuxer muxer = CbvMuxer.Create(path);

        int videoTrack = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(video),
            video.Width,
            video.Height,
            video.Width,
            video.Height,
            video.BitDepth,
            video.Layout,
            video.Color,
            null,
            frameDuration,
            "en",
            "synthetic picture");

        int alphaTrack = 0;
        if (withAlpha)
        {
            alphaTrack = muxer.AddVideoTrack(
                VideoCodecIds.Raw,
                RawVideoFormat.CreateDescriptor(Alpha),
                Alpha.Width,
                Alpha.Height,
                Alpha.Width,
                Alpha.Height,
                Alpha.BitDepth,
                Alpha.Layout,
                Alpha.Color,
                null,
                frameDuration,
                null,
                "synthetic alpha",
                CbvTrackFlags.AlphaPlane);
        }

        int audioTrack = 0;
        if (flacPackets > 0)
        {
            audioTrack = muxer.AddAudioTrack(
                VideoCodecIds.Flac,
                MakeFlacCodecPrivate(),
                FlacSampleRate,
                FlacChannels,
                language: "en",
                name: "synthetic flac");
        }

        TimeSpan packetDuration = TimeSpan.FromTicks((long)FlacBlockSize * TimeSpan.TicksPerSecond / FlacSampleRate);
        int audioIndex = 0;

        for (int i = 0; i < frameCount; i++)
        {
            TimeSpan timestamp = TimeSpan.FromTicks(frameDuration.Ticks * i);
            bool key = keyFrameInterval <= 1 || i % keyFrameInterval == 0;

            while (audioIndex < flacPackets && TimeSpan.FromTicks(packetDuration.Ticks * audioIndex) <= timestamp)
            {
                muxer.WriteChunk(
                    audioTrack,
                    MakeFlacPacket(audioIndex),
                    TimeSpan.FromTicks(packetDuration.Ticks * audioIndex),
                    packetDuration,
                    true);
                audioIndex++;
            }

            if (withAlpha && alphaChunkFirst) muxer.WriteChunk(alphaTrack, MakeAlphaFrame(i), timestamp, frameDuration, key);
            muxer.WriteChunk(videoTrack, SyntheticMedia.MakeFrame(i), timestamp, frameDuration, key);
            if (withAlpha && !alphaChunkFirst) muxer.WriteChunk(alphaTrack, MakeAlphaFrame(i), timestamp, frameDuration, key);
        }

        while (audioIndex < flacPackets)
        {
            muxer.WriteChunk(
                audioTrack,
                MakeFlacPacket(audioIndex),
                TimeSpan.FromTicks(packetDuration.Ticks * audioIndex),
                packetDuration,
                true);
            audioIndex++;
        }

        muxer.Complete();
        return path;
    }

    /// <summary>Declares a picture track and an alpha-plane track on a muxer, for the refusal tests.</summary>
    /// <param name="muxer">The muxer.</param>
    /// <param name="pictureTrack">The picture track's identifier.</param>
    /// <param name="alphaTrack">The alpha track's identifier.</param>
    public static void DeclareBoth(CbvMuxer muxer, out int pictureTrack, out int alphaTrack)
    {
        RawVideoDescriptor video = SyntheticMedia.Video;
        pictureTrack = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(video),
            video.Width,
            video.Height,
            bitDepth: video.BitDepth,
            layout: video.Layout,
            color: video.Color);

        alphaTrack = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(Alpha),
            Alpha.Width,
            Alpha.Height,
            bitDepth: Alpha.BitDepth,
            layout: Alpha.Layout,
            color: Alpha.Color,
            flags: CbvTrackFlags.AlphaPlane);
    }

    /// <summary>Collects every packet of a reader, copied.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>Track, timestamp, key flag and bytes of each packet, in file order.</returns>
    public static List<(int Track, TimeSpan Timestamp, bool Key, byte[] Data)> ReadAll(CbvReader reader)
    {
        List<(int, TimeSpan, bool, byte[])> packets = new List<(int, TimeSpan, bool, byte[])>();
        while (reader.TryReadPacket(out Containers.MediaPacket packet))
        {
            packets.Add((packet.TrackId, packet.Timestamp, packet.IsKeyFrame, packet.Data.ToArray()));
        }

        return packets;
    }
}
