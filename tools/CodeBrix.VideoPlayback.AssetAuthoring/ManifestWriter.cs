using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>Everything the generator learned about one finished file.</summary>
public sealed class CorpusFileRecord
{
    /// <summary>The plan entry the file was produced from.</summary>
    public CorpusItem Item { get; set; }

    /// <summary>The FFmpeg command line the library rendered and ran.</summary>
    public string CommandLine { get; set; }

    /// <summary>How long the encode took.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>What the probe found, and whether it matches the plan.</summary>
    public VerificationResult Verification { get; set; }

    /// <summary>What <c>cbvinfo</c> made of the file's streamable profile.</summary>
    public ProfileCheckResult ProfileCheck { get; set; }
}

/// <summary>Writes <c>MANIFEST.txt</c> beside the folders it describes.</summary>
public static class ManifestWriter
{
    /// <summary>Writes the manifest.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="records">Every finished file, in the order it was produced.</param>
    /// <param name="sources">What the probe found in the two Public-Domain originals.</param>
    /// <param name="totalElapsed">The wall clock of the whole run.</param>
    public static void Write(
        string path,
        IReadOnlyList<CorpusFileRecord> records,
        IReadOnlyList<SourceRecord> sources,
        TimeSpan totalElapsed)
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
        text.AppendLine("Every command line below was BUILT AND RUN by the CodeBrix.VideoProcessing library");
        text.AppendLine("(CodeBrix.VideoProcessing.MitLicenseForever), which launches the host's ffmpeg as a");
        text.AppendLine("child process. The generator never invokes ffmpeg itself.");
        text.AppendLine();
        text.AppendLine("REPRODUCIBILITY. The SHAPE of this corpus is deterministic - the same eighteen files,");
        text.AppendLine("the same resolutions, the same settings, the same command lines every run. The BYTES");
        text.AppendLine("are NOT: the Matroska muxer writes a random track UID and a fresh muxing date into");
        text.AppendLine("every file, so two runs of this tool produce files that differ even when the encoder");
        text.AppendLine("has made an identical bitstream. Those fields are fixed-length, so the sizes below do");
        text.AppendLine("tend to come back the same - but do not pin these files with a hash, and do not treat");
        text.AppendLine("a size that moves by a few bytes as a fault.");
        text.AppendLine();

        text.AppendLine();
        Rule(text);
        text.AppendLine("THE PUBLIC-DOMAIN ORIGINALS");
        Rule(text);
        text.AppendLine();
        text.AppendLine("Created by Jeremy Ellis on his phone, and placed by him in the Public Domain on");
        text.AppendLine("2026-08-29. Everything in MKV/, WebM/ and CodeBrix-Mode1/ is derived from these two files and");
        text.AppendLine("is therefore Public Domain too.");
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
        text.AppendLine("  Audio   " + CorpusPlan.AudioEncoder + ", " + CorpusPlan.AudioChannels + " channels at "
            + CorpusPlan.AudioSampleRateHz + " Hz.");
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
            }

            VerificationResult verification = record.Verification;
            totalBytes += verification.SizeInBytes;
            if (!verification.Passed) failed++;

            text.AppendLine();
            text.AppendLine("  " + record.Item.FileName);
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
            text.AppendLine("      encode time      " + record.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            text.AppendLine("      ffprobe check    " + verification);
            text.AppendLine("      profile check    " + Describe(record.ProfileCheck));

            foreach (string rule in record.ProfileCheck.Rules)
            {
                text.AppendLine("                       " + rule);
            }

            text.AppendLine("      command          ffmpeg " + record.CommandLine);
        }

        text.AppendLine();
        text.AppendLine();
        Rule(text);
        text.AppendLine("TOTALS");
        Rule(text);
        text.AppendLine();
        text.AppendLine("  files            " + records.Count);
        text.AppendLine("  total size       " + CorpusVerifier.FormatSize(totalBytes));
        text.AppendLine("  ffprobe checks   " + (records.Count - failed) + " of " + records.Count + " passed");
        text.AppendLine("  encode wall time " + totalElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        text.AppendLine();
        Rule(text);

        File.WriteAllText(path, text.ToString());
    }

    private static string Describe(ProfileCheckResult check)
    {
        if (check == null || !check.Ran) return "not run - " + (check == null ? "no result" : check.Unavailable);
        return check.Verdict + " (cbvinfo exit code " + check.ExitCode + ")";
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
