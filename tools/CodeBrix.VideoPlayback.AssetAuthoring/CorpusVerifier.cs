using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Decoding;
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
/// Reads a finished corpus file back and checks it against the plan.
/// </summary>
/// <remarks>
/// <para>
/// The three FFmpeg-muxed profiles are probed through <see cref="FFProbe.Analyse(string, FFOptions, string)" />
/// rather than by parsing a command's output, which is the same seam an application would use - and it means
/// the file is judged by an implementation OTHER than the one that wrote it.
/// </para>
/// <para>
/// Mode2 cannot be probed that way and it is not a shortcoming: a bespoke <c>CBVF</c> file is not a container
/// FFmpeg has ever heard of. It is read back with the playback library's own container reader instead, which
/// is the implementation that will have to open it in an application anyway.
/// </para>
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
        if (item.Profile == CorpusProfile.Mode2) return VerifyBespoke(item, path, sourceDuration);

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

            if (!string.Equals(audio.CodecName, item.ExpectedAudioCodecName, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"audio codec is '{audio.CodecName}', not {item.ExpectedAudioCodecName}");
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

    /// <summary>Reads a bespoke file back with the playback library's own reader and checks it.</summary>
    /// <param name="item">The plan entry the file was produced from.</param>
    /// <param name="path">The finished file.</param>
    /// <param name="sourceDuration">The source clip's duration, which the output must stay close to.</param>
    /// <returns>What was found, and any way in which it is wrong.</returns>
    public static VerificationResult VerifyBespoke(CorpusItem item, string path, TimeSpan sourceDuration)
    {
        VerificationResult result = new VerificationResult { SizeInBytes = new FileInfo(path).Length };

        using IMediaContainerReader reader = MediaContainers.Open(path);

        MediaTrackInfo video = null;
        MediaTrackInfo audio = null;
        int videoCount = 0;
        int audioCount = 0;

        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Video)
            {
                videoCount++;
                video ??= track;
            }
            else if (track.Kind == MediaTrackKind.Audio)
            {
                audioCount++;
                audio ??= track;
            }
        }

        if (video == null)
        {
            result.Failures.Add("there is no video track");
        }
        else
        {
            result.VideoCodec = video.CodecId;
            result.Width = video.Width;
            result.Height = video.Height;
            result.FrameRate = video.DefaultDuration > TimeSpan.Zero ? 1.0 / video.DefaultDuration.TotalSeconds : 0;
            result.PixelFormat = DescribePixels(video);

            if (!string.Equals(video.CodecId, VideoCodecIds.Av1, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"video codec is '{video.CodecId}', not {VideoCodecIds.Av1}");
            }

            if (video.Width != item.Width || video.Height != item.Height)
            {
                result.Failures.Add($"frame is {video.Width}x{video.Height}, not {item.Dimensions}");
            }

            if (Math.Abs(result.FrameRate - CorpusPlan.FramesPerSecond) > FrameRateTolerance)
            {
                result.Failures.Add(
                    "frame rate is "
                    + result.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)
                    + ", not 30");
            }

            if (!string.Equals(result.PixelFormat, CorpusPlan.PixelFormat, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"pixel format is '{result.PixelFormat}', not {CorpusPlan.PixelFormat}");
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
            result.Failures.Add("there is no audio track");
        }
        else
        {
            result.AudioCodec = audio.CodecId;
            result.AudioSampleRateHz = audio.SampleRate;
            result.AudioChannels = audio.Channels;

            if (!string.Equals(audio.CodecId, item.ExpectedAudioCodecName, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"audio codec is '{audio.CodecId}', not {item.ExpectedAudioCodecName}");
            }

            if (audio.Channels != CorpusPlan.AudioChannels)
            {
                result.Failures.Add($"audio has {audio.Channels} channel(s), not {CorpusPlan.AudioChannels}");
            }

            if (audio.SampleRate != CorpusPlan.AudioSampleRateHz)
            {
                result.Failures.Add($"audio is {audio.SampleRate} Hz, not {CorpusPlan.AudioSampleRateHz}");
            }
        }

        result.Duration = reader.Duration;

        TimeSpan bespokeDrift = result.Duration - sourceDuration;
        if (bespokeDrift < TimeSpan.Zero) bespokeDrift = bespokeDrift.Negate();
        if (bespokeDrift > DurationTolerance)
        {
            result.Failures.Add(
                "duration "
                + Format(result.Duration)
                + " is more than "
                + DurationTolerance.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                + " s from the source's "
                + Format(sourceDuration));
        }

        if (videoCount != 1 || audioCount != 1)
        {
            result.Failures.Add(
                $"the file carries {videoCount} video and {audioCount} audio track(s), not one of each");
        }

        if (reader.CaptionTracks.Count != 0)
        {
            result.Failures.Add(
                $"the file carries {reader.CaptionTracks.Count} caption track(s); this corpus is plain audio and video");
        }

        return result;
    }

    /// <summary>Names a video track's sample layout the way a probe would.</summary>
    /// <param name="video">The video track.</param>
    /// <returns>A pixel-format name such as <c>yuv420p</c>.</returns>
    public static string DescribePixels(MediaTrackInfo video)
    {
        string layout;
        switch (video.Layout)
        {
            case VideoPixelLayout.I420: layout = "yuv420p"; break;
            case VideoPixelLayout.I422: layout = "yuv422p"; break;
            case VideoPixelLayout.I444: layout = "yuv444p"; break;
            case VideoPixelLayout.Gray: layout = "gray"; break;
            default: layout = "unknown"; break;
        }

        return video.BitDepth is 0 or 8 ? layout : layout + video.BitDepth.ToString(CultureInfo.InvariantCulture) + "le";
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
