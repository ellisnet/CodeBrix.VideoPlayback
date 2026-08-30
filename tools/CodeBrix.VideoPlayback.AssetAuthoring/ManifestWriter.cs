using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Containers.Cbv;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>Everything the generator learned about one finished file.</summary>
public sealed class CorpusFileRecord
{
    /// <summary>The plan entry the file was produced from.</summary>
    public CorpusItem Item { get; set; }

    /// <summary>False when the file is not in this checkout at all.</summary>
    public bool Present { get; set; } = true;

    /// <summary>
    /// True when THIS run encoded the file; false when the run only read an existing file back to describe it.
    /// </summary>
    public bool Encoded { get; set; }

    /// <summary>The FFmpeg command lines the authoring library rendered - one, or two for Mode2.</summary>
    public IReadOnlyList<AuthoringCommand> Commands { get; set; }

    /// <summary>What the bespoke muxer produced, for a Mode2 file; null for the other three profiles.</summary>
    public CbvAuthoringResult Mux { get; set; }

    /// <summary>Anything the authoring library wanted the caller to know.</summary>
    public IReadOnlyList<string> Notes { get; set; }

    /// <summary>How long the encode took, when this run did it.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>The encode time recorded by an EARLIER run, when this one did not encode the file.</summary>
    public string RecordedElapsed { get; set; }

    /// <summary>What the probe or the container reader found, and whether it matches the plan.</summary>
    public VerificationResult Verification { get; set; }

    /// <summary>What the streamable profile made of the file.</summary>
    public ProfileCheckResult ProfileCheck { get; set; }
}

/// <summary>Writes <c>MANIFEST.txt</c> beside the folders it describes.</summary>
/// <remarks>
/// The manifest always describes the WHOLE corpus, even after a run that only rebuilt one folder. A run that
/// did not encode a file reads it back and describes it anyway - the command line is re-derived from the plan,
/// which is where it came from in the first place - and carries the encode time the previous manifest
/// recorded, so nothing measured is quietly lost. <see cref="ReadRecordedEncodeTimes" /> is how that carrying
/// happens.
/// </remarks>
public static class ManifestWriter
{
    /// <summary>Reads the encode times an earlier manifest recorded, so a partial run does not lose them.</summary>
    /// <param name="path">The existing manifest, which need not exist.</param>
    /// <returns>The recorded time for each "Folder/File", or an empty map when there is no manifest to read.</returns>
    public static IReadOnlyDictionary<string, string> ReadRecordedEncodeTimes(string path)
    {
        Dictionary<string, string> times = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return times;

        string folder = null;
        string file = null;
        bool inFiles = false;

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();

            if (!inFiles)
            {
                if (string.Equals(line, "THE FILES", StringComparison.Ordinal)) inFiles = true;
                continue;
            }

            if (line.Length > 1 && line[0] != ' ' && line.EndsWith("/", StringComparison.Ordinal))
            {
                folder = line.Substring(0, line.Length - 1);
                file = null;
                continue;
            }

            if (folder != null
                && line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("   ", StringComparison.Ordinal))
            {
                file = line.Trim();
                continue;
            }

            const string marker = "      encode time      ";
            if (folder != null && file != null && line.StartsWith(marker, StringComparison.Ordinal))
            {
                times[folder + "/" + file] = line.Substring(marker.Length).Trim();
            }
        }

