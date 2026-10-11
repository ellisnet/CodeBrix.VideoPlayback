using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// Writes WebM and Matroska files - the container of a CodeBrix Video Mode1 file - out of the files an encoder
/// produced: an IVF file of AV1 video, an Ogg Opus or Ogg Vorbis file (or a list of audio packets), WebVTT or
/// SubRip caption files and an FFmpeg metadata chapter file.
/// </summary>
/// <remarks>
/// <para>
/// Pure managed code with nothing installed: everything an external muxer would do - the codec configuration
/// record from the AV1 sequence header, the Opus delay, the caption block layout, the index - is done here, so a
/// device with an encoder and this library can author a streamable WebM file on its own.
/// </para>
/// <para>
/// <b>The layout.</b> An EBML header (<c>webm</c> or <c>matroska</c>, document type version 4, read version
/// 2), then ONE Segment of known size holding, in order: a <c>SeekHead</c> naming every other top-level
/// element, <c>Info</c> (a one-millisecond timestamp scale, the title, the muxing and writing application -
/// both <see cref="ApplicationName" /> - and the duration), <c>Tracks</c>, <c>Chapters</c> when there are any,
/// <c>Cues</c>, and the clusters. With <see cref="MatroskaAuthoringRequest.CuesToFront" /> false the
/// <c>Cues</c> move to the end instead. Every element states its size; nothing is ever written with an unknown
/// size and no <c>Void</c> padding is needed, because the layout is computed before it is written.
/// </para>
/// <para>
/// <b>Clusters</b> begin at every video key frame - except that the audio in front of the very first frame shares
/// that frame's cluster rather than getting one of its own - and also, at whatever block comes next, once a
/// cluster spans <see cref="MaximumClusterMilliseconds" /> or holds <see cref="MaximumClusterBytes" />, which are
/// the limits the WebM guidelines recommend. A file without video therefore has a cluster every five seconds.
/// Blocks are in timestamp order across tracks; at the same millisecond the video frame comes first, so a cluster
/// that starts at a key frame really starts with it.
/// </para>
/// <para>
/// <b>The index</b> holds one <c>CuePoint</c> for every video key frame (for every cluster's first audio block in
/// a file with no video), and - as FFmpeg writes it - a <c>CueTrackPositions</c> for every caption block, with
/// its <c>CueDuration</c>, merged into the <c>CuePoint</c> of the same time when there is one.
/// </para>
/// <para>
/// <b>Two passes.</b> The first pass lays the blocks into clusters and writes the clusters to a temporary file
/// beside the output, remembering where each cluster and each indexed block landed; a cluster's bytes depend on
/// nothing outside it. The second pass works out the metadata, then the index - whose size depends on the
/// positions it holds and whose positions, when it is in front, depend on its own size, which is settled by
/// recomputing until it stops changing - and writes the finished file in one forward sweep, copying the clusters
/// across unchanged. Track and chapter identifiers are their numbers and nothing is timestamped, so the same
/// inputs always produce the same bytes.
/// </para>
/// </remarks>
public static class MatroskaMuxer
{
    /// <summary>The name written as both <c>MuxingApp</c> and <c>WritingApp</c>.</summary>
    public const string ApplicationName = "CodeBrix.VideoPlayback";

    /// <summary>The longest span a cluster is allowed before the next block starts a new one, in milliseconds.</summary>
    public const long MaximumClusterMilliseconds = 5000;

    /// <summary>The most bytes a cluster holds before the next block starts a new one.</summary>
    public const int MaximumClusterBytes = 5 * 1024 * 1024;

    /// <summary>The <c>SeekPreRoll</c> written on an Opus track: 80 milliseconds, in nanoseconds.</summary>
    public const long OpusSeekPreRollNanoseconds = 80_000_000;

    private const long TimestampScaleNanoseconds = 1_000_000;
    private const int SeekPositionWidth = 8;
    private const int SegmentSizeWidth = 8;

