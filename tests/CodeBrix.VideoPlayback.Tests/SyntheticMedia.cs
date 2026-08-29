using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Builds bespoke container files out of uncompressed frames, so the session, the clock, the seek logic and
/// the reader can be exercised end to end with no codec involved.
/// </summary>
/// <remarks>
/// Every frame is filled with a value derived from its own number, so a test can look at a decoded frame and
/// say exactly which one it is - which is what makes an exact seek checkable.
/// </remarks>
public static class SyntheticMedia
{
    /// <summary>The frame shape these files use: small enough to be quick, odd enough to catch edge cases.</summary>
    public static RawVideoDescriptor Video { get; } = new RawVideoDescriptor(
        64,
        36,
        8,
        VideoPixelLayout.I420,
        new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited,
            VideoChromaSiting.Vertical));

    /// <summary>Fills a packet with a pattern that identifies the frame it belongs to.</summary>
    /// <param name="frameNumber">The frame's number, which decides the pattern.</param>
    /// <returns>One frame's worth of planar samples.</returns>
    public static byte[] MakeFrame(int frameNumber)
    {
        byte[] frame = new byte[RawVideoFormat.GetFrameByteCount(Video)];
        byte luma = (byte)(16 + (frameNumber % 220));

        int offset = 0;
        for (int plane = 0; plane < 3; plane++)
        {
            int width = RawVideoFormat.GetPlaneWidth(Video, plane);
            int height = RawVideoFormat.GetPlaneHeight(Video, plane);
            byte value = plane == 0 ? luma : (byte)(128 + (plane == 1 ? frameNumber % 40 : -(frameNumber % 40)));

            for (int i = 0; i < width * height; i++) frame[offset + i] = value;
            offset += width * height;
        }

        return frame;
    }

    /// <summary>Reads back the frame number a packet made by <see cref="MakeFrame" /> was built with.</summary>
    /// <param name="lumaSample">The value of any luma sample in the decoded frame.</param>
    /// <returns>The frame number modulo 220.</returns>
    public static int FrameNumberFromLuma(byte lumaSample) => lumaSample - 16;

    /// <summary>Writes a bespoke file made of uncompressed frames, with whatever extras a test asks for.</summary>
    /// <param name="path">Where to write the file.</param>
    /// <param name="frameCount">How many video frames to write.</param>
    /// <param name="frameRate">How many frames a second the file claims.</param>
    /// <param name="keyFrameInterval">
    /// How often a frame is marked as a key frame. Every uncompressed frame really is one, but marking only
    /// some of them is what makes key-frame-only seeking testable.
    /// </param>
    /// <param name="audioOggPath">An Ogg file whose packets become the audio track, or null for no audio.</param>
    /// <param name="captions">Caption tracks to store in the header, or null for none.</param>
    /// <param name="chapters">Chapters to store in the header, or null for none.</param>
    /// <returns>The path that was written.</returns>
    public static string WriteRawCbv(
        string path,
        int frameCount = 60,
        double frameRate = 25.0,
        int keyFrameInterval = 10,
        string audioOggPath = null,
        IReadOnlyList<CaptionTrack> captions = null,
        IReadOnlyList<Chapter> chapters = null)
    {
        TimeSpan frameDuration = TimeSpan.FromSeconds(1.0 / frameRate);

        using CbvMuxer muxer = CbvMuxer.Create(path);

        int videoTrack = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(Video),
            Video.Width,
            Video.Height,
            Video.Width,
            Video.Height,
            Video.BitDepth,
            Video.Layout,
            Video.Color,
            null,
            frameDuration,
            "en",
            "synthetic video");

        int audioTrack = 0;
        List<OggAudioPacket> audioPackets = new List<OggAudioPacket>();
        OggAudioStream audio = null;

        try
        {
            if (audioOggPath != null)
            {
                audio = OggAudioStream.Open(audioOggPath);
                foreach (OggAudioPacket packet in audio.ReadAllPackets())
                {
                    audioPackets.Add(new OggAudioPacket(
                        packet.Data.ToArray(),
                        packet.Timestamp,
                        packet.Duration,
                        packet.SampleCount));
                }

                audioTrack = muxer.AddAudioTrack(
                    audio.CodecId,
                    audio.CodecPrivate,
                    audio.SampleRate,
                    audio.Channels,
                    audio.PreSkipSamples,
                    audio.TrailingTrimSamples,
                    TimeSpan.Zero,
                    string.Equals(audio.CodecId, VideoCodecIds.Opus, StringComparison.Ordinal)
                        ? TimeSpan.FromMilliseconds(80)
                        : TimeSpan.Zero,
                    "en",
                    "synthetic audio");
            }
        }
        finally
        {
            audio?.Dispose();
        }

        if (captions != null)
        {
            foreach (CaptionTrack track in captions) muxer.AddCaptionTrack(track);
        }

        if (chapters != null) muxer.AddChapters(chapters);

        int audioIndex = 0;
        for (int i = 0; i < frameCount; i++)
        {
            TimeSpan timestamp = TimeSpan.FromTicks(frameDuration.Ticks * i);

            while (audioIndex < audioPackets.Count && audioPackets[audioIndex].Timestamp <= timestamp)
            {
                OggAudioPacket packet = audioPackets[audioIndex++];
                muxer.WriteChunk(audioTrack, packet.Data.Span, packet.Timestamp, packet.Duration, true);
            }

            muxer.WriteChunk(
                videoTrack,
                MakeFrame(i),
                timestamp,
                frameDuration,
                keyFrameInterval <= 1 || i % keyFrameInterval == 0);
        }

        while (audioIndex < audioPackets.Count)
        {
            OggAudioPacket packet = audioPackets[audioIndex++];
            muxer.WriteChunk(audioTrack, packet.Data.Span, packet.Timestamp, packet.Duration, true);
        }

        muxer.Complete();
        return path;
    }

    /// <summary>Builds a caption track in code, for a test that wants known cues.</summary>
    /// <param name="id">The track's identifier.</param>
    /// <param name="language">The BCP 47 language tag.</param>
    /// <param name="flags">What the track is for.</param>
    /// <param name="cues">The cues, as start, end and text triples.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    public static CaptionTrack MakeCaptionTrack(
        int id,
        string language,
        CaptionTrackFlags flags,
        params (double Start, double End, string Text)[] cues)
    {
        string vtt = "WEBVTT\n\n";
        foreach ((double start, double end, string text) in cues)
        {
            vtt += $"{CaptionFiles.FormatWebVttTime(TimeSpan.FromSeconds(start))} --> "
                + $"{CaptionFiles.FormatWebVttTime(TimeSpan.FromSeconds(end))}\n{text}\n\n";
        }

        return CaptionFiles.ParseWebVtt(vtt, id, language, "captions", flags);
    }

    /// <summary>Builds a small chapter list in code.</summary>
    /// <param name="boundaries">The start time of each chapter, in seconds.</param>
    /// <returns>The chapters, each titled after its index.</returns>
    public static IReadOnlyList<Chapter> MakeChapters(params double[] boundaries)
    {
        List<Chapter> chapters = new List<Chapter>();
        for (int i = 0; i < boundaries.Length; i++)
        {
            TimeSpan start = TimeSpan.FromSeconds(boundaries[i]);
            TimeSpan end = i + 1 < boundaries.Length ? TimeSpan.FromSeconds(boundaries[i + 1]) : TimeSpan.Zero;

            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = $"Chapter {i + 1}",
                ["fr"] = $"Chapitre {i + 1}",
            };

            chapters.Add(new Chapter(i, start, end, false, titles));
        }

        return chapters;
    }

    /// <summary>Creates a scratch folder and returns a path inside it for a file a test is about to write.</summary>
    /// <param name="label">A short label that appears in the folder's name.</param>
    /// <param name="fileName">The file's name.</param>
    /// <returns>The full path to write to.</returns>
    public static string ScratchPath(string label, string fileName) =>
        Path.Combine(TestAssets.CreateTemporaryDirectory(label), fileName);
}
