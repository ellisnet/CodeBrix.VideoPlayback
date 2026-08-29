using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CodeBrix.VideoProcessing;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>What the probe found in one finished file, and whether it is what was asked for.</summary>
public sealed class VerificationResult
{
    /// <summary>The failures, in the order they were checked. Empty means the file is as ordered.</summary>
    public List<string> Failures { get; } = new List<string>();

    /// <summary>True when nothing failed.</summary>
    public bool Passed => Failures.Count == 0;

    /// <summary>The video codec name the probe reported.</summary>
    public string VideoCodec { get; set; }

    /// <summary>The audio codec name the probe reported.</summary>
    public string AudioCodec { get; set; }

    /// <summary>The frame width the probe reported.</summary>
    public int Width { get; set; }

    /// <summary>The frame height the probe reported.</summary>
    public int Height { get; set; }

    /// <summary>The frame rate the probe reported.</summary>
    public double FrameRate { get; set; }

    /// <summary>The rotation the probe derived from the display matrix. Zero means none is left.</summary>
    public int Rotation { get; set; }

    /// <summary>The pixel format the probe reported.</summary>
    public string PixelFormat { get; set; }

    /// <summary>The audio sample rate the probe reported, in hertz.</summary>
    public int AudioSampleRateHz { get; set; }

    /// <summary>The audio channel count the probe reported.</summary>
    public int AudioChannels { get; set; }

    /// <summary>The duration the probe reported.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>The file's size in bytes.</summary>
    public long SizeInBytes { get; set; }

    /// <summary>A one-line summary for the manifest and the console.</summary>
    /// <returns><c>OK</c>, or <c>FAIL</c> followed by every failure.</returns>
    public override string ToString() =>
        Passed ? "OK" : "FAIL: " + string.Join("; ", Failures);
}

/// <summary>
/// Probes a finished corpus file through the library's own analysis API and checks it against the plan.
/// </summary>
/// <remarks>
/// Every number here comes back through <see cref="FFProbe.Analyse(string, FFOptions, string)" /> rather
/// than from parsing a command's output, which is the same seam an application would use.
/// </remarks>
public static class CorpusVerifier
{
    /// <summary>How far a finished file's duration may drift from its source before it is a failure.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(0.2);

    /// <summary>How far the reported frame rate may drift from 30 before it is a failure.</summary>
    public const double FrameRateTolerance = 0.05;

    /// <summary>Probes one finished file and checks everything the plan promised about it.</summary>
    /// <param name="item">The plan entry the file was produced from.</param>
    /// <param name="path">The finished file.</param>
    /// <param name="sourceDuration">The source clip's duration, which the output must stay close to.</param>
    /// <returns>What was found, and any way in which it is wrong.</returns>
    public static VerificationResult Verify(CorpusItem item, string path, TimeSpan sourceDuration)
    {
        IMediaAnalysis analysis = FFProbe.Analyse(path);
        VerificationResult result = new VerificationResult { SizeInBytes = new FileInfo(path).Length };

        VideoStream video = analysis.PrimaryVideoStream;
        AudioStream audio = analysis.PrimaryAudioStream;

        if (video == null)
        {
            result.Failures.Add("there is no video stream");
        }
        else
        {
            result.VideoCodec = video.CodecName;
            result.Width = video.Width;
            result.Height = video.Height;
            result.FrameRate = video.FrameRate;
            result.Rotation = video.Rotation;
            result.PixelFormat = video.PixelFormat;

            if (!string.Equals(video.CodecName, "av1", StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"video codec is '{video.CodecName}', not av1");
            }

            if (video.Width != item.Width || video.Height != item.Height)
            {
                result.Failures.Add($"frame is {video.Width}x{video.Height}, not {item.Dimensions}");
            }

            if (Math.Abs(video.FrameRate - CorpusPlan.FramesPerSecond) > FrameRateTolerance)
            {
                result.Failures.Add(
                    "frame rate is "
                    + video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)
                    + ", not 30");
            }

            // A non-zero rotation means a display matrix survived into the output and a player would still
            // have to turn the picture. The portrait files must carry their orientation in the PIXELS.
            if (video.Rotation != 0)
            {
                result.Failures.Add($"rotation side data of {video.Rotation} degrees is still present");
            }

            if (!string.Equals(video.PixelFormat, CorpusPlan.PixelFormat, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"pixel format is '{video.PixelFormat}', not {CorpusPlan.PixelFormat}");
            }

            bool wantsPortrait = item.Source.IsPortrait;
            bool isPortrait = video.Height > video.Width;
            if (wantsPortrait != isPortrait)
            {
                result.Failures.Add(
                    wantsPortrait
                        ? "the frame is not taller than it is wide"
                        : "the frame is not wider than it is tall");
            }
        }

        if (audio == null)
        {
            result.Failures.Add("there is no audio stream");
        }
        else
        {
            result.AudioCodec = audio.CodecName;
            result.AudioSampleRateHz = audio.SampleRateHz;
            result.AudioChannels = audio.Channels;

            if (!string.Equals(audio.CodecName, "opus", StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"audio codec is '{audio.CodecName}', not opus");
            }

            if (audio.Channels != CorpusPlan.AudioChannels)
            {
                result.Failures.Add($"audio has {audio.Channels} channel(s), not {CorpusPlan.AudioChannels}");
            }

            if (audio.SampleRateHz != CorpusPlan.AudioSampleRateHz)
            {
                result.Failures.Add($"audio is {audio.SampleRateHz} Hz, not {CorpusPlan.AudioSampleRateHz}");
            }
        }

        result.Duration = analysis.Duration;

        TimeSpan drift = result.Duration - sourceDuration;
        if (drift < TimeSpan.Zero) drift = drift.Negate();
        if (drift > DurationTolerance)
        {
            result.Failures.Add(
                "duration "
                + Format(result.Duration)
                + " is more than "
                + DurationTolerance.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                + " s from the source's "
                + Format(sourceDuration));
        }

        if (analysis.VideoStreams.Count != 1 || analysis.AudioStreams.Count != 1)
        {
            result.Failures.Add(
                $"the file carries {analysis.VideoStreams.Count} video and {analysis.AudioStreams.Count} audio stream(s), not one of each");
        }

        if (analysis.SubtitleStreams.Count != 0)
        {
            result.Failures.Add($"the file carries {analysis.SubtitleStreams.Count} subtitle stream(s); this corpus is plain audio and video");
        }

        return result;
    }

    /// <summary>Formats a duration the way the manifest and the console print it.</summary>
    /// <param name="value">The duration.</param>
    /// <returns>Seconds to three decimal places, with an <c>s</c>.</returns>
    public static string Format(TimeSpan value) =>
        value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    /// <summary>Formats a byte count the way the manifest and the console print it.</summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>Megabytes to two decimal places, with the exact byte count beside it.</returns>
    public static string FormatSize(long bytes)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append((bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture));
        builder.Append(" MB (");
        builder.Append(bytes.ToString("N0", CultureInfo.InvariantCulture));
        builder.Append(" bytes)");
        return builder.ToString();
    }
}
