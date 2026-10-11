using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// Turns the BCP 47 tags the rest of this library speaks into the three-letter codes Matroska's
/// <c>Language</c> and <c>ChapLanguage</c> elements hold.
/// </summary>
/// <remarks>
/// <para>
/// The Matroska specification asks for the ISO 639-2 BIBLIOGRAPHIC form where the two forms differ
/// (<c>fre</c>, <c>ger</c>, <c>chi</c> rather than <c>fra</c>, <c>deu</c>, <c>zho</c>), which is also the
/// form FFmpeg writes; <see cref="LanguageTags.Normalize" /> reads either form back to the same two-letter tag.
/// </para>
/// <para>
/// A tag with no three-letter equivalent here becomes <c>und</c>, and a tag with a region or script loses it in
/// the three-letter element; the muxer then states the whole tag as a BCP 47 element too in a <c>matroska</c>
/// document, and reports the loss as a notice in a <c>webm</c> one, whose element list has no BCP 47 element.
/// </para>
/// </remarks>
internal static class MatroskaLanguageCodes
{
    /// <summary>The code for "no language stated".</summary>
    internal const string Undetermined = "und";

    private static readonly Dictionary<string, string> Bibliographic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ara", ["bn"] = "ben", ["bg"] = "bul", ["ca"] = "cat", ["cs"] = "cze", ["zh"] = "chi",
        ["da"] = "dan", ["de"] = "ger", ["nl"] = "dut", ["el"] = "gre", ["en"] = "eng", ["et"] = "est",
        ["fa"] = "per", ["fi"] = "fin", ["fr"] = "fre", ["he"] = "heb", ["hi"] = "hin", ["hr"] = "hrv",
        ["hu"] = "hun", ["id"] = "ind", ["is"] = "ice", ["it"] = "ita", ["ja"] = "jpn", ["ko"] = "kor",
        ["lv"] = "lav", ["lt"] = "lit", ["ms"] = "may", ["no"] = "nor", ["nb"] = "nob", ["nn"] = "nno",
        ["pl"] = "pol", ["pt"] = "por", ["ro"] = "rum", ["ru"] = "rus", ["sk"] = "slo", ["sl"] = "slv",
        ["es"] = "spa", ["sr"] = "srp", ["sv"] = "swe", ["ta"] = "tam", ["te"] = "tel", ["th"] = "tha",
        ["tr"] = "tur", ["uk"] = "ukr", ["ur"] = "urd", ["vi"] = "vie",
    };

    /// <summary>Converts a tag to the three-letter code to write.</summary>
    /// <param name="tag">A BCP 47 tag, a three-letter code, or null.</param>
    /// <param name="exact">
    /// True when the three-letter code says everything the tag said; false when a region or script was dropped
    /// or the language has no code here.
    /// </param>
    /// <returns>The code; <c>und</c> for no language.</returns>
    internal static string ToMatroska(string tag, out bool exact)
    {
        exact = true;
        if (string.IsNullOrWhiteSpace(tag)) return Undetermined;

        string trimmed = tag.Trim();
        if (string.Equals(trimmed, Undetermined, StringComparison.OrdinalIgnoreCase)) return Undetermined;

        int dash = trimmed.IndexOf('-');
        string primary = dash < 0 ? trimmed : trimmed.Substring(0, dash);
        if (dash >= 0) exact = false;

        if (primary.Length == 3 && IsLetters(primary)) return primary.ToLowerInvariant();

        if (primary.Length == 2 && Bibliographic.TryGetValue(primary, out string code)) return code;

        exact = false;
        return Undetermined;
    }

    private static bool IsLetters(string text)
    {
        foreach (char c in text)
        {
            if (!(c is >= 'a' and <= 'z') && !(c is >= 'A' and <= 'Z')) return false;
        }

        return true;
    }
}
