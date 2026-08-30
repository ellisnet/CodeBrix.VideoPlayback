using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.AssetAuthoring;
using CodeBrix.VideoPlayback.Authoring.Commands;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Proves the authoring library still renders EXACTLY the command lines the committed sample-video manifest
/// records.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that lets the corpus stand. The eighteen MKV, WebM and CodeBrix-Mode1 files under
/// tests/assets/authoring were encoded by a tool that built its own FFmpeg arguments; that tool now asks this
/// library instead. If the library renders the same text, those files are still exactly what the manifest
/// beside them says they are and nothing has to be re-encoded. If it ever stops rendering the same text, this
/// test says so and the corpus has to be rebuilt - which is a decision, not an accident.
/// </para>
/// <para>
/// It compares only the three FFmpeg-muxed folders. CodeBrix-Mode2 is a two-pass file whose second half is
/// muxed by managed code, and it was authored by this library from the start, so it has nothing to be
/// compared against.
/// </para>
/// </remarks>
public class CorpusCommandEquivalenceTests
{
    /// <summary>Every file of the three FFmpeg-muxed folders, by its path inside the corpus.</summary>
    public static TheoryData<string> EveryFfmpegMuxedFile
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();

            foreach (CorpusItem item in CorpusPlan.Build())
            {
                if (item.Profile == CorpusProfile.Mode2) continue;
                data.Add(item.RelativePath);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryFfmpegMuxedFile))]
    public void The_library_renders_the_command_the_manifest_recorded(string relativePath)
    {
        //Arrange
        string repositoryRoot = FindRepositoryRoot();
        Assert.SkipWhen(repositoryRoot == null, "This test reads the repository's own corpus manifest.");

        IReadOnlyDictionary<string, string> recorded = ReadRecordedCommands(repositoryRoot);
        Assert.SkipUnless(
            recorded.ContainsKey(relativePath),
            "The manifest at tests/assets/authoring/MANIFEST.txt records no command for " + relativePath + ".");

        CorpusItem item = FindItem(relativePath);
        string authoringRoot = Path.Combine(repositoryRoot, "tests", "assets", "authoring");
        string source = Path.Combine(authoringRoot, "MP4", item.Source.FileName);
        string output = item.ResolveOutputPath(authoringRoot);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CorpusEncoder.BuildCommands(item, source, output);
        string rendered = commands[0].Arguments.Replace(repositoryRoot + Path.DirectorySeparatorChar, string.Empty);

        //Assert
        commands.Count.Should().Be(1);
        rendered.Should().Be(recorded[relativePath]);
    }

    [Fact]
    public void Every_mode2_file_is_authored_in_two_passes_and_a_managed_mux()
    {
        //Arrange
        CorpusItem item = FindItem("CodeBrix-Mode2/landscape_hd.cbv");

        //Act
        IReadOnlyList<AuthoringCommand> commands = CorpusEncoder.BuildCommands(item, "/clips/in.mp4", "/out/x.cbv");

        //Assert
        commands.Count.Should().Be(2);
        commands[0].Arguments.Should().Contain("-c:v libsvtav1");
        commands[0].Arguments.Should().Contain("-f ivf");
        commands[1].Arguments.Should().Contain("-c:a libvorbis");
        commands[1].Arguments.Should().Contain("-f ogg");
    }

    [Fact]
    public void The_plan_is_twenty_four_files_in_four_folders()
    {
        //Act
        IReadOnlyList<CorpusItem> plan = CorpusPlan.Build();

        //Assert
        plan.Count.Should().Be(24);

        foreach (CorpusProfile profile in CorpusPlan.Profiles)
        {
            int count = 0;
            foreach (CorpusItem item in plan)
            {
                if (item.Profile == profile) count++;
            }

            count.Should().Be(6);
        }
    }

    private static CorpusItem FindItem(string relativePath)
    {
        foreach (CorpusItem item in CorpusPlan.Build())
        {
            if (string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal)) return item;
        }

        throw new InvalidOperationException("The corpus plan has no entry for '" + relativePath + "'.");
    }

    private static IReadOnlyDictionary<string, string> ReadRecordedCommands(string repositoryRoot)
    {
        Dictionary<string, string> commands = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = Path.Combine(repositoryRoot, "tests", "assets", "authoring", "MANIFEST.txt");
        if (!File.Exists(manifest)) return commands;

        string folder = null;
        string file = null;
        bool inFiles = false;

        foreach (string raw in File.ReadAllLines(manifest))
        {
            string line = raw.TrimEnd();

            if (!inFiles)
            {
                if (string.Equals(line.Trim(), "THE FILES", StringComparison.Ordinal)) inFiles = true;
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

            if (folder == null || file == null || !line.StartsWith("      command", StringComparison.Ordinal)) continue;

            int marker = line.IndexOf("ffmpeg ", StringComparison.Ordinal);
            if (marker < 0) continue;

            string key = folder + "/" + file;
            if (!commands.ContainsKey(key)) commands[key] = line.Substring(marker + "ffmpeg ".Length);
        }

        return commands;
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
}
