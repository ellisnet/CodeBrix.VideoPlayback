using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Decodes EVERY frame of a master file's picture and alpha plane with the AV1 decoder package, directly through
/// the reader and the decoder - not through a playing session, which shows the newest frame and may skip - so two
/// authored files can be compared frame for frame.
/// </summary>
public static class MasterFrameDecoder
{
    /// <summary>Decodes a master file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="alpha">The alpha plane's frames (Gray, in <see cref="ShapedFrames.Snapshot.Y" />), or null without one.</param>
    /// <returns>The picture's frames, in order.</returns>
    public static List<ShapedFrames.Snapshot> Decode(string path, out List<ShapedFrames.Snapshot> alpha)
    {
        using CbvReader reader = new CbvReader(new FileMediaSource(path));
        MediaTrackInfo colourTrack = null;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Video && !track.IsAlphaPlane)
            {
                colourTrack = track;
                break;
            }
        }

        MediaTrackInfo alphaTrack = reader.AlphaPlaneTrack;
        Dav1dDecoderFactory factory = new Dav1dDecoderFactory();
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        List<ShapedFrames.Snapshot> colour = new List<ShapedFrames.Snapshot>();
        alpha = alphaTrack != null ? new List<ShapedFrames.Snapshot>() : null;

        using IVideoDecoder colourDecoder = factory.CreateDecoder(colourTrack.CodecId, colourTrack.CodecPrivate, new VideoDecoderOptions { BufferPool = pool });
        using IVideoDecoder alphaDecoder = alphaTrack != null
            ? factory.CreateDecoder(alphaTrack.CodecId, alphaTrack.CodecPrivate, new VideoDecoderOptions { BufferPool = pool })
            : null;

        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == colourTrack.Id)
            {
                Send(colourDecoder, packet, colour);
            }
            else if (alphaTrack != null && packet.TrackId == alphaTrack.Id)
            {
                Send(alphaDecoder, packet, alpha);
            }
        }

        Drain(colourDecoder, colour);
        if (alphaDecoder != null) Drain(alphaDecoder, alpha);
        return colour;
    }

    /// <summary>Counts the distinct luma values over a run of frames.</summary>
    /// <param name="frames">The frames.</param>
    /// <returns>How many different values appear.</returns>
    public static int DistinctLuma(IEnumerable<ShapedFrames.Snapshot> frames)
    {
        HashSet<int> values = new HashSet<int>();
        foreach (ShapedFrames.Snapshot frame in frames)
        {
            foreach (int value in frame.Y) values.Add(value);
        }

        return values.Count;
    }

    private static void Send(IVideoDecoder decoder, MediaPacket packet, List<ShapedFrames.Snapshot> frames)
    {
        // The reader reuses its packet buffer, so the bytes are copied before they are handed on.
        decoder.SendPacket(new VideoPacket(packet.Data.ToArray(), packet.Timestamp, packet.IsKeyFrame));
        Receive(decoder, frames);
    }

    private static void Drain(IVideoDecoder decoder, List<ShapedFrames.Snapshot> frames)
    {
        decoder.Drain();
        Receive(decoder, frames);
    }

    private static void Receive(IVideoDecoder decoder, List<ShapedFrames.Snapshot> frames)
    {
        while (decoder.TryReceiveFrame(out VideoFrame frame))
        {
            using (frame)
            {
                frames.Add(new ShapedFrames.Snapshot
                {
                    Timestamp = frame.Timestamp,
                    Index = frames.Count,
                    Width = frame.Width,
                    Height = frame.Height,
                    BitDepth = frame.BitDepth,
                    Y = Values(frame.Y),
                    U = frame.U.IsEmpty ? null : Values(frame.U),
                    V = frame.V.IsEmpty ? null : Values(frame.V),
                });
            }
        }
    }

    private static int[] Values(VideoFramePlane plane)
    {
        int[] values = new int[plane.Width * plane.Height];
        for (int row = 0; row < plane.Height; row++)
        {
            ReadOnlySpan<byte> data = plane.GetRowBytes(row);
            for (int x = 0; x < plane.Width; x++)
            {
                values[(row * plane.Width) + x] = plane.BytesPerSample == 1
                    ? data[x]
                    : data[2 * x] | (data[(2 * x) + 1] << 8);
            }
        }

        return values;
    }
}
