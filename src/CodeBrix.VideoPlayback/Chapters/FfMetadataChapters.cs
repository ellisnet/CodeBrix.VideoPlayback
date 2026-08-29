using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.Chapters;

/// <summary>
/// Reads and writes chapters in the plain-text metadata format FFmpeg uses, so ONE chapter file serves both
/// flavours of the bespoke container and the FFmpeg-authored one.
/// </summary>
/// <remarks>
/// <para>
/// The format is a header line, then one block per chapter:
/// </para>
/// <code>
/// ;FFMETADATA1
///
/// [CHAPTER]
/// TIMEBASE=1/1000
/// START=0
/// END=12000
/// title=Opening
/// title-fr=Ouverture
/// </code>
/// <para>
/// <c>TIMEBASE</c> is a rational fraction of a second; <c>START</c> and <c>END</c> count in those units.
/// <c>title=</c> is the untagged title. <c>title-&lt;bcp47&gt;=</c> is an extension this library reads and
/// writes so that a multilingual chapter list can be authored in the same file; other tools ignore the extra
/// keys.
/// </para>
/// </remarks>
public static class FfMetadataChapters
{
    /// <summary>Reads a chapter file.</summary>
    /// <param name="path">The path of the metadata file.</param>
    /// <returns>The chapters it declares, in ascending order of start time.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public static IReadOnlyList<Chapter> ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no chapter file at '{path}'.", path);

        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Parses chapter metadata text.</summary>
    /// <param name="text">The whole contents of a metadata file.</param>
    /// <returns>The chapters it declares, in ascending order of start time.</returns>
    public static IReadOnlyList<Chapter> Parse(string text)
    {
        List<Chapter> chapters = new List<Chapter>();
        if (string.IsNullOrEmpty(text)) return chapters;

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool inChapter = false;
        double timebase = 1.0 / 1000.0;
        long start = 0;
        long end = 0;
        Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (!inChapter) return;

            chapters.Add(new Chapter(
                chapters.Count,
                TimeSpan.FromSeconds(start * timebase),
                end > 0 ? TimeSpan.FromSeconds(end * timebase) : TimeSpan.Zero,
                false,
                titles));

            inChapter = false;
            timebase = 1.0 / 1000.0;
            start = 0;
            end = 0;
            titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

            if (string.Equals(line, "[CHAPTER]", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                inChapter = true;
                continue;
            }

            if (line[0] == '[')
            {
                Flush();
                continue;
            }

            if (!inChapter) continue;

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;

            string key = line.Substring(0, equals).Trim();
            string value = Unescape(line.Substring(equals + 1));

            if (string.Equals(key, "TIMEBASE", StringComparison.OrdinalIgnoreCase))
            {
                timebase = ParseTimebase(value);
                continue;
            }

            if (string.Equals(key, "START", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start);
                continue;
            }

            if (string.Equals(key, "END", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out end);
                continue;
            }

            if (string.Equals(key, "title", StringComparison.OrdinalIgnoreCase))
            {
                titles[string.Empty] = value;
                continue;
            }

            if (key.StartsWith("title-", StringComparison.OrdinalIgnoreCase))
            {
                titles[key.Substring("title-".Length)] = value;
            }
        }

        Flush();
        chapters.Sort((a, b) => a.Start.CompareTo(b.Start));

        List<Chapter> ordered = new List<Chapter>(chapters.Count);
        for (int i = 0; i < chapters.Count; i++)
        {
            Chapter chapter = chapters[i];
            ordered.Add(new Chapter(i, chapter.Start, chapter.End, chapter.IsHidden, chapter.Titles));
        }

        return ordered;
    }

    /// <summary>Writes chapters as metadata text, with a millisecond timebase.</summary>
    /// <param name="chapters">The chapters to write.</param>
    /// <returns>The whole contents of a metadata file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chapters" /> is null.</exception>
    public static string Write(IReadOnlyList<Chapter> chapters)
    {
        if (chapters == null) throw new ArgumentNullException(nameof(chapters));

        StringBuilder builder = new StringBuilder();
        builder.Append(";FFMETADATA1\n");

        foreach (Chapter chapter in chapters)
        {
            builder.Append("\n[CHAPTER]\nTIMEBASE=1/1000\n");
            builder.Append("START=").Append((long)chapter.Start.TotalMilliseconds).Append('\n');
            builder.Append("END=").Append((long)chapter.End.TotalMilliseconds).Append('\n');

            foreach (KeyValuePair<string, string> title in chapter.Titles)
            {
                string key = string.IsNullOrEmpty(title.Key) ? "title" : "title-" + title.Key;
                builder.Append(key).Append('=').Append(Escape(title.Value)).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static double ParseTimebase(string value)
    {
        int slash = value.IndexOf('/');
        if (slash <= 0)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) && plain > 0
                ? plain
                : 1.0 / 1000.0;
        }

        bool haveNumerator = double.TryParse(
            value.Substring(0, slash),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double numerator);

        bool haveDenominator = double.TryParse(
            value.Substring(slash + 1),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double denominator);

        if (!haveNumerator || !haveDenominator || denominator == 0) return 1.0 / 1000.0;
        return numerator / denominator;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;

        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                builder.Append(value[i] == 'n' ? '\n' : value[i]);
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        StringBuilder builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == '=' || c == ';' || c == '#' || c == '\\') builder.Append('\\');
            if (c == '\n')
            {
                builder.Append("\\\n");
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
