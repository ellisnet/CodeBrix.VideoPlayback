using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Runs the <c>lutbake</c> verb the way the authoring pipeline will, and checks that the file it writes is
/// the table the composer would have produced in process.
/// </summary>
/// <remarks>
/// The verb is a separate executable, so it is run as one - by its built assembly, found by walking up from
/// the test assembly. When the tools project has not been built the tests here skip themselves rather than
/// pretend to have run.
/// </remarks>
public class LutBakeCommandTests
{
    private static readonly string ToolsAssembly = FindToolsAssembly();

    [Fact]
    public void A_chain_of_two_tables_with_percentages_bakes_to_what_the_composer_would_have_made()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string directory = TestAssets.CreateTemporaryDirectory("lutbake");

        try
        {
            Lut3D first = LutComposerTests.Twist(17);
            Lut3D second = LutComposerTests.Curve(17, 2.4f);

            string firstPath = Path.Combine(directory, "twist.cube");
            string secondPath = Path.Combine(directory, "curve.cube");
            string bakedPath = Path.Combine(directory, "effective.cube");

            CubeLutFile.Write(first, firstPath, "twist");
            CubeLutFile.Write(second, secondPath, "curve");

            //Act
            int exit = Run(
                directory,
                "lutbake",
                "--lut",
                firstPath + "@70",
                "--lut",
                secondPath,
                "--size",
                "33",
                "-o",
                bakedPath);

            //Assert
            exit.Should().Be(0);
            File.Exists(bakedPath).Should().BeTrue();

            Lut3D wanted = LutComposer.Compose(
                new[] { new LutLayer(first, 70d), new LutLayer(second) },
                new LutComposerOptions { OutputSize = 33 });

            CubeLut baked = CubeLutFile.ReadFile(bakedPath);
            baked.Lut3D.Size.Should().Be(33);
            baked.Lut3D.Values.ToArray().Should().Equal(wanted.Values.ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_baked_file_samples_the_same_as_the_composer_between_its_nodes_too()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string directory = TestAssets.CreateTemporaryDirectory("lutbake-sample");

        try
        {
            Lut1D gamma = LutComposerTests.Gamma(256, 2.2f);
            string gammaPath = Path.Combine(directory, "gamma.cube");
            string bakedPath = Path.Combine(directory, "effective.cube");

            CubeLutFile.Write(gamma, gammaPath, "gamma");

            //Act
            int exit = Run(directory, "lutbake", "--lut", gammaPath + "@40", "-o", bakedPath);

            //Assert
            exit.Should().Be(0);

            Lut3D wanted = LutComposer.Compose(new[] { new LutLayer(gamma, 40d) });
            Lut3D baked = CubeLutFile.ReadFile(bakedPath).Lut3D;

            foreach (float input in new[] { 0.03f, 0.17f, 0.4f, 0.63f, 0.88f })
            {
                wanted.Sample(input, input, input, out float wantRed, out float wantGreen, out float wantBlue);
                baked.Sample(input, input, input, out float gotRed, out float gotGreen, out float gotBlue);

                gotRed.Should().BeApproximately(wantRed, 1e-6f);
                gotGreen.Should().BeApproximately(wantGreen, 1e-6f);
                gotBlue.Should().BeApproximately(wantBlue, 1e-6f);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_default_percentage_is_a_hundred_and_the_default_size_follows_the_chain()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string directory = TestAssets.CreateTemporaryDirectory("lutbake-defaults");

        try
        {
            Lut3D table = LutComposerTests.Curve(65, 1.8f);
            string tablePath = Path.Combine(directory, "curve65.cube");
            string bakedPath = Path.Combine(directory, "effective.cube");
            CubeLutFile.Write(table, tablePath, null);

            //Act
            int exit = Run(directory, "lutbake", "--lut", tablePath, "-o", bakedPath);

            //Assert
            exit.Should().Be(0);

            Lut3D baked = CubeLutFile.ReadFile(bakedPath).Lut3D;
            baked.Size.Should().Be(65);
            baked.Values.ToArray().Should().Equal(table.Values.ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_command_line_that_asks_for_nothing_is_refused_with_its_usage()
    {
        //Arrange
        SkipWhenToolsAreNotBuilt();
        string directory = TestAssets.CreateTemporaryDirectory("lutbake-usage");

        try
        {
            //Act
            int noLut = Run(directory, "lutbake", "-o", Path.Combine(directory, "x.cube"));
            int noOutput = Run(directory, "lutbake", "--lut", Path.Combine(directory, "missing.cube"));

            //Assert
            noLut.Should().Be(2);
            noOutput.Should().Be(2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static int Run(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(ToolsAssembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start);
        StringBuilder output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        process.WaitForExit();

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
