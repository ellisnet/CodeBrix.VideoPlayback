using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Runs the <c>cbvmux</c> verb as the asset scripts run it, and checks the one rule the bespoke container
/// exists for: the sound in a <c>.cbv</c> file is Vorbis.
/// </summary>
/// <remarks>
/// <para>
/// A bespoke <c>.cbv</c> has to play with CodeBrix.VideoPlayback - whose CodeBrix.Audio dependency has Vorbis
/// built in - plus a video decoder package, and NOTHING else. Opus would need the playing application to
/// reference CodeBrix.Audio.Opus and call its <c>Register()</c>, so this verb refuses an Ogg Opus rather than
/// write a file that quietly needs one package more than the format promises.
/// </para>
/// <para>
/// The core muxer underneath stays permissive on purpose - it is what the container's own tests drive to
/// build files no authoring surface would write - so the rule is asserted HERE, at the surface that has it.
/// </para>
/// <para>
/// The verb is a separate executable, so it is run as one. When the tools project has not been built the
/// tests skip themselves rather than pretend to have run.
/// </para>
/// </remarks>
public class CbvMuxCommandTests
{
    private static readonly string ToolsAssembly = FindToolsAssembly();

    [Fact]
    public void An_opus_ogg_is_refused_with_the_reason_and_no_file_is_written()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string video = TestAssets.Path("av1-video-only.ivf");
        string audio = TestAssets.Path("opus-audio.ogg");
        string directory = TestAssets.CreateTemporaryDirectory("cbvmux-opus");
        string output = Path.Combine(directory, "refused.cbv");

        try
        {
            //Act
            int exit = Run(out string text, "cbvmux", "--output", output, "--video", video, "--audio", audio);

            //Assert
            exit.Should().NotBe(0);
            text.Should().Contain("Opus");
            text.Should().Contain("CodeBrix.Audio.Opus");
            text.Should().Contain("NOTHING else");
            File.Exists(output).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_vorbis_ogg_is_muxed_exactly_as_it_always_was()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string video = TestAssets.Path("av1-video-only.ivf");
        string audio = TestAssets.Path("vorbis-audio.ogg");
        string directory = TestAssets.CreateTemporaryDirectory("cbvmux-vorbis");
        string output = Path.Combine(directory, "clip.cbv");

        try
        {
            //Act
            int exit = Run(out string text, "cbvmux", "--output", output, "--video", video, "--audio", audio);

            //Assert
            exit.Should().Be(0);
            File.Exists(output).Should().BeTrue();
            text.Should().Contain("12 video frames");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static int Run(out string output, params string[] arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(ToolsAssembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start);
        StringBuilder text = new StringBuilder();
        text.Append(process.StandardOutput.ReadToEnd());
        text.Append(process.StandardError.ReadToEnd());
        process.WaitForExit();

        output = text.ToString();
        return process.ExitCode;
    }

    private static void SkipWhenToolsAreNotBuilt() =>
        Assert.SkipWhen(
            ToolsAssembly == null,
            "The tools project has not been built; run 'dotnet build CodeBrix.VideoPlayback.slnx -c Release'.");

    private static string FindToolsAssembly()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";

        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "tools",
                "CodeBrix.VideoPlayback.Tools",
                "bin",
                configuration,
                "net10.0",
                "CodeBrix.VideoPlayback.Tools.dll");

            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
