using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.RawCodec;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Decodes the uncompressed frames of a bespoke ".cbv" file straight into a list, with no clock, no threads
/// and no playback session - so a rendering test can hold real frames still and look at them.
/// </summary>
public static class CbvFrameSource
{
    /// <summary>Decodes the first frames of a file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="pool">The pool the frames' buffers come from.</param>
    /// <param name="count">How many frames to decode at most.</param>
    /// <returns>The frames, which the caller owns and must dispose.</returns>
    public static List<VideoFrame> Decode(string path, PinnedFrameBufferPool pool, int count)
    {
        List<VideoFrame> frames = new List<VideoFrame>(count);

        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        MediaTrackInfo video = null;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind != MediaTrackKind.Video) continue;
            video = track;
            break;
        }

        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };

        using IVideoDecoder decoder = new RawVideoDecoderFactory()
            .CreateDecoder(video.CodecId, video.CodecPrivate, options);

        while (frames.Count < count && reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != video.Id) continue;

            decoder.SendPacket(new VideoPacket(
                packet.Data.ToArray(),
                packet.Timestamp,
                packet.IsKeyFrame,
                packet.Duration,
                frames.Count));

            while (frames.Count < count && decoder.TryReceiveFrame(out VideoFrame frame)) frames.Add(frame);
        }

        return frames;
    }
}
