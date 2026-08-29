using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Chapters;

/// <summary>
/// One chapter marker: when it starts, when it ends, whether it should be listed, and its title in as many
/// languages as the file carried.
/// </summary>
/// <remarks>
/// <para>
/// The model is Matroska's, because it is the richer of the two containers this library reads: a chapter is a
/// span with any number of titles, each tagged with a language. The bespoke container stores exactly the same
/// shape, and one ffmetadata file authors chapters for both.
/// </para>
/// <para>
/// Chapters are flat and single-edition at launch: nested chapter atoms and ordered editions are read past
/// rather than modelled.
/// </para>
/// </remarks>
public sealed class Chapter
{
    private readonly Dictionary<string, string> titles;

    /// <summary>Creates a chapter.</summary>
    /// <param name="index">The chapter's zero-based position in the media.</param>
    /// <param name="start">When the chapter begins.</param>
    /// <param name="end">
    /// When the chapter ends, or <see cref="TimeSpan.Zero" /> to mean "until the next chapter, or the end of
    /// the media".
    /// </param>
    /// <param name="isHidden">True when the file asks for the chapter not to be listed to the viewer.</param>
    /// <param name="titles">
    /// The chapter's titles keyed by BCP 47 language tag. An empty key holds a title whose language the file
    /// did not state.
    /// </param>
    public Chapter(int index, TimeSpan start, TimeSpan end, bool isHidden, IReadOnlyDictionary<string, string> titles)
    {
        Index = index;
        Start = start;
        End = end;
        IsHidden = isHidden;

        this.titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (titles == null) return;

        foreach (KeyValuePair<string, string> entry in titles)
        {
            this.titles[entry.Key ?? string.Empty] = entry.Value ?? string.Empty;
        }
    }

    /// <summary>The chapter's zero-based position in the media.</summary>
    public int Index { get; }

    /// <summary>When the chapter begins.</summary>
    public TimeSpan Start { get; }

    /// <summary>
    /// When the chapter ends, or <see cref="TimeSpan.Zero" /> when the file left it to run until the next
    /// chapter or the end of the media.
    /// </summary>
    public TimeSpan End { get; }

    /// <summary>True when the file asks for the chapter not to be listed to the viewer.</summary>
    public bool IsHidden { get; }

    /// <summary>The chapter's titles, keyed by BCP 47 language tag.</summary>
    public IReadOnlyDictionary<string, string> Titles => titles;

    /// <summary>
    /// The chapter's title with no language preference expressed - the first one the file carried, which is
    /// what a single-language file gives.
    /// </summary>
    public string Title => TitleFor(Array.Empty<string>());

    /// <summary>Picks the title that best matches a list of preferred languages.</summary>
    /// <param name="preferredLanguages">
    /// BCP 47 tags in order of preference. An exact match wins; failing that, a match on the primary
    /// subtag (so "en" finds "en-GB"); failing that, the untagged title; failing that, whatever there is.
    /// </param>
    /// <returns>A title, or an empty string when the chapter has none.</returns>
    public string TitleFor(IReadOnlyList<string> preferredLanguages)
    {
        if (titles.Count == 0) return string.Empty;

        if (preferredLanguages != null)
        {
            for (int i = 0; i < preferredLanguages.Count; i++)
            {
                string wanted = preferredLanguages[i];
                if (string.IsNullOrEmpty(wanted)) continue;
                if (titles.TryGetValue(wanted, out string exact)) return exact;
            }

            for (int i = 0; i < preferredLanguages.Count; i++)
            {
                string wanted = preferredLanguages[i];
                if (string.IsNullOrEmpty(wanted)) continue;

                string primary = PrimarySubtag(wanted);
                foreach (KeyValuePair<string, string> entry in titles)
                {
                    if (string.Equals(PrimarySubtag(entry.Key), primary, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
            }
        }

        if (titles.TryGetValue(string.Empty, out string untagged)) return untagged;

        foreach (KeyValuePair<string, string> entry in titles) return entry.Value;
        return string.Empty;
    }

    /// <summary>Reports whether a position falls inside the chapter.</summary>
    /// <param name="position">A position in the media.</param>
    /// <param name="mediaDuration">
    /// The media's whole duration, used when the chapter has no end of its own.
    /// </param>
    /// <returns>True when the position is at or after <see cref="Start" /> and before the chapter's end.</returns>
    public bool Contains(TimeSpan position, TimeSpan mediaDuration)
    {
        if (position < Start) return false;
        TimeSpan effectiveEnd = End > TimeSpan.Zero ? End : mediaDuration;
        return effectiveEnd <= TimeSpan.Zero || position < effectiveEnd;
    }

    /// <inheritdoc />
    public override string ToString() => $"chapter {Index} at {Start}: {Title}";

    private static string PrimarySubtag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return string.Empty;
        int dash = tag.IndexOf('-');
        return dash < 0 ? tag : tag.Substring(0, dash);
    }
}
