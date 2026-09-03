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

    /// <summary>
    /// Skips the calling test when FFmpeg cannot do this work here, naming what was looked for.
    /// </summary>
    /// <remarks>
    /// Two things are needed, and a machine missing either still has a green suite: the binaries, which is
    /// what the library itself checks for, and an AV1 encoder in the build, which it does not - a perfectly
    /// working FFmpeg may simply have been built without one. See <see cref="AuthoringEncoders" />.
    /// </remarks>
    public static void SkipWithoutFFmpeg()
    {
        bool available = CbvAuthor.TryVerifyTools(out string problem);
        Assert.SkipUnless(available, problem);
        Assert.SkipUnless(AuthoringEncoders.IsAvailable, AuthoringEncoders.Problem);
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
    public static string WriteClip(string path, int width, int height) =>
        WriteClip(path, width, height, DurationSeconds);

    /// <summary>Writes a test clip of a given size and length.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="width">The frame width. Must be at least 64 for SVT-AV1.</param>
    /// <param name="height">The frame height. Must be at least 64 for SVT-AV1.</param>
    /// <param name="durationSeconds">How long the clip runs.</param>
    /// <returns>The path it was written to.</returns>
    /// <remarks>
    /// The length is a parameter for one reason: the cancellation test needs a clip whose encode is still
    /// running a second or two in, and two seconds of 128 by 72 is over before a cancel can reach it.
    /// </remarks>
    public static string WriteClip(string path, int width, int height, double durationSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        string size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=" + durationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string audio = "sine=frequency=440:sample_rate=48000:duration="
            + durationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .AddFileInput(audio, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => ApplyVideoEncoder(output)
                .ForcePixelFormat("yuv420p")
                .WithAudioCodec("libopus")
                .WithAudioBitrate(32)
                .WithAudioSamplingRate(48000)
                .WithCustomArgument("-ac 2")
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        return path;
    }

    /// <summary>Puts whichever AV1 encoder this machine has, at its fastest setting, on an output.</summary>
    /// <param name="output">The output to configure.</param>
    /// <returns>The same output, so the fluent chain can carry on.</returns>
    private static FFMpegArgumentOptions ApplyVideoEncoder(FFMpegArgumentOptions output)
    {
        AuthoringEncoders.ApplyFastestTo(output, 50);
        return output;
    }

    /// <summary>
    /// Writes a landscape test clip that carries text of its OWN: one subrip subtitle stream tagged "eng" and
    /// two titled chapters, inside a Matroska file.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>The path it was written to.</returns>
    /// <remarks>
    /// This is the source the "what is taken from the source" tests author from. The library must leave both
    /// kinds of text behind, in either flavour, and say so; and a chapter file handed to it must win over the
    /// two chapters here. The subtitle and chapter files it is built from are written beside it, in the
    /// test's own folder.
    /// </remarks>
    public static string WriteClipWithEmbeddedText(string path)
    {
        string folder = Path.GetDirectoryName(path);
        Directory.CreateDirectory(folder);

        string subtitles = Path.Combine(folder, "embedded-source.srt");
        File.WriteAllText(
            subtitles,
            "1\n00:00:00,000 --> 00:00:00,900\nHello from the source\n\n"
            + "2\n00:00:01,000 --> 00:00:01,900\nStill the source\n\n");

        string chapters = Path.Combine(folder, "embedded-source-chapters.txt");
        File.WriteAllText(
            chapters,
            ";FFMETADATA1\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Source One\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Source Two\n");

        string size = Width.ToString(CultureInfo.InvariantCulture) + "x"
            + Height.ToString(CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=" + DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string audio = "sine=frequency=440:sample_rate=48000:duration="
            + DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .AddFileInput(audio, false, input => input.ForceFormat("lavfi"))
            .AddFileInput(subtitles, false)
            .AddFileInput(chapters, false)
            .OutputToFile(path, true, output => ApplyVideoEncoder(output)
                .ForcePixelFormat("yuv420p")
                .WithAudioCodec("libopus")
                .WithAudioBitrate(32)
                .WithAudioSamplingRate(48000)
                .WithCustomArgument("-ac 2")
                .SelectStream(0, 0, Channel.Video)
                .SelectStream(0, 1, Channel.Audio)
                .SelectStream(0, 2, Channel.Subtitle)
                .WithCustomArgument("-c:s srt -metadata:s:s:0 language=eng -map_chapters 3")
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
