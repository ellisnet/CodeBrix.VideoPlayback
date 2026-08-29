using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Captions;

/// <summary>
/// One track of text captions: a language, a name, what the track is for, and its cues.
/// </summary>
/// <remarks>
/// <para>
/// How the cues arrive depends on the container. The bespoke container stores whole caption tracks in its
/// header, so every cue is present the moment the file is opened and <see cref="AreCuesComplete" /> is true.
/// Matroska interleaves caption blocks with the video, so the cues accumulate as the file is played and
/// <see cref="AreCuesComplete" /> stays false until the end of the file has been reached. Either way the
/// session's active-cue reporting works the same; only the ability to list every cue up front differs.
/// </para>
/// <para>The cue list is safe to read from any thread while cues are being added.</para>
/// </remarks>
public sealed class CaptionTrack
{
    private readonly object gate = new object();
    private readonly List<CaptionCue> cues = new List<CaptionCue>();

    /// <summary>Creates a caption track.</summary>
    /// <param name="id">The track's identifier within the file.</param>
    /// <param name="language">The BCP 47 language tag, or an empty string when the file did not say.</param>
    /// <param name="name">The track's name as the file gave it, or an empty string.</param>
    /// <param name="flags">What the track is for.</param>
    /// <param name="format">The text format the cues were written in.</param>
    public CaptionTrack(int id, string language, string name, CaptionTrackFlags flags, CaptionFormat format)
    {
        Id = id;
        Language = language ?? string.Empty;
        Name = name ?? string.Empty;
        Flags = flags;
        Format = format;
    }

    /// <summary>The track's identifier within the file.</summary>
    public int Id { get; }

    /// <summary>The BCP 47 language tag, normalised from whatever the container carried. Never null.</summary>
    public string Language { get; }

    /// <summary>The track's name, as the file gave it. Never null.</summary>
    public string Name { get; }

    /// <summary>What the track is for: default, forced, written for the hearing impaired.</summary>
    public CaptionTrackFlags Flags { get; }

    /// <summary>The text format the cues were written in.</summary>
    public CaptionFormat Format { get; }

    /// <summary>True when the track is the one the author expects to be selected by default.</summary>
    public bool IsDefault => (Flags & CaptionTrackFlags.Default) != 0;

    /// <summary>True when the track carries only the parts that must be read whatever the viewer chose.</summary>
    public bool IsForced => (Flags & CaptionTrackFlags.Forced) != 0;

    /// <summary>True when the track is written for viewers who are deaf or hard of hearing.</summary>
    public bool IsHearingImpaired => (Flags & CaptionTrackFlags.HearingImpaired) != 0;

    /// <summary>
    /// True when every cue in the track is present. The bespoke container reports true from the moment the
    /// file is opened; Matroska only once the whole file has been read.
    /// </summary>
    public bool AreCuesComplete { get; internal set; }

    /// <summary>How many cues are known so far.</summary>
    public int CueCount
    {
        get
        {
            lock (gate) return cues.Count;
        }
    }

    /// <summary>The cues known so far, in ascending order of start time.</summary>
    /// <remarks>Reading this takes a copy, so the result is safe to walk while more cues are arriving.</remarks>
    public IReadOnlyList<CaptionCue> Cues
    {
        get
        {
            lock (gate) return cues.ToArray();
        }
    }

    /// <summary>Finds the cues that should be on screen at a given moment.</summary>
    /// <param name="position">A position in the media.</param>
    /// <param name="results">The list to add the active cues to. It is cleared first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="results" /> is null.</exception>
    public void GetActiveCues(TimeSpan position, List<CaptionCue> results)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        results.Clear();

        lock (gate)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                CaptionCue cue = cues[i];
                if (cue.Start > position) break;
                if (cue.IsActiveAt(position)) results.Add(cue);
            }
        }
    }

    internal void AddCue(CaptionCue cue)
    {
        if (cue == null) return;

        lock (gate)
        {
            int index = cues.Count;
            while (index > 0 && cues[index - 1].Start > cue.Start) index--;

            if (index < cues.Count || index > 0)
            {
                for (int i = Math.Max(0, index - 1); i < cues.Count && i <= index; i++)
                {
                    CaptionCue existing = cues[i];
                    if (existing.Start == cue.Start && existing.End == cue.End
                        && string.Equals(existing.Text, cue.Text, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            cues.Insert(index, cue);
        }
    }

    internal void ClearCues()
    {
        lock (gate) cues.Clear();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string label = string.IsNullOrEmpty(Name) ? Language : $"{Name} ({Language})";
        return $"caption track {Id}: {label}, {Format}, {Flags}";
    }
}