        return times;
    }

    /// <summary>Writes the manifest.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="records">Every file of the corpus, in plan order.</param>
    /// <param name="sources">What the probe found in the two Public-Domain originals.</param>
    /// <param name="totalElapsed">The wall clock of the whole run.</param>
    /// <param name="regenerated">The folders this run actually re-encoded.</param>
    public static void Write(
        string path,
        IReadOnlyList<CorpusFileRecord> records,
        IReadOnlyList<SourceRecord> sources,
        TimeSpan totalElapsed,
        IReadOnlyList<string> regenerated)
    {
        StringBuilder text = new StringBuilder();

        Rule(text);
        text.AppendLine("MANIFEST: tests/assets/authoring");
        text.AppendLine("The sample-video corpus, file by file - what it is and how it was verified");
        Rule(text);
        text.AppendLine();
        text.AppendLine(
            "Written by tools/CodeBrix.VideoPlayback.AssetAuthoring on "
            + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + " (local time).");
        text.AppendLine("Regenerate it, and everything it describes, with:");
        text.AppendLine();
        text.AppendLine("    dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release");
        text.AppendLine();
        text.AppendLine("Every command line below was BUILT by CodeBrix.VideoPlayback.Authoring and RUN by");
        text.AppendLine("CodeBrix.VideoProcessing, which launches the host's ffmpeg as a child process. The");
        text.AppendLine("generator never invokes ffmpeg itself, and the CodeBrix-Mode2 files are muxed by the");
        text.AppendLine("playback library's own bespoke muxer rather than by ffmpeg at all.");
        text.AppendLine();
        text.AppendLine("WHAT THIS RUN RE-ENCODED: "
            + (regenerated == null || regenerated.Count == 0 ? "nothing" : string.Join(", ", regenerated)) + ".");
        text.AppendLine("Everything else below was read back and re-verified, not re-encoded; its command line");
        text.AppendLine("is re-derived from the plan, which is where it came from in the first place, and its");
        text.AppendLine("encode time is the one the previous manifest recorded.");
        text.AppendLine();
        text.AppendLine("REPRODUCIBILITY. The SHAPE of this corpus is deterministic - the same twenty-four files,");
        text.AppendLine("the same resolutions, the same settings, the same command lines every run. The BYTES");
        text.AppendLine("are NOT: the Matroska muxer writes a random track UID and a fresh muxing date into");
        text.AppendLine("every file, so two runs of this tool produce files that differ even when the encoder");
        text.AppendLine("has made an identical bitstream. Those fields are fixed-length, so the sizes below do");
        text.AppendLine("tend to come back the same - but do not pin these files with a hash, and do not treat");
        text.AppendLine("a size that moves by a few bytes as a fault. The CodeBrix-Mode2 files carry no random");
        text.AppendLine("field at all, but the AV1 encoder is itself only deterministic for a fixed build and");
        text.AppendLine("thread count, so the same caution applies to them.");
        text.AppendLine();

        text.AppendLine();
        Rule(text);
        text.AppendLine("THE PUBLIC-DOMAIN ORIGINALS");
        Rule(text);
        text.AppendLine();
        text.AppendLine("Created by Jeremy Ellis on his phone, and placed by him in the Public Domain on");
        text.AppendLine("2026-08-29. Everything in MKV/, WebM/, CodeBrix-Mode1/ and CodeBrix-Mode2/ is derived");
        text.AppendLine("from these two files and is therefore Public Domain too.");
        text.AppendLine();

        foreach (SourceRecord source in sources)
        {
            text.AppendLine("  MP4/" + source.FileName);
            text.AppendLine("      stored frame     " + source.StoredWidth + "x" + source.StoredHeight);
            text.AppendLine("      rotation         " + source.Rotation + " degrees"
                + (source.Rotation == 0 ? string.Empty : " (applied while decoding; the outputs have true pixels)"));
            text.AppendLine("      displayed frame  " + source.DisplayedWidth + "x" + source.DisplayedHeight);
            text.AppendLine("      frame rate       " + source.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " per second");
            text.AppendLine("      duration         " + CorpusVerifier.Format(source.Duration));
            text.AppendLine("      video / audio    " + source.VideoCodec + " / " + source.AudioCodec);
            text.AppendLine("      size             " + CorpusVerifier.FormatSize(source.SizeInBytes));
            text.AppendLine();
        }

        text.AppendLine();
        Rule(text);
        text.AppendLine("ENCODER SETTINGS, BY RESOLUTION RUNG");
        Rule(text);
        text.AppendLine();
        text.AppendLine("  rung   landscape     portrait      preset  crf  keyframes  audio");
        text.AppendLine("  -----  ------------  ------------  ------  ---  ---------  ---------");

        foreach (OutputTier tier in CorpusPlan.Tiers)
        {
            text.AppendLine(
                "  " + tier.Key.PadRight(5)
                + "  " + (tier.LongSide + "x" + tier.ShortSide).PadRight(12)
                + "  " + (tier.ShortSide + "x" + tier.LongSide).PadRight(12)
                + "  " + tier.Preset.ToString(CultureInfo.InvariantCulture).PadRight(6)
                + "  " + tier.Crf.ToString(CultureInfo.InvariantCulture).PadRight(3)
                + "  " + (CorpusPlan.KeyframeIntervalFrames + " frames").PadRight(9)
                + "  " + tier.AudioKilobitsPerSecond + " kbit/s");
        }

        text.AppendLine();
        text.AppendLine("  Video   " + CorpusPlan.VideoEncoder + ", 8-bit " + CorpusPlan.PixelFormat + ", "
            + CorpusPlan.FramesPerSecond + " frames per second, scaled with " + CorpusPlan.ScalerFlags + ".");
        text.AppendLine("  Audio   " + CorpusPlan.AudioEncoder + " for MKV, WebM and CodeBrix-Mode1; "
            + CorpusPlan.Mode2AudioEncoder + " for CodeBrix-Mode2.");
        text.AppendLine("          " + CorpusPlan.AudioChannels + " channels at " + CorpusPlan.AudioSampleRateHz
            + " Hz throughout, at the bit rates in the table above.");
        text.AppendLine("          The rungs are the SAME numbers for both codecs, so the two ladders stay");
        text.AppendLine("          comparable. Mode2 is Vorbis rather than Opus because Mode2 is the flavour an");
        text.AppendLine("          application ships inside itself, and a Vorbis file plays with the core");
        text.AppendLine("          playback package alone - an Opus one needs the application to add");
        text.AppendLine("          CodeBrix.Audio.Opus and call its Register().");
        text.AppendLine("  Keyframes every " + CorpusPlan.KeyframeIntervalFrames + " frames is two seconds at "
            + CorpusPlan.FramesPerSecond + " frames per second - set");
        text.AppendLine("  explicitly, because the encoder's own default interval is far longer and a long");
        text.AppendLine("  interval is exactly what makes a scrub feel slow.");
        text.AppendLine();
        text.AppendLine("  The preset gets FASTER as the frame gets bigger and the rate factor gets LOWER as it");
        text.AppendLine("  gets smaller: encode cost scales with the pixel count, and AV1's rate factor is");
        text.AppendLine("  resolution-relative, so this keeps both the wall clock and the perceived quality");
        text.AppendLine("  roughly level across the three rungs.");
        text.AppendLine();

        long totalBytes = 0;
        int failed = 0;
        int present = 0;

        text.AppendLine();
        Rule(text);
        text.AppendLine("THE FILES");
        Rule(text);

        string folder = null;

        foreach (CorpusFileRecord record in records)
        {
            if (record.Item.FolderName != folder)
            {
                folder = record.Item.FolderName;
                text.AppendLine();
                text.AppendLine(folder + "/");
                text.AppendLine(new string('=', folder.Length + 1));
                text.AppendLine();
                text.AppendLine("  " + CorpusPlan.DescriptionFor(record.Item.Profile));
            }

            text.AppendLine();
            text.AppendLine("  " + record.Item.FileName);

            if (!record.Present)
            {
                text.AppendLine("      NOT PRESENT      this file has not been generated in this checkout");
                continue;
            }

            present++;
            VerificationResult verification = record.Verification;
            totalBytes += verification.SizeInBytes;
            if (!verification.Passed) failed++;

            text.AppendLine("      source           MP4/" + record.Item.Source.FileName);
            text.AppendLine("      resolution       " + verification.Width + "x" + verification.Height
                + " (planned " + record.Item.Dimensions + ")");
            text.AppendLine("      frame rate       " + verification.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " per second");
            text.AppendLine("      duration         " + CorpusVerifier.Format(verification.Duration));
            text.AppendLine("      size             " + CorpusVerifier.FormatSize(verification.SizeInBytes));
            text.AppendLine("      video            " + verification.VideoCodec + ", " + verification.PixelFormat);
            text.AppendLine("      audio            " + verification.AudioCodec + ", " + verification.AudioChannels
                + " channels at " + verification.AudioSampleRateHz + " Hz, "
                + record.Item.Tier.AudioKilobitsPerSecond + " kbit/s requested");
            text.AppendLine("      rotation         " + verification.Rotation + " degrees");
            text.AppendLine("      encoder          " + CorpusPlan.VideoEncoder + " preset " + record.Item.Tier.Preset
                + ", crf " + record.Item.Tier.Crf + ", keyframes every " + CorpusPlan.KeyframeIntervalFrames + " frames");
            text.AppendLine("      encode time      " + DescribeElapsed(record));
            text.AppendLine("      read-back check  " + verification);

            if (record.Mux != null)
            {
                text.AppendLine("      mux              " + record.Mux.VideoFrameCount + " video frames, "
                    + record.Mux.AudioPacketCount + " audio packets, "
                    + record.Mux.CaptionTrackCount + " caption track(s) with "
                    + record.Mux.CaptionCueCount + " cues");
            }

            text.AppendLine("      profile check    " + Describe(record.ProfileCheck));

            foreach (string rule in record.ProfileCheck.Rules)
            {
                text.AppendLine("                       " + rule);
            }

            if (record.Notes != null)
            {
                foreach (string note in record.Notes) text.AppendLine("      note             " + note);
            }

            if (record.Commands != null)
            {
                foreach (AuthoringCommand command in record.Commands)
                {
                    text.AppendLine("      command          [" + command.Label + "] ffmpeg " + command.Arguments);
                }
            }
        }

        text.AppendLine();
        text.AppendLine();
        Rule(text);
        text.AppendLine("TOTALS");
        Rule(text);
        text.AppendLine();
        text.AppendLine("  files            " + present + " of " + records.Count + " present");
        text.AppendLine("  total size       " + CorpusVerifier.FormatSize(totalBytes));
        text.AppendLine("  read-back checks " + (present - failed) + " of " + present + " passed");
        text.AppendLine("  run wall time    " + totalElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        text.AppendLine();
        Rule(text);

        File.WriteAllText(path, text.ToString());
    }

    private static string DescribeElapsed(CorpusFileRecord record)
    {
        if (record.Encoded)
        {
            return record.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        return string.IsNullOrEmpty(record.RecordedElapsed)
            ? "not recorded (this run did not encode it)"
            : record.RecordedElapsed;
    }

    private static string Describe(ProfileCheckResult check)
    {
        if (check == null || !check.Ran) return "not run - " + (check == null ? "no result" : check.Unavailable);
        return check.Verdict;
    }

    private static void Rule(StringBuilder text) =>
        text.AppendLine(new string('=', 80));
}

/// <summary>What the probe found in one of the two Public-Domain originals.</summary>
public sealed class SourceRecord
{
    /// <summary>The file's name inside the <c>MP4</c> folder.</summary>
    public string FileName { get; set; }

    /// <summary>The width of the frame as it is STORED, before any rotation is applied.</summary>
    public int StoredWidth { get; set; }

    /// <summary>The height of the frame as it is STORED, before any rotation is applied.</summary>
    public int StoredHeight { get; set; }

    /// <summary>The rotation the container asks a player to apply, in degrees.</summary>
    public int Rotation { get; set; }

    /// <summary>The width of the frame once the rotation has been applied.</summary>
    public int DisplayedWidth { get; set; }

    /// <summary>The height of the frame once the rotation has been applied.</summary>
    public int DisplayedHeight { get; set; }

    /// <summary>The frame rate the container reports.</summary>
    public double FrameRate { get; set; }

    /// <summary>The clip's duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>The video codec's name.</summary>
    public string VideoCodec { get; set; }

    /// <summary>The audio codec's name.</summary>
    public string AudioCodec { get; set; }

    /// <summary>The file's size in bytes.</summary>
    public long SizeInBytes { get; set; }
}
