using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// Builds a bespoke <c>.cbv</c> file out of the files an encoder produced: an IVF video stream, an Ogg audio
/// stream, caption files and a chapter file.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole authoring path in one call. It reads the video's sequence header to build the codec
/// configuration record a player needs, reads the audio's setup headers to build its initialisation data,
/// works out every packet's timestamp, interleaves the two streams in presentation order, and writes the
/// index in front of the result.
/// </para>
/// <para>
/// It needs nothing installed. The IVF and Ogg files come from whatever encoder made them; everything after
/// that is this library.
/// </para>
/// <para>
/// A MASTER (Mode3) file adds a second IVF - the picture's alpha channel as a monochrome AV1 stream, declared
/// as an alpha-plane track - and may carry its audio as ready-made packets (a FLAC stream split into frames)
/// instead of an Ogg file. The muxer checks the alpha track against the picture and refuses a pair that is not
/// in lock step.
/// </para>
/// </remarks>
public static class CbvAuthoring
{
    /// <summary>Writes a bespoke container from the request's inputs.</summary>
    /// <param name="request">What to put in the file and where to write it.</param>
    /// <returns>A summary of what was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="ArgumentException">The request states no output path, or no inputs at all.</exception>
    /// <exception cref="VideoPlaybackException">An input file is malformed or carries something unsupported.</exception>
    public static CbvAuthoringResult Write(CbvAuthoringRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ArgumentException("The request must state where to write the file.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.AudioOggPath) && request.PacketAudio != null)
        {
            throw new ArgumentException(
                "The request supplies audio both as an Ogg file and as packets; a file carries one audio track.",
                nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.AlphaIvfPath) && string.IsNullOrWhiteSpace(request.VideoIvfPath))
        {
            throw new ArgumentException(
                "The request supplies an alpha plane and no picture; the alpha plane is the alpha channel OF the "
                + "picture in VideoIvfPath.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.VideoIvfPath)
            && string.IsNullOrWhiteSpace(request.AudioOggPath)
            && request.PacketAudio == null)
        {
            throw new ArgumentException(
                "The request must supply video, audio, or both; a container with neither is not a media file.",
                nameof(request));
        }

        using CbvMuxer muxer = CbvMuxer.Create(request.OutputPath);

        int videoTrackId = 0;
        int alphaTrackId = 0;
        int audioTrackId = 0;
        int videoFrames = 0;
        int alphaFrames = 0;
        int audioPackets = 0;
        int captionCues = 0;

        List<TimedChunk> chunks = new List<TimedChunk>();

        IvfReader video = null;
        IvfReader alpha = null;
        OggAudioStream audio = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(request.VideoIvfPath))
            {
                video = new IvfReader(new FileMediaSource(request.VideoIvfPath));
                videoTrackId = AddVideoTrack(
                    muxer, video, request.VideoIvfPath, request.VideoName, CbvTrackFlags.None, chunks, out videoFrames);
            }

            if (!string.IsNullOrWhiteSpace(request.AlphaIvfPath))
            {
                alpha = new IvfReader(new FileMediaSource(request.AlphaIvfPath));
                alphaTrackId = AddVideoTrack(
                    muxer, alpha, request.AlphaIvfPath, null, CbvTrackFlags.AlphaPlane, chunks, out alphaFrames);
            }

            if (request.PacketAudio != null)
            {
                audioTrackId = AddPacketAudioTrack(muxer, request.PacketAudio, request, chunks, out audioPackets);
            }

            if (!string.IsNullOrWhiteSpace(request.AudioOggPath))
            {
                audio = OggAudioStream.Open(request.AudioOggPath);
                audioTrackId = AddAudioTrack(muxer, audio, request, chunks, out audioPackets);
            }
        }
        finally
        {
            video?.Dispose();
            alpha?.Dispose();
            audio?.Dispose();
        }

        int captionTrackIndex = 0;
        foreach (CbvCaptionInput caption in request.Captions)
        {
            CaptionTrack track = CaptionFiles.ReadFile(
                caption.Path,
                captionTrackIndex++,
                caption.Language,
                caption.Name,
                caption.Flags);

            captionCues += track.CueCount;
            muxer.AddCaptionTrack(track);
        }

        if (!string.IsNullOrWhiteSpace(request.ChaptersPath))
        {
            muxer.AddChapters(FfMetadataChapters.ReadFile(request.ChaptersPath));
        }

        chunks.Sort(CompareChunks);

        foreach (TimedChunk chunk in chunks)
        {
            muxer.WriteChunk(chunk.TrackId, chunk.Data, chunk.Timestamp, chunk.Duration, chunk.IsKeyFrame);
        }

        ushort version = muxer.FormatVersion;
        muxer.Complete();

        return new CbvAuthoringResult(
            request.OutputPath,
            new FileInfo(request.OutputPath).Length,
            videoTrackId,
            alphaTrackId,
            audioTrackId,
            videoFrames,
            alphaFrames,
            audioPackets,
            request.Captions.Count,
            captionCues,
            version);
    }

