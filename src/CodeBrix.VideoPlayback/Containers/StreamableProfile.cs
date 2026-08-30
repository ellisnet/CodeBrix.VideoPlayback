using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// The streamable-profile rules: what a file has to be for a player to open it, scrub it and draw it without
/// a second round trip, a second guess or a conversion nobody asked for.
/// </summary>
/// <remarks>
/// <para>
/// The profile is what makes a "CodeBrix Video" file a CodeBrix Video file rather than merely a WebM one, and
/// it is deliberately a small list of things that each buy something concrete:
/// </para>
/// <list type="bullet">
///   <item><description>
///     AV1 video with Opus or Vorbis audio and WebVTT captions, so a player needs one royalty-free decoder
///     pair and no others.
///   </description></item>
///   <item><description>
///     The seek index in FRONT of the media data, so a reader holding the first few kilobytes already holds
///     the whole index and the first scrub costs no extra read.
///   </description></item>
///   <item><description>
///     Every element a known size, so nothing has to be scanned to be skipped.
///   </description></item>
///   <item><description>
///     A stated duration, so a scrub bar can be drawn before a single frame is decoded.
///   </description></item>
///   <item><description>
///     Timestamps that ascend within every track, so nothing has to be re-ordered.
///   </description></item>
///   <item><description>
///     8-bit 4:2:0 samples - a RECOMMENDATION, not a requirement - because that is the shape every decoder
///     and every display path handles fastest.
///   </description></item>
/// </list>
/// <para>
/// The rules apply to both container flavours. Three of them are asked differently of a Matroska/WebM file
/// and of a bespoke <c>.cbv</c>, because the two containers state the same facts in different places; the
/// rest are asked of the tracks, which both containers describe the same way.
/// </para>
/// <para>
/// This is the one implementation of these rules. The <c>cbvinfo</c> tool prints from it and the authoring
/// library validates every file it writes with it, so a file that passes in one place passes in the other.
/// </para>
/// </remarks>
public static class StreamableProfile
{
    /// <summary>Opens a file, walks every packet in it, and evaluates the profile.</summary>
    /// <param name="path">A file path, a <c>file://</c> URL or an <c>http(s)://</c> URL.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="VideoPlaybackException">The file is not a container this library reads.</exception>
    /// <remarks>
    /// Nothing is decoded: the packets are walked for their timestamps only, so this runs with no codec
    /// package installed at all.
    /// </remarks>
    public static StreamableProfileReport EvaluateFile(string path)
    {
        using IMediaContainerReader reader = MediaContainers.Open(path);
        return Evaluate(reader, CountOutOfOrderPackets(reader));
    }

    /// <summary>
    /// Walks a reader to the end and counts the packets whose timestamp goes backwards within their own
    /// track.
    /// </summary>
    /// <param name="reader">The reader, which is left at the end of the file.</param>
    /// <returns>How many packets arrived out of order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader" /> is null.</exception>
    public static int CountOutOfOrderPackets(IMediaContainerReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));

        Dictionary<int, TimeSpan> last = new Dictionary<int, TimeSpan>();
        int outOfOrder = 0;

        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (last.TryGetValue(packet.TrackId, out TimeSpan previous) && packet.Timestamp < previous)
            {
                outOfOrder++;
            }

            last[packet.TrackId] = packet.Timestamp;
        }

        return outOfOrder;
    }

    /// <summary>Evaluates the profile over a reader whose packets have already been walked.</summary>
    /// <param name="reader">The reader, opened on the file being judged.</param>
    /// <param name="outOfOrderPacketCount">
    /// How many packets went backwards in time within their own track, from
    /// <see cref="CountOutOfOrderPackets" /> or from a caller that was counting as it read.
    /// </param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader" /> is null.</exception>
    public static StreamableProfileReport Evaluate(IMediaContainerReader reader, int outOfOrderPacketCount)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));

        List<StreamableProfileRule> rules = new List<StreamableProfileRule>(8);

        bool videoOk = true;
        bool audioOk = true;
        bool captionsOk = true;
        string videoDetail = "no video track";
        string audioDetail = "no audio track";

        foreach (MediaTrackInfo track in reader.Tracks)
        {
            switch (track.Kind)
            {
                case MediaTrackKind.Video:
                    videoDetail = "'" + track.CodecId + "'";
                    videoOk = string.Equals(track.CodecId, VideoCodecIds.Av1, StringComparison.OrdinalIgnoreCase);
                    break;

                case MediaTrackKind.Audio:
                    audioDetail = "'" + track.CodecId + "'";
                    audioOk = string.Equals(track.CodecId, VideoCodecIds.Opus, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(track.CodecId, VideoCodecIds.Vorbis, StringComparison.OrdinalIgnoreCase);
                    break;

                case MediaTrackKind.Caption:
                    if (!string.Equals(track.CodecId, VideoCodecIds.WebVtt, StringComparison.OrdinalIgnoreCase))
                    {
                        captionsOk = false;
                    }

                    break;
            }
        }

        Require(rules, "video codec is AV1", videoOk, videoDetail);
        Require(rules, "audio codec is Opus or Vorbis", audioOk, audioDetail);
        Require(rules, "caption tracks are WebVTT", captionsOk, reader.CaptionTracks.Count + " caption track(s)");

        if (reader is MatroskaReader matroska)
        {
            Require(
                rules,
                "cues sit before the first cluster",
                matroska.HasIndex && matroska.CuesPrecedeFirstCluster,
                matroska.HasIndex ? null : "the file carries no cues at all");

            Require(
                rules,
                "every element declares a known size",
                !matroska.HasUnknownSizeElements,
                matroska.HasUnknownSizeElements ? matroska.UnknownSizeElementCount + " unsized element(s)" : null);

            Require(rules, "the Info element states a duration", matroska.HasDeclaredDuration, null);
        }
        else if (reader is CbvReader cbv)
        {
            Require(rules, "the file carries an index", cbv.Index.Count > 0, null);
            Require(rules, "the index sits before the chunks", (cbv.Flags & CbvHeaderFlags.HasIndex) != 0, null);
            Require(rules, "the header states a duration", cbv.Duration > TimeSpan.Zero, null);
        }

        Require(
            rules,
            "timestamps ascend within every track",
            outOfOrderPacketCount == 0,
            outOfOrderPacketCount == 0 ? null : outOfOrderPacketCount + " packet(s) go backwards");

        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind != MediaTrackKind.Video) continue;

            bool eightBitFourTwoZero = track.BitDepth is 0 or 8
                && (track.Layout == VideoPixelLayout.I420 || track.Layout == VideoPixelLayout.Unknown);

            Recommend(
                rules,
                "video is 8-bit 4:2:0 (recommended)",
                eightBitFourTwoZero,
                eightBitFourTwoZero ? null : track.Layout + " at " + track.BitDepth + " bits");
        }

        return new StreamableProfileReport(rules);
    }

    private static void Require(List<StreamableProfileRule> rules, string rule, bool satisfied, string detail) =>
        rules.Add(new StreamableProfileRule(
            rule,
            satisfied ? StreamableProfileOutcome.Pass : StreamableProfileOutcome.Fail,
            detail));

    private static void Recommend(List<StreamableProfileRule> rules, string rule, bool satisfied, string detail) =>
        rules.Add(new StreamableProfileRule(
            rule,
            satisfied ? StreamableProfileOutcome.Pass : StreamableProfileOutcome.Warn,
            detail));
}