    /// <summary>Writes a WebM or Matroska file from the request's inputs.</summary>
    /// <param name="request">What to put in the file and where to write it.</param>
    /// <returns>A summary of what was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="ArgumentException">The request states no output path, no media at all, or audio twice.</exception>
    /// <exception cref="FileNotFoundException">An input file does not exist.</exception>
    /// <exception cref="VideoPlaybackException">
    /// An input is malformed, carries something this muxer does not write, or does not fit the media - a
    /// caption or chapter that starts after the media ends, chapters that overlap. Nothing is left at the output
    /// path when this is thrown.
    /// </exception>
    public static MatroskaAuthoringResult Write(MatroskaAuthoringRequest request)
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

        if (string.IsNullOrWhiteSpace(request.VideoIvfPath)
            && string.IsNullOrWhiteSpace(request.AudioOggPath)
            && request.PacketAudio == null)
        {
            throw new ArgumentException(
                "The request must supply video, audio, or both; a container with neither is not a media file.",
                nameof(request));
        }

        RequireFile(request.VideoIvfPath, "video");
        RequireFile(request.AudioOggPath, "audio");
        RequireFile(request.ChaptersPath, "chapter");
        foreach (MatroskaCaptionInput caption in request.Captions)
        {
            if (caption == null) throw new ArgumentException("The request's caption list holds a null entry.", nameof(request));
            RequireFile(caption.Path, "caption");
        }

        List<string> notices = new List<string>();
        List<MatroskaMuxTrack> tracks = new List<MatroskaMuxTrack>();
        List<MatroskaBlockSource> sources = new List<MatroskaBlockSource>();
        List<MatroskaCaptionSource> captionSources = new List<MatroskaCaptionSource>();
        string tempPath = request.OutputPath + ".pass1.tmp";
        bool outputStarted = false;

