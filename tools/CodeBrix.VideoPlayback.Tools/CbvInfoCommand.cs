using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Tools;

/// <summary>
/// The <c>cbvinfo</c> verb: reads a media file's structure and prints it, then checks the file against the
/// streamable profile rules.
/// </summary>
/// <remarks>
/// It reads both container flavours - the bespoke <c>.cbv</c> and Matroska/WebM - and works with no codec
/// package installed at all, because nothing here is decoded.
/// </remarks>
public static class CbvInfoCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="args">The file to inspect, plus any switches.</param>
    /// <returns>0 when the file was read and passes the profile, 1 when it fails or cannot be read.</returns>
    public static int Run(string[] args)
    {
        string path = null;
        bool showCues = false;
        bool showPackets = false;
        bool verifyChecksums = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--cues":
                    showCues = true;
                    break;

                case "--packets":
                    showPackets = true;
                    break;

                case "--verify-checksums":
                    verifyChecksums = true;
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"cbvinfo: '{arg}' is not a switch this verb knows.");
                        return 2;
                    }

                    path = arg;
                    break;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("cbvinfo: a file to inspect is required.");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"cbvinfo: there is no file at '{path}'.");
            return 1;
        }

        using IMediaSource source = new FileMediaSource(path);
        using IMediaContainerReader reader = OpenReader(source, verifyChecksums);

        Console.WriteLine($"file        {Path.GetFullPath(path)}");
        Console.WriteLine($"size        {new FileInfo(path).Length:N0} bytes");
        Console.WriteLine($"format      {reader.FormatName}");
        Console.WriteLine($"duration    {Format(reader.Duration)}");
        Console.WriteLine($"seekable    {reader.CanSeek}");

        if (reader is MatroskaReader matroska) WriteMatroskaHeader(matroska);
        if (reader is CbvReader cbv) WriteCbvHeader(cbv);

        Console.WriteLine();
        WriteTracks(reader);
        WriteCaptions(reader);
        WriteChapters(reader);

        if (reader is CbvReader indexed) WriteCbvIndex(indexed, showCues);
        if (reader is MatroskaReader cued) WriteMatroskaCues(cued, showCues);

        PacketSummary summary = ReadPackets(reader, showPackets);
        WritePacketSummary(summary);

        if (reader.Notices.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("notices");
            foreach (string notice in reader.Notices) Console.WriteLine($"  - {notice}");
        }

        Console.WriteLine();
        return WriteProfileReport(reader, summary) ? 0 : 1;
    }

    private static IMediaContainerReader OpenReader(IMediaSource source, bool verifyChecksums)
    {
        // The library sniffs the header and picks the reader; the only thing this verb adds is the
        // checksum switch, which only the Matroska reader has.
        IMediaContainerReader reader = MediaContainers.Open(source, true);

        if (reader is MatroskaReader matroska) matroska.VerifyClusterChecksums = verifyChecksums;

        return reader;
    }

    private static void WriteMatroskaHeader(MatroskaReader reader)
    {
        Console.WriteLine($"doctype     {reader.DocType} (version {reader.DocTypeVersion}, read version {reader.DocTypeReadVersion})");
        Console.WriteLine($"timescale   {reader.TimestampScale} ns per tick");
        if (reader.Title.Length > 0) Console.WriteLine($"title       {reader.Title}");
        if (reader.MuxingApp.Length > 0) Console.WriteLine($"muxed by    {reader.MuxingApp}");
        if (reader.WritingApp.Length > 0) Console.WriteLine($"written by  {reader.WritingApp}");
        Console.WriteLine($"segment     data starts at {reader.SegmentDataOffset}, first cluster at {reader.FirstClusterOffset}");
        Console.WriteLine($"cues        {reader.Cues.Count} entries, before first cluster: {reader.CuesPrecedeFirstCluster}");
    }

    private static void WriteCbvHeader(CbvReader reader)
    {
        Console.WriteLine($"version     {reader.Version}");
        Console.WriteLine($"flags       {reader.Flags}");
        Console.WriteLine($"timescale   {reader.Timescale} ticks per second");
        Console.WriteLine($"index       {reader.Index.Count} entries");
        Console.WriteLine($"checksums   header verified: {reader.HeaderChecksumVerified}, index verified: {reader.IndexChecksumVerified}");
    }

    private static void WriteTracks(IMediaContainerReader reader)
    {
        Console.WriteLine($"tracks ({reader.Tracks.Count})");

        foreach (MediaTrackInfo track in reader.Tracks)
        {
            Console.WriteLine($"  [{track.Id}] {track.Kind.ToString().ToLowerInvariant()}  codec '{track.CodecId}'");

            if (track.Name.Length > 0) Console.WriteLine($"       name          {track.Name}");
            if (track.Language.Length > 0) Console.WriteLine($"       language      {track.Language}");

            Console.WriteLine(
                $"       flags         default {track.IsDefault}, forced {track.IsForced}, "
                + $"hearing-impaired {track.IsHearingImpaired}, enabled {track.IsEnabled}");

            if (!track.CodecPrivate.IsEmpty)
            {
                Console.WriteLine($"       codec data    {track.CodecPrivate.Length} bytes {Hex(track.CodecPrivate.Span, 16)}");
                WriteCodecPrivateDetail(track);
            }

            switch (track.Kind)
            {
                case MediaTrackKind.Video:
                    Console.WriteLine($"       size          {track.Width}x{track.Height} coded, {track.DisplayWidth}x{track.DisplayHeight} displayed");
                    Console.WriteLine($"       samples       {track.Layout}, {(track.BitDepth > 0 ? track.BitDepth + "-bit" : "bit depth not stated")}");
                    Console.WriteLine($"       colour        {track.Color}");
                    if (track.Hdr != null) Console.WriteLine($"       mastering     {track.Hdr}");
                    if (track.DefaultDuration > TimeSpan.Zero)
                    {
                        double fps = 1.0 / track.DefaultDuration.TotalSeconds;
                        Console.WriteLine($"       frame time    {Format(track.DefaultDuration)} ({fps.ToString("0.###", CultureInfo.InvariantCulture)} per second)");
                    }

                    break;

                case MediaTrackKind.Audio:
                    Console.WriteLine($"       audio         {track.SampleRate} Hz, {track.Channels} channel(s)");
                    if (track.CodecDelay > TimeSpan.Zero) Console.WriteLine($"       codec delay   {Format(track.CodecDelay)}");
                    if (track.SeekPreRoll > TimeSpan.Zero) Console.WriteLine($"       seek pre-roll {Format(track.SeekPreRoll)}");
                    if (track.PreSkipSamples > 0) Console.WriteLine($"       pre-skip      {track.PreSkipSamples} samples");
                    if (track.TrailingTrimSamples > 0) Console.WriteLine($"       trailing trim {track.TrailingTrimSamples} samples");

                    // Asked, never started: AudioDecoders forwards to the shared audio output's non-starting
                    // probe, so this line costs no audio device and this tool still runs on a machine that
                    // has none.
                    Console.WriteLine(
                        "       decoder available: "
                        + (AudioDecoders.IsCodecSupported(track.CodecId) ? "yes" : "no")
                        + " (via the shared audio output)");
                    break;

                case MediaTrackKind.Caption:
                    Console.WriteLine($"       captions      {track.CaptionFormat}");
                    break;
            }
        }
    }

    private static void WriteCodecPrivateDetail(MediaTrackInfo track)
    {
        if (string.Equals(track.CodecId, VideoCodecIds.Av1, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Av1SequenceHeader header = Av1Bitstream.ParseCodecConfigurationRecord(track.CodecPrivate.Span);
                Console.WriteLine($"       av1C          {header}");
                Console.WriteLine($"       av1C colour   {header.Color}{(header.ColorDescriptionPresent ? string.Empty : " (not stated by the stream)")}");
                if (header.FilmGrainParamsPresent) Console.WriteLine("       av1C          the stream may carry film grain");
            }
            catch (VideoPlaybackException ex)
            {
                Console.WriteLine($"       av1C          could not be read: {ex.Message}");
            }

            return;
        }

        if (string.Equals(track.CodecId, VideoCodecIds.Raw, StringComparison.OrdinalIgnoreCase))
        {
            if (RawVideoFormat.TryParseDescriptor(track.CodecPrivate.Span, out RawVideoDescriptor descriptor))
            {
                Console.WriteLine($"       descriptor    {descriptor}, {RawVideoFormat.GetFrameByteCount(descriptor):N0} bytes per frame");
            }
        }
    }

    private static void WriteCaptions(IMediaContainerReader reader)
    {
        if (reader.CaptionTracks.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"caption tracks ({reader.CaptionTracks.Count})");

        foreach (CaptionTrack track in reader.CaptionTracks)
        {
            Console.WriteLine(
                $"  [{track.Id}] {(track.Language.Length > 0 ? track.Language : "no language")}"
                + $"{(track.Name.Length > 0 ? " \"" + track.Name + "\"" : string.Empty)}, {track.Format}, {track.Flags}");
            Console.WriteLine($"       cues          {track.CueCount}{(track.AreCuesComplete ? " (complete)" : " (so far)")}");

            IReadOnlyList<CaptionCue> cues = track.Cues;
            int shown = Math.Min(3, cues.Count);
            for (int i = 0; i < shown; i++)
            {
                CaptionCue cue = cues[i];
                Console.WriteLine(
                    $"         {Format(cue.Start)} -> {Format(cue.End)}"
                    + $"{(cue.Identifier.Length > 0 ? " id=\"" + cue.Identifier + "\"" : string.Empty)}"
                    + $"{(cue.Settings.Length > 0 ? " settings=\"" + cue.Settings + "\"" : string.Empty)}");
                Console.WriteLine($"           {cue.Text.Replace("\n", " / ")}");
            }

            if (cues.Count > shown) Console.WriteLine($"         ... and {cues.Count - shown} more");
        }
    }

    private static void WriteChapters(IMediaContainerReader reader)
    {
        if (reader.Chapters.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"chapters ({reader.Chapters.Count})");

        foreach (Chapter chapter in reader.Chapters)
        {
            Console.WriteLine(
                $"  [{chapter.Index}] {Format(chapter.Start)} -> "
                + $"{(chapter.End > TimeSpan.Zero ? Format(chapter.End) : "next")}"
                + $"{(chapter.IsHidden ? " (hidden)" : string.Empty)}");

            foreach (KeyValuePair<string, string> title in chapter.Titles)
            {
                string language = title.Key.Length > 0 ? title.Key : "und";
                Console.WriteLine($"         {language}: {title.Value}");
            }
        }
    }

    private static void WriteCbvIndex(CbvReader reader, bool showEntries)
    {
        Console.WriteLine();
        Console.WriteLine($"index ({reader.Index.Count} entries)");

        Dictionary<int, int> perTrack = new Dictionary<int, int>();
        Dictionary<int, int> keyFrames = new Dictionary<int, int>();

        foreach (CbvIndexEntry entry in reader.Index)
        {
            perTrack.TryGetValue(entry.TrackId, out int count);
            perTrack[entry.TrackId] = count + 1;

            if (!entry.IsKeyFrame) continue;
            keyFrames.TryGetValue(entry.TrackId, out int keys);
            keyFrames[entry.TrackId] = keys + 1;
        }

        foreach (KeyValuePair<int, int> entry in perTrack)
        {
            keyFrames.TryGetValue(entry.Key, out int keys);
            Console.WriteLine($"  track {entry.Key}: {entry.Value} chunks, {keys} key frames");
        }

        if (!showEntries) return;

        foreach (CbvIndexEntry entry in reader.Index) Console.WriteLine($"    {entry}");
    }

    private static void WriteMatroskaCues(MatroskaReader reader, bool showEntries)
    {
        if (reader.Cues.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"cues ({reader.Cues.Count} entries)");

        if (!showEntries)
        {
            Console.WriteLine($"  first at {Format(reader.Cues[0].Time)}, last at {Format(reader.Cues[reader.Cues.Count - 1].Time)}");
            return;
        }

        foreach (MatroskaCuePoint cue in reader.Cues) Console.WriteLine($"    {cue}");
    }

    private static PacketSummary ReadPackets(IMediaContainerReader reader, bool listThem)
    {
        PacketSummary summary = new PacketSummary();

        while (reader.TryReadPacket(out MediaPacket packet))
        {
            summary.Add(packet);

            if (!listThem) continue;
            Console.WriteLine(
                $"    packet track {packet.TrackId} at {Format(packet.Timestamp)}, {packet.Data.Length} bytes"
                + $"{(packet.IsKeyFrame ? ", key" : string.Empty)}");
        }

        return summary;
    }

    private static void WritePacketSummary(PacketSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine($"packets ({summary.Total})");

        foreach (KeyValuePair<int, TrackStatistics> entry in summary.PerTrack)
        {
            TrackStatistics stats = entry.Value;
            Console.WriteLine(
                $"  track {entry.Key}: {stats.Count} packets, {stats.KeyFrames} key, {stats.Bytes:N0} bytes, "
                + $"{Format(stats.First)} .. {Format(stats.Last)}"
                + $"{(stats.OutOfOrder > 0 ? $", {stats.OutOfOrder} out of order" : string.Empty)}");
        }
    }

    // The RULES are not here: they live in the library, as CodeBrix.VideoPlayback.Containers.StreamableProfile,
    // so that this verb and the authoring library judge a file by one implementation rather than two. All this
    // does is print what the report already renders.
    private static bool WriteProfileReport(IMediaContainerReader reader, PacketSummary summary)
    {
        StreamableProfileReport report = StreamableProfile.Evaluate(reader, summary.OutOfOrder);
        Console.WriteLine(report.ToString());
        return report.Passes;
    }

    private static string Format(TimeSpan value) =>
        value.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> data, int limit)
    {
        int count = Math.Min(limit, data.Length);
        System.Text.StringBuilder builder = new System.Text.StringBuilder(count * 3 + 4);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (data.Length > count) builder.Append(" ...");
        return builder.ToString();
    }

    private sealed class TrackStatistics
    {
        internal int Count;

        internal int KeyFrames;

        internal long Bytes;

        internal TimeSpan First = TimeSpan.MinValue;

        internal TimeSpan Last = TimeSpan.MinValue;

        internal int OutOfOrder;
    }

    private sealed class PacketSummary
    {
        internal int Total { get; private set; }

        internal int OutOfOrder { get; private set; }

        internal Dictionary<int, TrackStatistics> PerTrack { get; } = new Dictionary<int, TrackStatistics>();

        internal void Add(MediaPacket packet)
        {
            Total++;

            if (!PerTrack.TryGetValue(packet.TrackId, out TrackStatistics stats))
            {
                stats = new TrackStatistics();
                PerTrack[packet.TrackId] = stats;
            }

            stats.Count++;
            stats.Bytes += packet.Data.Length;
            if (packet.IsKeyFrame) stats.KeyFrames++;
            if (stats.First == TimeSpan.MinValue) stats.First = packet.Timestamp;

            if (stats.Last != TimeSpan.MinValue && packet.Timestamp < stats.Last)
            {
                stats.OutOfOrder++;
                OutOfOrder++;
            }

            stats.Last = packet.Timestamp;
        }
    }
}
