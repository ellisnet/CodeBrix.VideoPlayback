using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Runs the host FFmpeg as the ORACLE the managed processing primitives are measured against: raw frames in,
/// one filter chain, raw frames (or a frame listing) out. FFmpeg never ships; the tests that use this skip
/// themselves when it is not installed, exactly as the other host-dependent tests do.
/// </summary>
internal static class FfmpegProcessingOracle
{
    public const string FfmpegPath = "/usr/bin/ffmpeg";

    public static void SkipWhenAbsent() =>
        Assert.SkipUnless(
            File.Exists(FfmpegPath),
            $"FFmpeg is not installed at '{FfmpegPath}'; it is the oracle these tests compare against.");

    /// <summary>Filters one raw frame and returns the raw result.</summary>
    public static byte[] FilterRaw(
        byte[] input,
        string inputPixelFormat,
        int width,
        int height,
        string filter,
        string outputPixelFormat)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codebrix-processing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string inputPath = Path.Combine(directory, "in.raw");
            string outputPath = Path.Combine(directory, "out.raw");
            File.WriteAllBytes(inputPath, input);

            Run(new[]
            {
                "-hide_banner", "-nostdin", "-y",
                "-f", "rawvideo", "-pix_fmt", inputPixelFormat, "-s", $"{width}x{height}",
                "-i", inputPath,
                "-vf", filter,
                "-f", "rawvideo", "-pix_fmt", outputPixelFormat,
                outputPath,
            });

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Runs FFmpeg with a lavfi source and a filter chain into the <c>framecrc</c> muxer, and returns each
    /// output frame's presentation timestamp in the output stream's time base.
    /// </summary>
    public static List<long> FrameTimestamps(string lavfiSource, string filter)
    {
        List<string> arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", lavfiSource,
        };

        if (filter != null)
        {
            arguments.Add("-vf");
            arguments.Add(filter);
        }

        arguments.AddRange(new[] { "-fps_mode", "passthrough", "-f", "framecrc", "-" });

        string output = Run(arguments);
        List<long> timestamps = new List<long>();
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            string[] fields = trimmed.Split(',');
            timestamps.Add(long.Parse(fields[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        return timestamps;
    }

    private static string Run(IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, "ffmpeg said: " + error.Result);
        return output;
    }
}
