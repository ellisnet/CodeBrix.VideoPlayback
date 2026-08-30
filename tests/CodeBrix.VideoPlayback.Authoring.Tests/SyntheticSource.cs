using System;
using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Enums;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Makes the tiny clips the end-to-end tests author from, out of FFmpeg's own synthetic generators.
/// </summary>
/// <remarks>
/// Nothing here is third-party media and nothing is downloaded, exactly as the golden corpus one folder up is
/// made (see tests/assets/generate-assets.sh). The frames are 128 by 72 because SVT-AV1 refuses anything
/// under 64 on either side, and the clips are two seconds at ten frames a second because that is enough to
/// carry two chapters and four caption cues and still encode in well under a second.
/// </remarks>
public static class SyntheticSource
{
    /// <summary>The frame width every synthetic clip is made at.</summary>
    public const int Width = 128;

    /// <summary>The frame height every synthetic clip is made at.</summary>
    public const int Height = 72;

    /// <summary>The frame rate every synthetic clip is made at.</summary>
    public const int FramesPerSecond = 10;

    /// <summary>The duration every synthetic clip is made at, in seconds.</summary>
    public const double DurationSeconds = 2.0;

    /// <summary>The duration every synthetic clip is made at.</summary>
    public static TimeSpan Duration { get; } = TimeSpan.FromSeconds(DurationSeconds);

    /// <summary>Skips the calling test when FFmpeg is not installed, naming what was looked for.</summary>
    public static void SkipWithoutFFmpeg()
    {
        bool available = CbvAuthor.TryVerifyTools(out string problem);
        Assert.SkipUnless(available, problem);
    }

    /// <summary>Writes a landscape test clip carrying a moving picture and a tone.</summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>The path it was written to.</returns>
    public static string WriteClip(string path) => WriteClip(path, Width, Height);

    /// <summary>Writes a test clip of a given size.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="width">The frame width. Must be at least 64 for SVT-AV1.</param>
    /// <param name="height">The frame height. Must be at least 64 for SVT-AV1.</param>
    /// <returns>The path it was written to.</returns>
    public static string WriteClip(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        string size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=" + DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string audio = "sine=frequency=440:sample_rate=48000:duration="
            + DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .AddFileInput(audio, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => output
                .WithVideoCodec("libsvtav1")
                .WithSpeedPreset(13)
                .WithConstantRateFactor(50)
                .ForcePixelFormat("yuv420p")
                .WithAudioCodec("libopus")
                .WithAudioBitrate(32)
                .WithAudioSamplingRate(48000)
                .WithCustomArgument("-ac 2")
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        return path;
    }

    /// <summary>Writes a chapter file naming two chapters with a title in three languages each.</summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>The path it was written to.</returns>
    /// <remarks>
    /// The per-language titles are the point: the bespoke flavour keeps all three, and the WebM-profile
    /// flavour keeps only the untagged one. Both are asserted.
    /// </remarks>
    public static string WriteMultilingualChapters(string path)
    {
        File.WriteAllText(
            path,
            ";FFMETADATA1\n"
            + "\n"
            + "[CHAPTER]\n"
            + "TIMEBASE=1/1000\n"
            + "START=0\n"
            + "END=1000\n"
            + "title=Opening\n"
            + "title-de=Anfang\n"
            + "title-fr=Ouverture\n"
            + "\n"
            + "[CHAPTER]\n"
            + "TIMEBASE=1/1000\n"
            + "START=1000\n"
            + "END=2000\n"
            + "title=Closing\n"
            + "title-de=Schluss\n"
            + "title-fr=Fermeture\n");

        return path;
    }

    /// <summary>Writes a WebVTT caption file whose cues carry identifiers and positioning settings.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="firstLine">The text of the first cue, so two tracks can be told apart.</param>
    /// <returns>The path it was written to.</returns>
    public static string WriteCaptions(string path, string firstLine)
    {
        File.WriteAllText(
            path,
            "WEBVTT\n"
            + "\n"
            + "opening-cue\n"
            + "00:00:00.000 --> 00:00:01.000 line:0 position:50%\n"
            + firstLine + "\n"
            + "\n"
            + "closing-cue\n"
            + "00:00:01.000 --> 00:00:02.000\n"
            + firstLine + " again\n");

        return path;
    }
}