        try
        {
            MatroskaMuxTrack video = null;
            MatroskaMuxTrack audio = null;

            if (!string.IsNullOrWhiteSpace(request.VideoIvfPath))
            {
                video = NewTrack(tracks, MediaTrackKind.Video, "V_AV1", request.VideoName, request.VideoLanguage);
                sources.Add(new MatroskaVideoSource(request.VideoIvfPath, video));
            }

            if (!string.IsNullOrWhiteSpace(request.AudioOggPath))
            {
                audio = NewTrack(tracks, MediaTrackKind.Audio, string.Empty, request.AudioName, request.AudioLanguage);
                sources.Add(new MatroskaOggAudioSource(request.AudioOggPath, audio));
            }
            else if (request.PacketAudio != null)
            {
                audio = NewTrack(tracks, MediaTrackKind.Audio, string.Empty, request.AudioName, request.AudioLanguage);
                DescribePacketAudio(request.PacketAudio, audio);
                sources.Add(new MatroskaPacketAudioSource(request.PacketAudio, audio));
            }

            foreach (MatroskaCaptionInput input in request.Captions)
            {
                bool hearingImpaired = (input.Flags & CaptionTrackFlags.HearingImpaired) != 0;
                MatroskaMuxTrack track = NewTrack(
                    tracks,
                    MediaTrackKind.Caption,
                    hearingImpaired ? "D_WEBVTT/CAPTIONS" : "D_WEBVTT/SUBTITLES",
                    input.Name,
                    input.Language);

                track.IsDefault = (input.Flags & CaptionTrackFlags.Default) != 0;
                track.IsForced = (input.Flags & CaptionTrackFlags.Forced) != 0;
                track.IsHearingImpaired = hearingImpaired;

                CaptionTrack parsed = CaptionFiles.ReadFile(input.Path, track.Number, input.Language, input.Name, input.Flags);
                MatroskaCaptionSource source = new MatroskaCaptionSource(parsed, track, input.Path);
                captionSources.Add(source);
                sources.Add(source);
            }

            IReadOnlyList<Chapter> chapters = string.IsNullOrWhiteSpace(request.ChaptersPath)
                ? Array.Empty<Chapter>()
                : FfMetadataChapters.ReadFile(request.ChaptersPath);
            CheckChapterOrder(chapters, request.ChaptersPath);

            CollectLanguageNotices(tracks, request.DocType, notices);

            // ---- first pass: the clusters, to a temporary file.
            MatroskaClusterWriter clusters;
            using (FileStream temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                clusters = new MatroskaClusterWriter(temp, video ?? audio);
                Interleave(sources, clusters);
                clusters.Flush();
            }

            TimeSpan duration = TimeSpan.Zero;
            if (video != null) duration = Max(duration, FindSource(sources, video).PresentationEnd);
            if (audio != null) duration = Max(duration, FindSource(sources, audio).PresentationEnd);

            foreach (MatroskaCaptionSource source in captionSources) CheckCaptionsFit(source, duration);
            CheckChaptersFit(chapters, request.ChaptersPath, duration);

            // ---- second pass: the metadata, the index, and the finished file.
            byte[] ebmlHeader = BuildEbmlHeader(request.DocType);
            // A request with no title of its own takes the chapter file's file-level title=, as FFmpeg does when
            // the chapter file is the metadata source (-map_metadata), so a Mode1 file keeps the same title
            // whichever of the two wrote it.
            string title = request.Title;
            if (title == null && !string.IsNullOrWhiteSpace(request.ChaptersPath))
            {
                title = FfMetadataChapters.ReadGlobalTitle(request.ChaptersPath);
            }

            byte[] info = BuildInfo(title, duration);
            byte[] trackElement = BuildTracks(tracks, request.DocType);
            byte[] chapterElement = chapters.Count == 0 ? Array.Empty<byte>() : BuildChapters(chapters, duration, request.DocType, notices);

            int seekEntries = chapterElement.Length > 0 ? 4 : 3;
            int seekHeadLength = (int)EbmlWriter.MeasureElement(MatroskaIds.SeekHead, seekEntries * SeekEntryLength);

            long infoPosition = seekHeadLength;
            long tracksPosition = infoPosition + info.Length;
            long chaptersPosition = tracksPosition + trackElement.Length;
            long metadataEnd = chaptersPosition + chapterElement.Length;

            byte[] cues = BuildCuesSettled(clusters, metadataEnd, request.CuesToFront, out long clustersPosition, out int cuePointCount);
            long cuesPosition = request.CuesToFront ? metadataEnd : clustersPosition + clusters.ClusterBytes;

            byte[] seekHead = BuildSeekHead(infoPosition, tracksPosition, chapterElement.Length > 0 ? chaptersPosition : -1, cuesPosition);
            if (seekHead.Length != seekHeadLength)
            {
                throw new InvalidOperationException("The seek head came out a different size from the one it was laid out for.");
            }

            long segmentLength = metadataEnd + cues.Length + clusters.ClusterBytes;

            outputStarted = true;
            using (FileStream output = new FileStream(request.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(ebmlHeader);

                EbmlWriter segmentHeader = new EbmlWriter(16);
                segmentHeader.WriteHeader(MatroskaIds.Segment, segmentLength, SegmentSizeWidth);
                segmentHeader.CopyTo(output);

                output.Write(seekHead);
                output.Write(info);
                output.Write(trackElement);
                output.Write(chapterElement);

                if (request.CuesToFront) output.Write(cues);

                using (FileStream temp = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    temp.CopyTo(output);
                }

                if (!request.CuesToFront) output.Write(cues);
            }

            List<MatroskaAuthoredTrack> summary = new List<MatroskaAuthoredTrack>(tracks.Count);
            foreach (MatroskaMuxTrack track in tracks)
            {
                summary.Add(new MatroskaAuthoredTrack(
                    track.Number, track.Kind, track.CodecId, track.Name, track.LanguageCode, track.BlockCount));
            }

            StreamableProfileReport report = request.EvaluateProfile
                ? StreamableProfile.EvaluateFile(request.OutputPath)
                : null;

            return new MatroskaAuthoringResult(
                request.OutputPath,
                new FileInfo(request.OutputPath).Length,
                request.DocType,
                duration,
                summary,
                chapters.Count,
                cuePointCount,
                clusters.Clusters.Count,
                request.CuesToFront,
                notices,
                report);
        }
        catch
        {
            if (outputStarted) TryDelete(request.OutputPath);
            throw;
        }
        finally
        {
            foreach (MatroskaBlockSource source in sources) source.Dispose();
            TryDelete(tempPath);
        }
    }

    // ------------------------------------------------------------------ inputs

    private static void RequireFile(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no {what} file at '{path}'.", path);
    }

