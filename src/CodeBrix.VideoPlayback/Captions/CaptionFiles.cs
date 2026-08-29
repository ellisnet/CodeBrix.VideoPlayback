using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.Captions;

/// <summary>
/// Reads caption files - WebVTT and SubRip - into <see cref="CaptionTrack" />s, and pulls plain text out of
/// the block payloads a media container carries.
/// </summary>
/// <remarks>
/// This is what turns the caption files an author hands to the bespoke muxer into cues, and what the
/// Matroska reader uses to make sense of a subtitle block. Both formats are read leniently: an unparseable
/// cue is skipped rather than failing the whole file, because a caption file is text somebody typed.
/// </remarks>
public static class CaptionFiles
{
    /// <summary>Reads a WebVTT file.</summary>
    /// <param name="path">The path of the <c>.vtt</c> file.</param>
    /// <param name="id">The identifier to give the resulting track.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">The track's name, or null for none.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public static CaptionTrack ReadWebVttFile(
        string path,
        int id,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no caption file at '{path}'.", path);

        return ParseWebVtt(File.ReadAllText(path, Encoding.UTF8), id, language, name, flags);
    }

    /// <summary>Reads a SubRip file.</summary>
    /// <param name="path">The path of the <c>.srt</c> file.</param>
    /// <param name="id">The identifier to give the resulting track.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">The track's name, or null for none.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">There is no file at that path.</exception>
    public static CaptionTrack ReadSubRipFile(
        string path,
        int id,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no caption file at '{path}'.", path);

        return ParseSubRip(File.ReadAllText(path, Encoding.UTF8), id, language, name, flags);
    }

    /// <summary>Reads a caption file, choosing the parser from the file's extension.</summary>
    /// <param name="path">The path of a <c>.vtt</c> or <c>.srt</c> file.</param>
    /// <param name="id">The identifier to give the resulting track.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">The track's name, or null for none.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    /// <exception cref="ArgumentException">The extension is neither <c>.vtt</c> nor <c>.srt</c>.</exception>
    public static CaptionTrack ReadFile(
        string path,
        int id,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));

        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".vtt", StringComparison.OrdinalIgnoreCase))
        {
            return ReadWebVttFile(path, id, language, name, flags);
        }

        if (string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase))
        {
            return ReadSubRipFile(path, id, language, name, flags);
        }

        throw new ArgumentException(
            $"'{path}' has the extension '{extension}'; caption files must be '.vtt' (WebVTT) or '.srt' (SubRip).",
            nameof(path));
    }

    /// <summary>Parses WebVTT text.</summary>
    /// <param name="text">The whole contents of a WebVTT file.</param>
    /// <param name="id">The identifier to give the resulting track.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">The track's name, or null for none.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    /// <exception cref="VideoPlaybackException">The text does not begin with the WEBVTT signature.</exception>
    public static CaptionTrack ParseWebVtt(
        string text,
        int id,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        CaptionTrack track = new CaptionTrack(id, language, name, flags, CaptionFormat.WebVtt);
        if (string.IsNullOrEmpty(text))
        {
            track.AreCuesComplete = true;
            return track;
        }

        string body = text[0] == '﻿' ? text.Substring(1) : text;
        if (!body.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            throw new VideoPlaybackException(
                "A WebVTT file must begin with the text 'WEBVTT'; this one begins with "
                + $"'{body.Substring(0, Math.Min(16, body.Length))}'.");
        }

        string[] lines = SplitLines(body);
        int index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length && lines[index].Trim().Length == 0) index++;
            if (index >= lines.Length) break;

            string first = lines[index];
            if (first.StartsWith("WEBVTT", StringComparison.Ordinal)
                || first.StartsWith("NOTE", StringComparison.Ordinal)
                || first.StartsWith("STYLE", StringComparison.Ordinal)
                || first.StartsWith("REGION", StringComparison.Ordinal))
            {
                while (index < lines.Length && lines[index].Trim().Length != 0) index++;
                continue;
            }

            string identifier = string.Empty;
            if (first.IndexOf("-->", StringComparison.Ordinal) < 0)
            {
                identifier = first.Trim();
                index++;
                if (index >= lines.Length) break;
            }

            string timing = lines[index];
            int arrow = timing.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                index++;
                continue;
            }

            index++;

            string startText = timing.Substring(0, arrow).Trim();
            string rest = timing.Substring(arrow + 3).Trim();
            string endText = rest;
            string settings = string.Empty;

            int space = rest.IndexOf(' ');
            if (space > 0)
            {
                endText = rest.Substring(0, space);
                settings = rest.Substring(space + 1).Trim();
            }

            if (!TryParseWebVttTime(startText, out TimeSpan start) || !TryParseWebVttTime(endText, out TimeSpan end))
            {
                while (index < lines.Length && lines[index].Trim().Length != 0) index++;
                continue;
            }

            StringBuilder payload = new StringBuilder();
            while (index < lines.Length && lines[index].Trim().Length != 0)
            {
                if (payload.Length > 0) payload.Append('\n');
                payload.Append(lines[index]);
                index++;
            }

            track.AddCue(new CaptionCue(start, end, payload.ToString(), settings, identifier));
        }

        track.AreCuesComplete = true;
        return track;
    }

    /// <summary>Parses SubRip text.</summary>
    /// <param name="text">The whole contents of a SubRip file.</param>
    /// <param name="id">The identifier to give the resulting track.</param>
    /// <param name="language">The BCP 47 language tag for the track.</param>
    /// <param name="name">The track's name, or null for none.</param>
    /// <param name="flags">What the track is for.</param>
    /// <returns>A caption track whose cues are complete.</returns>
    public static CaptionTrack ParseSubRip(
        string text,
        int id,
        string language,
        string name = null,
        CaptionTrackFlags flags = CaptionTrackFlags.None)
    {
        CaptionTrack track = new CaptionTrack(id, language, name, flags, CaptionFormat.SubRip);
        if (string.IsNullOrEmpty(text))
        {
            track.AreCuesComplete = true;
            return track;
        }

        string body = text[0] == '﻿' ? text.Substring(1) : text;
        string[] lines = SplitLines(body);
        int index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length && lines[index].Trim().Length == 0) index++;
            if (index >= lines.Length) break;

            string identifier = string.Empty;
            if (lines[index].IndexOf("-->", StringComparison.Ordinal) < 0)
            {
                identifier = lines[index].Trim();
                index++;
                if (index >= lines.Length) break;
            }

            string timing = lines[index];
            int arrow = timing.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                index++;
                continue;
            }

            index++;

            string startText = timing.Substring(0, arrow).Trim();
            string endText = timing.Substring(arrow + 3).Trim();
            int space = endText.IndexOf(' ');
            if (space > 0) endText = endText.Substring(0, space);

            if (!TryParseWebVttTime(startText, out TimeSpan start) || !TryParseWebVttTime(endText, out TimeSpan end))
            {
                while (index < lines.Length && lines[index].Trim().Length != 0) index++;
                continue;
            }

            StringBuilder payload = new StringBuilder();
            while (index < lines.Length && lines[index].Trim().Length != 0)
            {
                if (payload.Length > 0) payload.Append('\n');
                payload.Append(lines[index]);
                index++;
            }

            track.AddCue(new CaptionCue(start, end, payload.ToString(), string.Empty, identifier));
        }

        track.AreCuesComplete = true;
        return track;
    }

    /// <summary>Parses a time stamp in the form <c>hh:mm:ss.mmm</c> or <c>mm:ss.mmm</c>, comma or dot.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="value">The parsed time, or <see cref="TimeSpan.Zero" /> when the text was not a time.</param>
    /// <returns>True when the text was parsed.</returns>
    public static bool TryParseWebVttTime(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim().Replace(',', '.');
        string[] parts = trimmed.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return false;

        double hours = 0;
        double minutes;
        double seconds;

        if (parts.Length == 3)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hours)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return false;
        }
        else
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return false;
        }

        value = TimeSpan.FromSeconds((hours * 3600) + (minutes * 60) + seconds);
        return true;
    }

    /// <summary>Writes a time stamp in WebVTT's <c>hh:mm:ss.mmm</c> form.</summary>
    /// <param name="value">The time to write.</param>
    /// <returns>The formatted time.</returns>
    public static string FormatWebVttTime(TimeSpan value) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)value.TotalHours,
            value.Minutes,
            value.Seconds,
            value.Milliseconds);

    /// <summary>
    /// Pulls the readable text out of one Advanced SubStation dialogue line as a media container stores it.
    /// </summary>
    /// <param name="line">
    /// The block payload: the dialogue fields from <c>ReadOrder</c> onwards, comma separated, with the text
    /// as the ninth field.
    /// </param>
    /// <returns>The dialogue text with the style override blocks removed and the line breaks turned into newlines.</returns>
    /// <remarks>
    /// Styling, positioning and effects are dropped. Carrying Advanced SubStation styling faithfully is a
    /// rendering problem, and rendering captions is not something this package does.
    /// </remarks>
    public static string ExtractAssText(string line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        int field = 0;
        int start = 0;
        while (field < 8)
        {
            int comma = line.IndexOf(',', start);
            if (comma < 0)
            {
                start = -1;
                break;
            }

            start = comma + 1;
            field++;
        }

        string text = start >= 0 && start <= line.Length ? line.Substring(start) : line;

        StringBuilder builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                int close = text.IndexOf('}', i);
                if (close < 0) break;
                i = close;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == 'N' || text[i + 1] == 'n'))
            {
                builder.Append('\n');
                i++;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] == 'h')
            {
                builder.Append(' ');
                i++;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string text)
    {
        List<string> lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r') continue;

            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }

        if (start < text.Length) lines.Add(text.Substring(start));
        return lines.ToArray();
    }
}
