using System;
using System.Collections.Generic;
using System.Text;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>What <see cref="MatroskaMuxer.Write" /> produced.</summary>
public sealed class MatroskaAuthoringResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="path">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="docType">The document type stamped into it.</param>
    /// <param name="duration">The duration stated in its <c>Info</c> element.</param>
    /// <param name="tracks">Every track written, in track-number order.</param>
    /// <param name="chapterCount">How many chapters were written.</param>
    /// <param name="cuePointCount">How many <c>CuePoint</c> elements the index holds.</param>
    /// <param name="clusterCount">How many clusters were written.</param>
    /// <param name="cuesPrecedeClusters">True when the index was placed in front of the first cluster.</param>
    /// <param name="notices">Things worth knowing that did not stop the file being written.</param>
    /// <param name="profileReport">The streamable-profile report, or null when it was not asked for.</param>
    public MatroskaAuthoringResult(
        string path,
        long sizeInBytes,
        MatroskaDocType docType,
        TimeSpan duration,
        IReadOnlyList<MatroskaAuthoredTrack> tracks,
        int chapterCount,
        int cuePointCount,
        int clusterCount,
        bool cuesPrecedeClusters,
        IReadOnlyList<string> notices,
        StreamableProfileReport profileReport)
    {
        Path = path ?? string.Empty;
        SizeInBytes = sizeInBytes;
        DocType = docType;
        Duration = duration;
        Tracks = tracks ?? Array.Empty<MatroskaAuthoredTrack>();
        ChapterCount = chapterCount;
        CuePointCount = cuePointCount;
        ClusterCount = clusterCount;
        CuesPrecedeClusters = cuesPrecedeClusters;
        Notices = notices ?? Array.Empty<string>();
        ProfileReport = profileReport;
    }

    /// <summary>Where the file was written.</summary>
    public string Path { get; }

    /// <summary>How big the finished file is, in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>The document type stamped into the file.</summary>
    public MatroskaDocType DocType { get; }

    /// <summary>The duration stated in the file's <c>Info</c> element.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Every track written, in track-number order.</summary>
    public IReadOnlyList<MatroskaAuthoredTrack> Tracks { get; }

    /// <summary>How many chapters were written.</summary>
    public int ChapterCount { get; }

    /// <summary>How many <c>CuePoint</c> elements the index holds.</summary>
    public int CuePointCount { get; }

    /// <summary>How many clusters were written.</summary>
    public int ClusterCount { get; }

    /// <summary>True when the index was placed in front of the first cluster.</summary>
    public bool CuesPrecedeClusters { get; }

    /// <summary>Things worth knowing that did not stop the file being written - a language tag that had to be shortened, say.</summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>
    /// The streamable-profile report on the finished file, or null when
    /// <see cref="MatroskaAuthoringRequest.EvaluateProfile" /> was false.
    /// </summary>
    public StreamableProfileReport ProfileReport { get; }

    /// <summary>True when the profile was evaluated and the file passed it.</summary>
    public bool PassesProfile => ProfileReport != null && ProfileReport.Passes;

    /// <summary>The track of a given kind with the lowest number, or null when the file has none.</summary>
    /// <param name="kind">The kind wanted.</param>
    /// <returns>The track, or null.</returns>
    public MatroskaAuthoredTrack FindTrack(MediaTrackKind kind)
    {
        foreach (MatroskaAuthoredTrack track in Tracks)
        {
            if (track.Kind == kind) return track;
        }

        return null;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder text = new StringBuilder();
        text.Append(Path).Append(": ").Append(SizeInBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" bytes, ").Append(DocType == MatroskaDocType.WebM ? "webm" : "matroska")
            .Append(", ").Append(Tracks.Count).Append(" track(s), ")
            .Append(ClusterCount).Append(" cluster(s), ")
            .Append(CuePointCount).Append(" cue point(s) ").Append(CuesPrecedeClusters ? "in front" : "at the end")
            .Append(", ").Append(ChapterCount).Append(" chapter(s)");
        return text.ToString();
    }
}