    private static int AddVideoTrack(
        CbvMuxer muxer,
        IvfReader video,
        string ivfPath,
        string trackName,
        CbvTrackFlags flags,
        List<TimedChunk> chunks,
        out int frameCount)
    {
        if (!string.Equals(video.FourCharacterCode, "AV01", StringComparison.OrdinalIgnoreCase))
        {
            throw new VideoPlaybackException(
                $"'{ivfPath}' is an IVF file carrying '{video.FourCharacterCode}'. This muxer writes "
                + "AV1 video, whose four-character code is 'AV01'.");
        }

        List<TimedChunk> frames = new List<TimedChunk>();
        Av1SequenceHeader sequenceHeader = null;
        byte[] codecPrivate = null;

        while (video.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out _))
        {
            byte[] copy = data.ToArray();

            if (codecPrivate == null
                && Av1Bitstream.TryReadSequenceHeader(copy, out Av1SequenceHeader header, out int start, out int length))
            {
                sequenceHeader = header;
                codecPrivate = Av1Bitstream.BuildCodecConfigurationRecord(header, copy.AsSpan(start, length));
            }

            frames.Add(new TimedChunk(0, copy, timestamp, video.TimeBase, Av1Bitstream.IsKeyFrame(copy, sequenceHeader)));
        }

        frameCount = frames.Count;

        if (codecPrivate == null)
        {
            throw new VideoPlaybackException(
                $"'{ivfPath}' carries no AV1 sequence header, so no codec configuration record can be "
                + "built for it. The first frame of an elementary stream must carry one.");
        }

        int trackId = muxer.AddVideoTrack(
            VideoCodecIds.Av1,
            codecPrivate,
            sequenceHeader.MaxFrameWidth,
            sequenceHeader.MaxFrameHeight,
            video.Width > 0 ? video.Width : sequenceHeader.MaxFrameWidth,
            video.Height > 0 ? video.Height : sequenceHeader.MaxFrameHeight,
            sequenceHeader.BitDepth,
            sequenceHeader.Layout,
            sequenceHeader.Color,
            null,
            video.TimeBase,
            null,
            trackName,
            flags);

        foreach (TimedChunk frame in frames) chunks.Add(frame.WithTrack(trackId));
        return trackId;
    }

    private static int AddAudioTrack(
        CbvMuxer muxer,
        OggAudioStream audio,
        CbvAuthoringRequest request,
        List<TimedChunk> chunks,
        out int packetCount)
    {
        TimeSpan codecDelay = audio.PreSkipSamples > 0 && audio.SampleRate > 0
            ? TimeSpan.FromTicks((long)audio.PreSkipSamples * TimeSpan.TicksPerSecond / audio.SampleRate)
            : TimeSpan.Zero;

        TimeSpan seekPreRoll = string.Equals(audio.CodecId, VideoCodecIds.Opus, StringComparison.Ordinal)
            ? TimeSpan.FromMilliseconds(80)
            : TimeSpan.Zero;

        List<TimedChunk> packets = new List<TimedChunk>();
        while (audio.TryReadPacket(out OggAudioPacket packet))
        {
            packets.Add(new TimedChunk(0, packet.Data.ToArray(), packet.Timestamp, packet.Duration, true));
        }

        packetCount = packets.Count;

        int trackId = muxer.AddAudioTrack(
            audio.CodecId,
            audio.CodecPrivate,
            audio.SampleRate,
            audio.Channels,
            audio.PreSkipSamples,
            audio.TrailingTrimSamples,
            codecDelay,
            seekPreRoll,
            request.AudioLanguage,
            request.AudioName);

        foreach (TimedChunk packet in packets) chunks.Add(packet.WithTrack(trackId));
        return trackId;
    }

    private static int AddPacketAudioTrack(
        CbvMuxer muxer,
        CbvPacketAudioInput audio,
        CbvAuthoringRequest request,
        List<TimedChunk> chunks,
        out int packetCount)
    {
        int trackId = muxer.AddAudioTrack(
            audio.CodecId,
            audio.CodecPrivate,
            audio.SampleRate,
            audio.Channels,
            language: request.AudioLanguage,
            name: request.AudioName);

        packetCount = audio.Packets.Count;
        foreach (CbvAudioPacket packet in audio.Packets)
        {
            chunks.Add(new TimedChunk(trackId, packet.Data, packet.Timestamp, packet.Duration, true));
        }

        return trackId;
    }

    private static int CompareChunks(TimedChunk left, TimedChunk right)
    {
        int byTime = left.Timestamp.CompareTo(right.Timestamp);
        if (byTime != 0) return byTime;
        return left.TrackId.CompareTo(right.TrackId);
    }

    private readonly struct TimedChunk
    {
        internal TimedChunk(int trackId, byte[] data, TimeSpan timestamp, TimeSpan duration, bool isKeyFrame)
        {
            TrackId = trackId;
            Data = data;
            Timestamp = timestamp;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        internal int TrackId { get; }

        internal byte[] Data { get; }

        internal TimeSpan Timestamp { get; }

        internal TimeSpan Duration { get; }

        internal bool IsKeyFrame { get; }

        internal TimedChunk WithTrack(int trackId) =>
            new TimedChunk(trackId, Data, Timestamp, Duration, IsKeyFrame);
    }
}
