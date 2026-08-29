using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Helpers;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>
/// Regenerates the sample-video corpus under <c>tests/assets/authoring</c> from the two Public-Domain phone
/// recordings in its <c>MP4</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// It writes three sibling folders - <c>MKV</c>, <c>WebM</c> and <c>CodeBrix-Mode1</c> - six files each: two
/// orientations by three resolutions. Every one of them is AV1 video with Opus audio; the difference between
/// the folders is the container and, for Mode1, where the seek index sits.
/// </para>
/// <para>
/// This tool is a kept artifact, not a one-off script, and it is the working PROTOTYPE of the authoring
/// helper the library side of this program will eventually expose - see <see cref="CorpusEncoder" />, where
/// the command construction lives.
/// </para>
/// <code>
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --dry-run
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --only CodeBrix-Mode1
/// </code>
/// </remarks>
public static class Program
{
    /// <summary>Regenerates the corpus.</summary>
    /// <param name="args">The switches described by <c>--help</c>.</param>
    /// <returns>0 when every file was produced and verified, 1 when anything failed, 2 for a bad command line.</returns>
    public static int Main(string[] args)
    {
        bool dryRun = false;
        bool skipProfileCheck = false;
        string only = null;
        string root = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--skip-profile-check":
                    skipProfileCheck = true;
                    break;

                case "--only":
                    if (++i == args.Length)
                    {
                        Console.Error.WriteLine("--only needs a folder name: MKV, WebM or CodeBrix-Mode1.");
                        return 2;
                    }

                    only = args[i];
                    break;

                case "--authoring-root":
                    if (++i == args.Length)
                    {
                        Console.Error.WriteLine("--authoring-root needs a path.");
                        return 2;
                    }

                    root = args[i];
                    break;

                case "-h":
                case "--help":
                    WriteUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"'{args[i]}' is not a switch this tool knows.");
                    WriteUsage();
                    return 2;
            }
        }

        string repositoryRoot = FindRepositoryRoot();
        if (root == null)
        {
            if (repositoryRoot == null)
            {
                Console.Error.WriteLine(
                    "The repository root could not be found from "
                    + AppContext.BaseDirectory
                    + ". Pass --authoring-root <path>.");
                return 1;
            }

            root = Path.Combine(repositoryRoot, "tests", "assets", "authoring");
        }

        string sourceFolder = Path.Combine(root, "MP4");
        if (!Directory.Exists(sourceFolder))
        {
            Console.Error.WriteLine($"There is no MP4 folder at '{sourceFolder}'.");
            return 1;
        }

        try
        {
            FFMpegHelper.VerifyFFMpegExists(GlobalFFOptions.Current);
            FFProbeHelper.VerifyFFProbeExists(GlobalFFOptions.Current);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ffmpeg and ffprobe must be on the PATH: " + ex.Message);
            return 1;
        }

        Log("authoring root  " + root);

        List<SourceRecord> sources = new List<SourceRecord>();
        Dictionary<string, TimeSpan> sourceDurations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (SourceClip clip in CorpusPlan.Sources)
        {
            string path = Path.Combine(sourceFolder, clip.FileName);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"The Public-Domain original '{path}' is missing.");
                return 1;
            }

            SourceRecord record = ProbeSource(clip, path);
            sources.Add(record);
            sourceDurations[clip.Key] = record.Duration;

            Log(
                "source          MP4/" + clip.FileName
                + "  stored " + record.StoredWidth + "x" + record.StoredHeight
                + ", rotation " + record.Rotation
                + ", displayed " + record.DisplayedWidth + "x" + record.DisplayedHeight
                + ", " + CorpusVerifier.Format(record.Duration));
        }

        IReadOnlyList<CorpusItem> plan = CorpusPlan.Build();
        List<CorpusItem> wanted = new List<CorpusItem>(plan.Count);

        foreach (CorpusItem item in plan)
        {
            if (only == null || string.Equals(item.FolderName, only, StringComparison.OrdinalIgnoreCase))
            {
                wanted.Add(item);
            }
        }

        if (wanted.Count == 0)
        {
            Console.Error.WriteLine($"'{only}' matches no folder. Use MKV, WebM or CodeBrix-Mode1.");
            return 2;
        }

        if (dryRun)
        {
            foreach (CorpusItem item in wanted)
            {
                string output = item.ResolveOutputPath(root);
                string source = Path.Combine(sourceFolder, item.Source.FileName);
                Console.WriteLine(item.RelativePath);
                Console.WriteLine("    ffmpeg " + CorpusEncoder.BuildCommand(item, source, output).Arguments);
                Console.WriteLine();
            }

            return 0;
        }

        string tool = skipProfileCheck ? null : ProfileCheckRunner.FindTool(repositoryRoot);
        if (!skipProfileCheck && tool == null)
        {
            Log("cbvinfo         NOT BUILT - the profile check will be recorded as not run");
        }

        List<CorpusFileRecord> records = new List<CorpusFileRecord>(wanted.Count);
        Stopwatch total = Stopwatch.StartNew();
        int failures = 0;

        for (int i = 0; i < wanted.Count; i++)
        {
            CorpusItem item = wanted[i];
            string output = item.ResolveOutputPath(root);
            string source = Path.Combine(sourceFolder, item.Source.FileName);
            TimeSpan duration = sourceDurations[item.Source.Key];

            Log(
                "[" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2) + "/" + wanted.Count + "] "
                + item.RelativePath + "  " + item.Dimensions
                + "  preset " + item.Tier.Preset + ", crf " + item.Tier.Crf);

            Stopwatch clock = Stopwatch.StartNew();
            string commandLine = CorpusEncoder.Encode(
                item,
                source,
                output,
                duration,
                percent =>
                {
                    if (percent % 25 == 0 && percent > 0 && percent < 100) Log("        " + percent + "%");
                });

            clock.Stop();

            // The manifest is checked in and read by people on other machines, so the command line it records
            // must not be full of this machine's home directory.
            if (repositoryRoot != null)
            {
                commandLine = commandLine.Replace(repositoryRoot + Path.DirectorySeparatorChar, string.Empty);
            }

            VerificationResult verification = CorpusVerifier.Verify(item, output, duration);
            ProfileCheckResult profile = ProfileCheckRunner.Run(tool, output);

            records.Add(new CorpusFileRecord
            {
                Item = item,
                CommandLine = commandLine,
                Elapsed = clock.Elapsed,
                Verification = verification,
                ProfileCheck = profile,
            });

            if (!verification.Passed) failures++;

            Log(
                "        done in " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, "
                + CorpusVerifier.FormatSize(verification.SizeInBytes)
                + ", ffprobe " + verification
                + ", profile " + (profile.Ran ? profile.Verdict : "not checked"));

            if (item.Profile == CorpusProfile.Mode1 && profile.Ran && !profile.Passed)
            {
                failures++;
                Log("        MODE1 FILE DOES NOT PASS THE PROFILE:");
                foreach (string rule in profile.FailedRules()) Log("            " + rule);
            }
        }

        total.Stop();

        // The manifest describes the whole corpus, so it is only rewritten when the whole corpus was made.
        if (only == null)
        {
            string manifest = Path.Combine(root, "MANIFEST.txt");
            ManifestWriter.Write(manifest, records, sources, total.Elapsed);
            Log("manifest        " + manifest);
        }
        else
        {
            Log("manifest        not rewritten: this run only produced " + only);
        }

        long bytes = 0;
        foreach (CorpusFileRecord record in records) bytes += record.Verification.SizeInBytes;

        Log(
            "TOTAL           " + records.Count + " files, " + CorpusVerifier.FormatSize(bytes)
            + ", " + total.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");

        if (failures > 0)
        {
            Log("FAILED          " + failures + " check(s) did not pass");
            return 1;
        }

        return 0;
    }

    private static SourceRecord ProbeSource(SourceClip clip, string path)
    {
        IMediaAnalysis analysis = FFProbe.Analyse(path);
        VideoStream video = analysis.PrimaryVideoStream;
        AudioStream audio = analysis.PrimaryAudioStream;

        bool quarterTurn = video != null && (Math.Abs(video.Rotation) == 90 || Math.Abs(video.Rotation) == 270);

        return new SourceRecord
        {
            FileName = clip.FileName,
            StoredWidth = video == null ? 0 : video.Width,
            StoredHeight = video == null ? 0 : video.Height,
            Rotation = video == null ? 0 : video.Rotation,
            DisplayedWidth = video == null ? 0 : quarterTurn ? video.Height : video.Width,
            DisplayedHeight = video == null ? 0 : quarterTurn ? video.Width : video.Height,
            FrameRate = video == null ? 0 : video.FrameRate,
            Duration = analysis.Duration,
            VideoCodec = video == null ? "none" : video.CodecName,
            AudioCodec = audio == null ? "none" : audio.CodecName,
            SizeInBytes = new FileInfo(path).Length,
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeBrix.VideoPlayback.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static void Log(string message) =>
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message);

    private static void WriteUsage()
    {
        Console.Error.WriteLine("CodeBrix.VideoPlayback sample-video corpus generator");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Rebuilds tests/assets/authoring/{MKV,WebM,CodeBrix-Mode1} and MANIFEST.txt from the two");
        Console.Error.WriteLine("  Public-Domain recordings in tests/assets/authoring/MP4.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("    --dry-run              print every command line and produce nothing");
        Console.Error.WriteLine("    --only <folder>        rebuild only MKV, WebM or CodeBrix-Mode1 (the manifest is left alone)");
        Console.Error.WriteLine("    --skip-profile-check   do not run cbvinfo over each finished file");
        Console.Error.WriteLine("    --authoring-root <p>   use this folder instead of the repository's own");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Needs ffmpeg and ffprobe on the PATH, built with libsvtav1 and libopus.");
    }
}
