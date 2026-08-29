using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// Turns the language codes media containers carry into BCP 47 tags, which is the one form this library
/// exposes.
/// </summary>
/// <remarks>
/// <para>
/// Matroska has carried a three-letter ISO 639-2 code since it was designed and a proper BCP 47 tag since the
/// <c>LanguageBCP47</c> element was added; a file may have either or both, and the BCP 47 one wins where both
/// are present. Everything this library hands to an application is BCP 47, so an application never has to know
/// which the file used.
/// </para>
/// <para>
/// The mapping covers the two-letter equivalents of the languages that actually turn up in media files. A
/// three-letter code with no two-letter equivalent is a perfectly good BCP 47 tag on its own and is passed
/// through unchanged; so is anything that already looks like a tag.
/// </para>
/// </remarks>
public static class LanguageTags
{
    private static readonly Dictionary<string, string> TwoLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ara"] = "ar", ["ben"] = "bn", ["bul"] = "bg", ["cat"] = "ca", ["ces"] = "cs", ["cze"] = "cs",
        ["chi"] = "zh", ["zho"] = "zh", ["dan"] = "da", ["deu"] = "de", ["ger"] = "de", ["dut"] = "nl",
        ["nld"] = "nl", ["ell"] = "el", ["gre"] = "el", ["eng"] = "en", ["est"] = "et", ["fas"] = "fa",
        ["per"] = "fa", ["fin"] = "fi", ["fra"] = "fr", ["fre"] = "fr", ["heb"] = "he", ["hin"] = "hi",
        ["hrv"] = "hr", ["hun"] = "hu", ["ind"] = "id", ["isl"] = "is", ["ice"] = "is", ["ita"] = "it",
        ["jpn"] = "ja", ["kor"] = "ko", ["lav"] = "lv", ["lit"] = "lt", ["msa"] = "ms", ["may"] = "ms",
        ["nor"] = "no", ["nob"] = "nb", ["nno"] = "nn", ["pol"] = "pl", ["por"] = "pt", ["ron"] = "ro",
        ["rum"] = "ro", ["rus"] = "ru", ["slk"] = "sk", ["slo"] = "sk", ["slv"] = "sl", ["spa"] = "es",
        ["srp"] = "sr", ["swe"] = "sv", ["tam"] = "ta", ["tel"] = "te", ["tha"] = "th", ["tur"] = "tr",
        ["ukr"] = "uk", ["urd"] = "ur", ["vie"] = "vi",
    };

    /// <summary>Normalises a container's language code to a BCP 47 tag.</summary>
    /// <param name="code">
    /// An ISO 639-2 three-letter code, a BCP 47 tag, or null. The Matroska placeholder <c>und</c>
    /// ("undetermined") and the default <c>eng</c> placeholder are both handled.
    /// </param>
    /// <returns>
    /// A BCP 47 tag, or an empty string when the code says nothing - so an application can tell "no language"
    /// from "some language".
    /// </returns>
    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;

        string trimmed = code.Trim();
        if (string.Equals(trimmed, "und", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "mis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "zxx", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        int dash = trimmed.IndexOf('-');
        string primary = dash < 0 ? trimmed : trimmed.Substring(0, dash);
        string suffix = dash < 0 ? string.Empty : trimmed.Substring(dash);

        if (primary.Length == 3 && TwoLetter.TryGetValue(primary, out string mapped)) return mapped + suffix;
        if (primary.Length == 2) return primary.ToLowerInvariant() + suffix;

        return trimmed;
    }

    /// <summary>Reports whether two language tags name the same language, ignoring region and script.</summary>
    /// <param name="first">The first tag.</param>
    /// <param name="second">The second tag.</param>
    /// <returns>True when the primary subtags match.</returns>
    public static bool SameLanguage(string first, string second)
    {
        string a = Normalize(first);
        string b = Normalize(second);
        if (a.Length == 0 || b.Length == 0) return false;

        int dashA = a.IndexOf('-');
        int dashB = b.IndexOf('-');
        if (dashA > 0) a = a.Substring(0, dashA);
        if (dashB > 0) b = b.Substring(0, dashB);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
