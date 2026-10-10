using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// What an AV1 IVF file's frames are, as far as lock step cares: how many, when, and which are key frames.
/// </summary>
/// <remarks>
/// A master file's alpha plane is encoded AFTER its picture, with its key frames forced onto the picture's,
/// and the two are compared again before muxing. Key frames are read the way the muxer reads them - from the
/// AV1 bitstream, not from anything the encoder claims - so what is compared here is what the file will say.
/// </remarks>
internal sealed class IvfKeyFrames
{
    private IvfKeyFrames(string path, List<TimeSpan> timestamps, List<bool> keyFrames, TimeSpan frameDuration)
    {
        Path = path;
        Timestamps = timestamps;
        KeyFrames = keyFrames;
        FrameDuration = frameDuration;
    }

    /// <summary>The file that was read.</summary>
    internal string Path { get; }

    /// <summary>Every frame's timestamp, in file order.</summary>
    internal IReadOnlyList<TimeSpan> Timestamps { get; }

    /// <summary>Whether each frame is a key frame, in file order.</summary>
    internal IReadOnlyList<bool> KeyFrames { get; }

    /// <summary>The IVF time base - one frame's duration at a constant rate.</summary>
    internal TimeSpan FrameDuration { get; }

    /// <summary>Reads an AV1 IVF file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its frames.</returns>
    internal static IvfKeyFrames Read(string path)
    {
        using IvfReader reader = new IvfReader(new FileMediaSource(path));

        List<TimeSpan> timestamps = new List<TimeSpan>();
        List<bool> keyFrames = new List<bool>();
        Av1SequenceHeader sequenceHeader = null;

        while (reader.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out _))
        {
            if (sequenceHeader == null
                && Av1Bitstream.TryReadSequenceHeader(data.Span, out Av1SequenceHeader header, out _, out _))
            {
                sequenceHeader = header;
            }

            timestamps.Add(timestamp);
            keyFrames.Add(Av1Bitstream.IsKeyFrame(data.Span, sequenceHeader));
        }

        return new IvfKeyFrames(path, timestamps, keyFrames, reader.TimeBase);
    }

    /// <summary>
    /// The key frames as an FFmpeg <c>-force_key_frames</c> list: each key frame's time less half a frame, so
    /// the frame FFmpeg picks - the first at or after each time - is exactly that key frame, whatever rounding
    /// its own time base adds.
    /// </summary>
    /// <returns>A comma-separated list of times in seconds, invariant culture.</returns>
    internal string FormatForceKeyFrames()
    {
        StringBuilder list = new StringBuilder();
        TimeSpan half = TimeSpan.FromTicks(FrameDuration.Ticks / 2);

        for (int i = 0; i < KeyFrames.Count; i++)
        {
            if (!KeyFrames[i]) continue;

            TimeSpan time = Timestamps[i] - half;
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;

            if (list.Length > 0) list.Append(',');
            list.Append(time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }

        return list.Length == 0 ? "0" : list.ToString();
    }

    /// <summary>Says how another stream fails to be in lock step with this one.</summary>
    /// <param name="other">The alpha plane.</param>
    /// <returns>Null when the two agree frame for frame; otherwise one sentence saying where they do not.</returns>
    internal string DescribeMismatch(IvfKeyFrames other)
    {
        if (other.Timestamps.Count != Timestamps.Count)
        {
            return "the video pass wrote " + Count(Timestamps.Count) + " frame(s) and the alpha pass "
                + Count(other.Timestamps.Count) + ".";
        }

        for (int i = 0; i < Timestamps.Count; i++)
        {
            if (Timestamps[i] != other.Timestamps[i])
            {
                return "frame " + Count(i) + " is at " + Timestamps[i] + " in the video pass and at "
                    + other.Timestamps[i] + " in the alpha pass.";
            }

            if (KeyFrames[i] != other.KeyFrames[i])
            {
                return "frame " + Count(i) + " (at " + Timestamps[i] + ") is "
                    + (KeyFrames[i] ? "a key frame in the video pass and not in the alpha pass."
                                    : "a key frame in the alpha pass and not in the video pass.");
            }
        }

        return null;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