    private static MatroskaMuxTrack NewTrack(
        List<MatroskaMuxTrack> tracks,
        MediaTrackKind kind,
        string codecId,
        string name,
        string language)
    {
        MatroskaMuxTrack track = new MatroskaMuxTrack
        {
            Number = tracks.Count + 1,
            Kind = kind,
            CodecId = codecId,
            Name = name ?? string.Empty,
        };

        track.SetLanguage(language);
        tracks.Add(track);
        return track;
    }

    private static void DescribePacketAudio(MatroskaPacketAudioInput input, MatroskaMuxTrack track)
    {
        ReadOnlySpan<byte> codecPrivate = input.CodecPrivate.Span;

        if (string.Equals(input.CodecId, VideoCodecIds.Opus, StringComparison.OrdinalIgnoreCase))
        {
            if (codecPrivate.Length < 19 || !codecPrivate.Slice(0, 8).SequenceEqual("OpusHead"u8))
            {
                throw new VideoPlaybackException(
                    "An Opus track's codec-private data must be the OpusHead identification header (at least 19 "
                    + $"bytes, beginning 'OpusHead'); this one is {codecPrivate.Length} byte(s) that are not one.");
            }

            int channels = codecPrivate[9];
            if (channels != input.Channels)
            {
                throw new VideoPlaybackException(
                    $"The packet audio input declares {input.Channels} channel(s) and its OpusHead says {channels}; "
                    + "the track header and the stream must agree.");
            }

            track.CodecId = "A_OPUS";
            track.SampleRate = OpusPacketDuration.SampleRate;
            track.Channels = channels;
            track.CodecDelayNs = MatroskaOggAudioSource.SamplesToNanoseconds(
                BinaryPrimitives.ReadUInt16LittleEndian(codecPrivate.Slice(10, 2)),
                OpusPacketDuration.SampleRate);
            track.SeekPreRollNs = OpusSeekPreRollNanoseconds;
        }
        else if (string.Equals(input.CodecId, VideoCodecIds.Vorbis, StringComparison.OrdinalIgnoreCase))
        {
            // Throws with a precise message when the block is not three Xiph-laced headers.
            OggAudioStream.SplitXiphCodecPrivate(codecPrivate, out _, out _, out _);

            track.CodecId = "A_VORBIS";
            track.SampleRate = input.SampleRate;
            track.Channels = input.Channels;
        }
        else
        {
            throw new VideoPlaybackException(
                $"The packet audio input is '{input.CodecId}'. This muxer writes Opus or Vorbis audio, the two codecs "
                + "the streamable profile allows.");
        }

        track.CodecPrivate = codecPrivate.ToArray();
    }

    private static void CollectLanguageNotices(List<MatroskaMuxTrack> tracks, MatroskaDocType docType, List<string> notices)
    {
        foreach (MatroskaMuxTrack track in tracks)
        {
            if (track.LanguageIsExact || track.LanguageTag.Length == 0) continue;

            notices.Add(docType == MatroskaDocType.Matroska
                ? $"Track {track.Number}'s language '{track.LanguageTag}' is written as '{track.LanguageCode}' and, in "
                  + "full, as a BCP 47 tag beside it."
                : $"Track {track.Number}'s language '{track.LanguageTag}' is written as '{track.LanguageCode}'; a webm "
                  + "document has no element for the full tag, so the rest of it is not carried.");
        }
    }

    private static void CheckChapterOrder(IReadOnlyList<Chapter> chapters, string path)
    {
        for (int i = 0; i < chapters.Count; i++)
        {
            Chapter chapter = chapters[i];

            if (chapter.End > TimeSpan.Zero && chapter.End <= chapter.Start)
            {
                throw new VideoPlaybackException(
                    $"Chapter {i + 1} in '{path}' starts at {chapter.Start} and ends at {chapter.End}; a chapter must "
                    + "end after it starts.");
            }

            if (i == 0) continue;

            Chapter previous = chapters[i - 1];
            if (chapter.Start == previous.Start)
            {
                throw new VideoPlaybackException(
                    $"Chapters {i} and {i + 1} in '{path}' both start at {chapter.Start}; chapters must follow one "
                    + "another, not share a start.");
            }

            if (previous.End > chapter.Start)
            {
                throw new VideoPlaybackException(
                    $"Chapter {i} in '{path}' ends at {previous.End}, after chapter {i + 1} starts at {chapter.Start}; "
                    + "chapters must be in order and must not overlap.");
            }
        }
    }

