using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback.Authoring;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoProcessing;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>
/// Regenerates the sample-video corpus under <c>tests/assets/authoring</c> from the two Public-Domain phone
/// recordings in its <c>MP4</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// It writes four sibling folders - <c>MKV</c>, <c>WebM</c>, <c>CodeBrix-Mode1</c> and
/// <c>CodeBrix-Mode2</c> - six files each: two orientations by three resolutions. Every one of them is AV1
/// video; the first three carry Opus audio and are muxed by FFmpeg, and Mode2 carries Vorbis and is muxed by
/// the playback library's own bespoke muxer.
/// </para>
/// <para>
/// It is a kept artifact rather than a one-off script, and every command line it runs is built by
/// CodeBrix.VideoPlayback.Authoring - see <see cref="CorpusEncoder" />, which is now a translation from this
/// tool's plan into that library's request rather than a prototype of it.
/// </para>
/// <code>
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --dry-run
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --only CodeBrix-Mode2
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
                        Console.Error.WriteLine("--only needs a folder name: " + KnownFolders() + ".");
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

        // The authoring library needs ffmpeg and ffprobe, and it is the thing that says so: its message names
        // both binaries and where it looked for them.
        if (!CbvAuthor.TryVerifyTools(out string toolProblem))
        {
            Console.Error.WriteLine(toolProblem);
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
            Console.Error.WriteLine($"'{only}' matches no folder. Use {KnownFolders()}.");
            return 2;
        }

        if (dryRun)
        {
            foreach (CorpusItem item in wanted)
            {
                string output = item.ResolveOutputPath(root);
                string source = Path.Combine(sourceFolder, item.Source.FileName);
                Console.WriteLine(item.RelativePath);

                foreach (AuthoringCommand command in CorpusEncoder.BuildCommands(item, source, output))
                {
                    Console.WriteLine("    [" + command.Label + "] ffmpeg " + command.Arguments);
                }

                if (item.Flavour == VideoAuthoringFlavour.Bespoke)
                {
                    Console.WriteLine("    [mux] CbvAuthoring.Write - the bespoke container, written by the library");
                }

                Console.WriteLine();
            }

            return 0;
        }

        string manifestPath = Path.Combine(root, "MANIFEST.txt");
        IReadOnlyDictionary<string, string> recordedTimes = ManifestWriter.ReadRecordedEncodeTimes(manifestPath);

        List<CorpusFileRecord> records = new List<CorpusFileRecord>(plan.Count);
        List<string> regenerated = new List<string>();
        Stopwatch total = Stopwatch.StartNew();
        int failures = 0;
        int produced = 0;

        foreach (CorpusItem item in plan)
        {
            string output = item.ResolveOutputPath(root);
            string source = Path.Combine(sourceFolder, item.Source.FileName);
            TimeSpan duration = sourceDurations[item.Source.Key];
            bool encodeIt = wanted.Contains(item);

            if (encodeIt)
            {
                if (!regenerated.Contains(item.FolderName)) regenerated.Add(item.FolderName);

                produced++;
                Log(
                    "[" + produced.ToString(CultureInfo.InvariantCulture).PadLeft(2) + "/" + wanted.Count + "] "
                    + item.RelativePath + "  " + item.Dimensions
                    + "  preset " + item.Tier.Preset + ", crf " + item.Tier.Crf
                    + ", " + item.AudioEncoderName);

                Stopwatch clock = Stopwatch.StartNew();
                VideoAuthoringResult authored;

                try
                {
                    authored = CorpusEncoder.Encode(
                        item,
                        source,
                        output,
                        duration,
                        progress =>
                        {
                            if (progress.Percent % 25 == 0 && progress.Percent > 0 && progress.Percent < 100)
                            {
                                Log("        " + progress.Label + " " + progress.Percent + "%");
                            }
                        },
                        !skipProfileCheck);
                }
                catch (Exception ex)
                {
                    Log("        FAILED: " + ex.Message);
                    failures++;
                    records.Add(new CorpusFileRecord { Item = item, Present = false });
                    continue;
                }

                clock.Stop();

                VerificationResult verification = CorpusVerifier.Verify(item, output, duration);
                ProfileCheckResult profile = skipProfileCheck
                    ? ProfileCheckRunner.Skipped("--skip-profile-check was given")
                    : ProfileCheckRunner.From(authored.Profile);

                records.Add(new CorpusFileRecord
                {
                    Item = item,
                    Encoded = true,
                    Commands = Relativise(authored.Commands, repositoryRoot),
                    Mux = authored.Mux,
                    Notes = authored.Notes,
                    Elapsed = clock.Elapsed,
                    Verification = verification,
                    ProfileCheck = profile,
                });

                if (!verification.Passed) failures++;

                Log(
                    "        done in " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, "
                    + CorpusVerifier.FormatSize(verification.SizeInBytes)
                    + ", read back " + verification
                    + ", profile " + (profile.Ran ? profile.Verdict : "not checked"));

                if (item.Profile is CorpusProfile.Mode1 or CorpusProfile.Mode2 && profile.Ran && !profile.Passed)
                {
                    failures++;
                    Log("        THIS FILE DOES NOT PASS THE PROFILE:");
                    foreach (string rule in profile.FailedRules()) Log("            " + rule);
                }

                continue;
            }

            // Not re-encoded in this run, but the manifest describes the WHOLE corpus, so the file is read
            // back and described anyway. Its command line is re-derived from the plan - which is where it
            // came from - and its encode time is carried over from the previous manifest.
            if (!File.Exists(output))
            {
                records.Add(new CorpusFileRecord { Item = item, Present = false });
                continue;
            }

            VerificationResult existing = CorpusVerifier.Verify(item, output, duration);
            ProfileCheckResult existingProfile = skipProfileCheck
                ? ProfileCheckRunner.Skipped("--skip-profile-check was given")
                : ProfileCheckRunner.Run(output);

            recordedTimes.TryGetValue(item.RelativePath, out string recorded);

            records.Add(new CorpusFileRecord
            {
                Item = item,
                Encoded = false,
                Commands = Relativise(CorpusEncoder.BuildCommands(item, source, output), repositoryRoot),
                Notes = Array.Empty<string>(),
                RecordedElapsed = recorded,
                Verification = existing,
                ProfileCheck = existingProfile,
            });

            if (!existing.Passed) failures++;
        }

        total.Stop();

        ManifestWriter.Write(manifestPath, records, sources, total.Elapsed, regenerated);
        Log("manifest        " + manifestPath);

        long bytes = 0;
        int present = 0;
        foreach (CorpusFileRecord record in records)
        {
            if (!record.Present) continue;
            present++;
            bytes += record.Verification.SizeInBytes;
        }

        Log(
            "TOTAL           " + present + " files present, " + CorpusVerifier.FormatSize(bytes)
            + ", " + total.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");

        if (failures > 0)
        {
            Log("FAILED          " + failures + " check(s) did not pass");
            return 1;
        }

        return 0;
    }

    // The manifest is checked in and read by people on other machines, so the command lines it records must
    // not be full of this machine's home directory.
    private static IReadOnlyList<AuthoringCommand> Relativise(
        IReadOnlyList<AuthoringCommand> commands,
        string repositoryRoot)
    {
        if (repositoryRoot == null) return commands;

        string prefix = repositoryRoot + Path.DirectorySeparatorChar;
        List<AuthoringCommand> relative = new List<AuthoringCommand>(commands.Count);

        foreach (AuthoringCommand command in commands)
        {
            relative.Add(new AuthoringCommand(command.Label, command.Arguments.Replace(prefix, string.Empty)));
        }

        return relative;
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

    private static string KnownFolders()
    {
        List<string> folders = new List<string>();
        foreach (CorpusProfile profile in CorpusPlan.Profiles) folders.Add(CorpusPlan.FolderFor(profile));
        return string.Join(", ", folders);
    }

    private static void Log(string message) =>
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message);

    private static void WriteUsage()
    {
        Console.Error.WriteLine("CodeBrix.VideoPlayback sample-video corpus generator");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Rebuilds tests/assets/authoring/{MKV,WebM,CodeBrix-Mode1,CodeBrix-Mode2} and");
        Console.Error.WriteLine("  MANIFEST.txt from the two Public-Domain recordings in tests/assets/authoring/MP4.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("    --dry-run              print every command line and produce nothing");
        Console.Error.WriteLine("    --only <folder>        re-encode only that folder. The manifest is still");
        Console.Error.WriteLine("                           rewritten in full: every other folder is read back and");
        Console.Error.WriteLine("                           re-verified rather than re-encoded, so the manifest");
        Console.Error.WriteLine("                           never describes a corpus that is half stale.");
        Console.Error.WriteLine("    --skip-profile-check   do not judge each finished file against the profile");
        Console.Error.WriteLine("    --authoring-root <p>   use this folder instead of the repository's own");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Needs ffmpeg and ffprobe on the PATH, built with libsvtav1, libopus and");
        Console.Error.WriteLine("  libvorbis. Nothing else: the bespoke container is written by managed code.");
    }
}
