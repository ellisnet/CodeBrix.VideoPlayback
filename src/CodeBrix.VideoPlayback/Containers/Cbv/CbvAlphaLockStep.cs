using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The one rule an alpha-plane track lives by: it is in lock step with its picture track - the same number of
/// chunks, at the same timestamps, with key frames in the same places.
/// </summary>
/// <remarks>
/// The muxer checks it before it writes a file and the reader checks it again when it opens one, from the
/// index alone, so a player never has to discover half-way through a file that the two tracks have drifted.
/// </remarks>
internal static class CbvAlphaLockStep
{
    /// <summary>Describes how an alpha-plane track breaks lock step with its picture track.</summary>
    /// <param name="entries">Every index entry of the file, in storage order.</param>
    /// <param name="pictureTrackId">The picture track.</param>
    /// <param name="alphaTrackId">The alpha-plane track.</param>
    /// <param name="toTime">Turns a timestamp in the file's timescale into a duration, for the message.</param>
    /// <returns>Null when the two tracks are in lock step; otherwise one precise sentence saying where not.</returns>
    internal static string Describe(
        IReadOnlyList<CbvIndexEntry> entries,
        int pictureTrackId,
        int alphaTrackId,
        Func<long, TimeSpan> toTime)
    {
        List<CbvIndexEntry> picture = new List<CbvIndexEntry>();
        List<CbvIndexEntry> alpha = new List<CbvIndexEntry>();

        foreach (CbvIndexEntry entry in entries)
        {
            if (entry.TrackId == pictureTrackId) picture.Add(entry);
            else if (entry.TrackId == alphaTrackId) alpha.Add(entry);
        }

        if (picture.Count != alpha.Count)
        {
            return $"The alpha-plane track {alphaTrackId} has {alpha.Count} frame(s) and its picture track "
                + $"{pictureTrackId} has {picture.Count}; an alpha-plane track must carry exactly one frame for every "
                + "picture frame.";
        }

        for (int i = 0; i < picture.Count; i++)
        {
            if (picture[i].TimestampTicks != alpha[i].TimestampTicks)
            {
                return $"Frame {i.ToString(CultureInfo.InvariantCulture)} of the picture track {pictureTrackId} is at "
                    + $"{toTime(picture[i].TimestampTicks)} and frame {i.ToString(CultureInfo.InvariantCulture)} of "
                    + $"the alpha-plane track {alphaTrackId} is at {toTime(alpha[i].TimestampTicks)}; the two tracks "
                    + "must carry their frames at identical timestamps.";
            }

            if (picture[i].IsKeyFrame != alpha[i].IsKeyFrame)
            {
                return $"Frame {i.ToString(CultureInfo.InvariantCulture)} at {toTime(picture[i].TimestampTicks)} is "
                    + (picture[i].IsKeyFrame ? "a key frame in the picture track and not in" : "not a key frame in the picture track but is one in")
                    + $" the alpha-plane track {alphaTrackId}; key frames must sit in the same places in both, so a "
                    + "seek can start both decoders at the same frame.";
            }
        }

        return null;
    }
}