    private static void CheckChaptersFit(IReadOnlyList<Chapter> chapters, string path, TimeSpan duration)
    {
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Start >= duration)
            {
                throw new VideoPlaybackException(
                    $"Chapter {i + 1} in '{path}' starts at {chapters[i].Start}, at or after the end of the media "
                    + $"({duration}); a chapter must start inside the media it describes.");
            }
        }
    }

    private static void CheckCaptionsFit(MatroskaCaptionSource source, TimeSpan duration)
    {
        foreach (CaptionCue cue in source.Cues)
        {
            if (cue.Start >= duration)
            {
                throw new VideoPlaybackException(
                    $"'{source.Path}' has a cue starting at {cue.Start}, at or after the end of the media ({duration}); "
                    + "a caption must start inside the media it belongs to.");
            }
        }
    }

    // ------------------------------------------------------------------ first pass

    private static void Interleave(List<MatroskaBlockSource> sources, MatroskaClusterWriter clusters)
    {
        while (true)
        {
            MatroskaBlockSource next = null;
            foreach (MatroskaBlockSource source in sources)
            {
                MatroskaPendingBlock block = source.Current;
                if (block == null) continue;

                if (next == null || Precedes(block, next.Current)) next = source;
            }

            if (next == null) return;

            clusters.Add(next.Current);
            next.MoveNext();
        }
    }

    private static bool Precedes(MatroskaPendingBlock candidate, MatroskaPendingBlock current)
    {
        if (candidate.TimestampMs != current.TimestampMs) return candidate.TimestampMs < current.TimestampMs;
        if (candidate.Track.KindOrder != current.Track.KindOrder) return candidate.Track.KindOrder < current.Track.KindOrder;
        return candidate.Track.Number < current.Track.Number;
    }

    private static MatroskaBlockSource FindSource(List<MatroskaBlockSource> sources, MatroskaMuxTrack track)
    {
        foreach (MatroskaBlockSource source in sources)
        {
            if (ReferenceEquals(source.Track, track)) return source;
        }

        throw new InvalidOperationException("A track has no source.");
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    // ------------------------------------------------------------------ second pass

    private static byte[] BuildEbmlHeader(MatroskaDocType docType)
    {
        EbmlWriter children = new EbmlWriter(64);
        children.WriteUnsigned(EbmlIds.EbmlVersion, 1);
        children.WriteUnsigned(EbmlIds.EbmlReadVersion, 1);
        children.WriteUnsigned(EbmlIds.EbmlMaxIdLength, 4);
        children.WriteUnsigned(EbmlIds.EbmlMaxSizeLength, 8);
        children.WriteAscii(EbmlIds.DocType, docType == MatroskaDocType.WebM ? "webm" : "matroska");
        children.WriteUnsigned(EbmlIds.DocTypeVersion, 4);
        children.WriteUnsigned(EbmlIds.DocTypeReadVersion, 2);

        EbmlWriter header = new EbmlWriter(64);
        header.WriteMaster(EbmlIds.EbmlHeader, children);
        return header.ToArray();
    }

    private static byte[] BuildInfo(string title, TimeSpan duration)
    {
        EbmlWriter children = new EbmlWriter(128);
        children.WriteUnsigned(MatroskaIds.TimestampScale, TimestampScaleNanoseconds);
        if (!string.IsNullOrEmpty(title)) children.WriteUtf8(MatroskaIds.Title, title);
        children.WriteUtf8(MatroskaIds.MuxingApp, ApplicationName);
        children.WriteUtf8(MatroskaIds.WritingApp, ApplicationName);
        children.WriteFloat(MatroskaIds.Duration, duration.Ticks / (double)TimeSpan.TicksPerMillisecond);

        EbmlWriter info = new EbmlWriter(160);
        info.WriteMaster(MatroskaIds.Info, children);
        return info.ToArray();
    }

    private static byte[] BuildTracks(List<MatroskaMuxTrack> tracks, MatroskaDocType docType)
    {
        EbmlWriter entries = new EbmlWriter(1024);

        foreach (MatroskaMuxTrack track in tracks)
        {
            EbmlWriter entry = new EbmlWriter(256);
            entry.WriteUnsigned(MatroskaIds.TrackNumber, (ulong)track.Number);
            entry.WriteUnsigned(MatroskaIds.TrackUid, (ulong)track.Number);
            entry.WriteUnsigned(MatroskaIds.FlagLacing, 0);
            if (track.Name.Length > 0) entry.WriteUtf8(MatroskaIds.Name, track.Name);
            entry.WriteAscii(MatroskaIds.Language, track.LanguageCode);
            if (docType == MatroskaDocType.Matroska && !track.LanguageIsExact && track.LanguageTag.Length > 0)
            {
                entry.WriteAscii(MatroskaIds.LanguageBcp47, track.LanguageTag);
            }

            if (!track.IsDefault) entry.WriteUnsigned(MatroskaIds.FlagDefault, 0);
            if (track.IsForced) entry.WriteUnsigned(MatroskaIds.FlagForced, 1);
            if (track.IsHearingImpaired) entry.WriteUnsigned(MatroskaIds.FlagHearingImpaired, 1);
            entry.WriteAscii(MatroskaIds.CodecId, track.CodecId);
            if (track.CodecDelayNs > 0) entry.WriteUnsigned(MatroskaIds.CodecDelay, (ulong)track.CodecDelayNs);
            if (track.SeekPreRollNs > 0) entry.WriteUnsigned(MatroskaIds.SeekPreRoll, (ulong)track.SeekPreRollNs);
            entry.WriteUnsigned(MatroskaIds.TrackType, (ulong)track.TrackType);
            if (track.DefaultDurationNs > 0) entry.WriteUnsigned(MatroskaIds.DefaultDuration, (ulong)track.DefaultDurationNs);

            if (track.Kind == MediaTrackKind.Video) entry.WriteMaster(MatroskaIds.Video, BuildVideoSettings(track));

            if (track.Kind == MediaTrackKind.Audio)
            {
                EbmlWriter audio = new EbmlWriter(32);
                audio.WriteUnsigned(MatroskaIds.Channels, (ulong)track.Channels);
                audio.WriteFloat(MatroskaIds.SamplingFrequency, track.SampleRate);
                entry.WriteMaster(MatroskaIds.Audio, audio);
            }

            if (track.CodecPrivate.Length > 0) entry.WriteBinary(MatroskaIds.CodecPrivate, track.CodecPrivate);

            entries.WriteMaster(MatroskaIds.TrackEntry, entry);
        }

        EbmlWriter element = new EbmlWriter(entries.Length + 16);
        element.WriteMaster(MatroskaIds.Tracks, entries);
        return element.ToArray();
    }

    private static EbmlWriter BuildVideoSettings(MatroskaMuxTrack track)
    {
        EbmlWriter video = new EbmlWriter(64);
        video.WriteUnsigned(MatroskaIds.PixelWidth, (ulong)track.PixelWidth);
        video.WriteUnsigned(MatroskaIds.PixelHeight, (ulong)track.PixelHeight);

        Av1SequenceHeader header = track.SequenceHeader;
        VideoColorInfo color = header.Color;
        EbmlWriter colour = new EbmlWriter(64);

        if (header.ColorDescriptionPresent) colour.WriteUnsigned(MatroskaIds.MatrixCoefficients, (ulong)(int)color.Matrix);
        colour.WriteUnsigned(MatroskaIds.BitsPerChannel, (ulong)header.BitDepth);

        if (!header.Monochrome)
        {
            colour.WriteUnsigned(MatroskaIds.ChromaSubsamplingHorz, (ulong)header.SubsamplingX);
            colour.WriteUnsigned(MatroskaIds.ChromaSubsamplingVert, (ulong)header.SubsamplingY);
        }

        // AV1's chroma_sample_position: 1 = "vertical" (left, between rows), 2 = co-located (top left).
        if (header.ChromaSamplePosition == 1)
        {
            colour.WriteUnsigned(MatroskaIds.ChromaSitingHorz, 1);
            colour.WriteUnsigned(MatroskaIds.ChromaSitingVert, 2);
        }
        else if (header.ChromaSamplePosition == 2)
        {
            colour.WriteUnsigned(MatroskaIds.ChromaSitingHorz, 1);
            colour.WriteUnsigned(MatroskaIds.ChromaSitingVert, 1);
        }

        colour.WriteUnsigned(MatroskaIds.Range, color.Range == VideoColorRange.Full ? 2UL : 1UL);

        if (header.ColorDescriptionPresent)
        {
            colour.WriteUnsigned(MatroskaIds.TransferCharacteristics, (ulong)(int)color.Transfer);
            colour.WriteUnsigned(MatroskaIds.Primaries, (ulong)(int)color.Primaries);
        }

        video.WriteMaster(MatroskaIds.Colour, colour);
        return video;
    }

    private static byte[] BuildChapters(
        IReadOnlyList<Chapter> chapters,
        TimeSpan duration,
        MatroskaDocType docType,
        List<string> notices)
    {
        EbmlWriter edition = new EbmlWriter(512);

        for (int i = 0; i < chapters.Count; i++)
        {
            Chapter chapter = chapters[i];
            TimeSpan end = chapter.End > TimeSpan.Zero
                ? chapter.End
                : i + 1 < chapters.Count ? chapters[i + 1].Start : duration;

            EbmlWriter atom = new EbmlWriter(128);
            atom.WriteUnsigned(MatroskaIds.ChapterUid, (ulong)(i + 1));
            atom.WriteUnsigned(MatroskaIds.ChapterTimeStart, (ulong)(chapter.Start.Ticks * 100));
            atom.WriteUnsigned(MatroskaIds.ChapterTimeEnd, (ulong)(end.Ticks * 100));

            // The untagged title goes LAST: a reader that keeps one display per chapter - FFmpeg's does - keeps
            // the last one it meets, and the untagged title is the one meant for everybody.
            List<string> keys = new List<string>(chapter.Titles.Keys);
            keys.Sort((a, b) =>
            {
                if (a.Length == 0 && b.Length > 0) return 1;
                if (b.Length == 0 && a.Length > 0) return -1;
                return string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            });

            foreach (string key in keys)
            {
                string text = chapter.Titles[key];
                if (string.IsNullOrEmpty(text)) continue;

                string code = MatroskaLanguageCodes.ToMatroska(key, out bool exact);

                EbmlWriter display = new EbmlWriter(64);
                display.WriteUtf8(MatroskaIds.ChapString, text);
                display.WriteAscii(MatroskaIds.ChapLanguage, code);

                if (!exact && key.Length > 0)
                {
                    if (docType == MatroskaDocType.Matroska)
                    {
                        display.WriteAscii(MatroskaIds.ChapLanguageBcp47, key);
                    }
                    else
                    {
                        notices.Add(
                            $"Chapter {i + 1}'s '{key}' title is written with the language '{code}'; a webm document "
                            + "has no element for the full tag, so the rest of it is not carried.");
                    }
                }

                atom.WriteMaster(MatroskaIds.ChapterDisplay, display);
            }

            edition.WriteMaster(MatroskaIds.ChapterAtom, atom);
        }

        EbmlWriter editions = new EbmlWriter(edition.Length + 16);
        editions.WriteMaster(MatroskaIds.EditionEntry, edition);

        EbmlWriter element = new EbmlWriter(editions.Length + 16);
        element.WriteMaster(MatroskaIds.Chapters, editions);
        return element.ToArray();
    }

    // A Seek is: Seek header (2-byte id, 1-byte size) + SeekID (2 + 1 + 4) + SeekPosition (2 + 1 + 8).
    private const int SeekEntryLength = 3 + 7 + 3 + SeekPositionWidth;

    private static byte[] BuildSeekHead(long infoPosition, long tracksPosition, long chaptersPosition, long cuesPosition)
    {
        EbmlWriter entries = new EbmlWriter(128);
        AddSeek(entries, MatroskaIds.Info, infoPosition);
        AddSeek(entries, MatroskaIds.Tracks, tracksPosition);
        if (chaptersPosition >= 0) AddSeek(entries, MatroskaIds.Chapters, chaptersPosition);
        AddSeek(entries, MatroskaIds.Cues, cuesPosition);

        EbmlWriter element = new EbmlWriter(entries.Length + 16);
        element.WriteMaster(MatroskaIds.SeekHead, entries);
        return element.ToArray();
    }

    private static void AddSeek(EbmlWriter entries, uint id, long position)
    {
        Span<byte> idBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);

        EbmlWriter seek = new EbmlWriter(32);
        seek.WriteBinary(MatroskaIds.SeekId, idBytes);
        seek.WriteUnsignedFixed(MatroskaIds.SeekPosition, (ulong)position, SeekPositionWidth);
        entries.WriteMaster(MatroskaIds.Seek, seek);
    }

    /// <summary>
    /// Builds the index, settling the circle between its size and the positions in it: with the index in front,
    /// every cluster sits after it, so its positions grow with its own size. Starting from the smallest possible
    /// size, each round can only make the positions - and so the index - larger or leave them unchanged, so the
    /// loop ends as soon as a round changes nothing.
    /// </summary>
    private static byte[] BuildCuesSettled(
        MatroskaClusterWriter clusters,
        long metadataEnd,
        bool cuesToFront,
        out long clustersPosition,
        out int cuePointCount)
    {
        long cuesLength = 0;

        for (int round = 0; round < 64; round++)
        {
            clustersPosition = metadataEnd + (cuesToFront ? cuesLength : 0);
            byte[] cues = BuildCues(clusters, clustersPosition, out cuePointCount);
            if (cues.Length == cuesLength) return cues;
            cuesLength = cues.Length;
        }

        throw new InvalidOperationException("The index's size did not settle; this is a defect in the muxer.");
    }

    private static byte[] BuildCues(MatroskaClusterWriter clusters, long clustersPosition, out int cuePointCount)
    {
        List<MatroskaCueEntry> entries = new List<MatroskaCueEntry>(clusters.CueEntries);
        List<KeyValuePair<int, MatroskaCueEntry>> keyed = new List<KeyValuePair<int, MatroskaCueEntry>>(entries.Count);
        for (int i = 0; i < entries.Count; i++) keyed.Add(new KeyValuePair<int, MatroskaCueEntry>(i, entries[i]));
        keyed.Sort((a, b) =>
        {
            int byTime = a.Value.TimeMs.CompareTo(b.Value.TimeMs);
            if (byTime != 0) return byTime;
            int byTrack = a.Value.Track.CompareTo(b.Value.Track);
            return byTrack != 0 ? byTrack : a.Key.CompareTo(b.Key);
        });

        EbmlWriter points = new EbmlWriter(1024);
        cuePointCount = 0;

        int index = 0;
        while (index < keyed.Count)
        {
            long time = keyed[index].Value.TimeMs;
            EbmlWriter point = new EbmlWriter(64);
            point.WriteUnsigned(MatroskaIds.CueTime, (ulong)time);

            while (index < keyed.Count && keyed[index].Value.TimeMs == time)
            {
                MatroskaCueEntry entry = keyed[index].Value;
                MatroskaClusterRecord cluster = clusters.Clusters[entry.ClusterIndex];

                EbmlWriter positions = new EbmlWriter(32);
                positions.WriteUnsigned(MatroskaIds.CueTrack, (ulong)entry.Track);
                positions.WriteUnsigned(MatroskaIds.CueClusterPosition, (ulong)(clustersPosition + cluster.Offset));
                positions.WriteUnsigned(MatroskaIds.CueRelativePosition, (ulong)entry.RelativePosition);
                if (entry.DurationMs >= 0) positions.WriteUnsigned(MatroskaIds.CueDuration, (ulong)entry.DurationMs);

                point.WriteMaster(MatroskaIds.CueTrackPositions, positions);
                index++;
            }

            points.WriteMaster(MatroskaIds.CuePoint, point);
            cuePointCount++;
        }

        EbmlWriter element = new EbmlWriter(points.Length + 16);
        element.WriteMaster(MatroskaIds.Cues, points);
        return element.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
