using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>The three sibling folders this tool writes, and the muxing each of them asks for.</summary>
public enum CorpusProfile
{
    /// <summary>Off-the-shelf Matroska: <c>-f matroska</c> with FFmpeg's default muxing, so the cues land wherever FFmpeg puts them.</summary>
    Matroska,

    /// <summary>Off-the-shelf WebM: <c>-f webm</c> with FFmpeg's default muxing.</summary>
    WebM,

    /// <summary>CodeBrix Video Mode1: <c>-f webm</c> with the cues moved to the FRONT, written with a <c>.cbv</c> extension.</summary>
    Mode1,
}

/// <summary>One of the two Public-Domain phone recordings the corpus is derived from.</summary>
public sealed class SourceClip
{
    /// <summary>The short key that starts every derived file's name - <c>landscape</c> or <c>portrait</c>.</summary>
    public string Key { get; set; }

    /// <summary>The file name inside the <c>MP4</c> folder.</summary>
    public string FileName { get; set; }

    /// <summary>
    /// True when the clip is stored landscape with rotation metadata and must therefore come out with TRUE
    /// portrait pixels - taller than it is wide, with no rotation left for a player to have to apply.
    /// </summary>
    public bool IsPortrait { get; set; }
}

/// <summary>One resolution rung, with the encoder settings chosen for it.</summary>
public sealed class OutputTier
{
    /// <summary>The short key that ends every derived file's name - <c>4k</c>, <c>hd</c> or <c>720p</c>.</summary>
    public string Key { get; set; }

    /// <summary>The LONG side of the frame in pixels: the width for a landscape clip, the height for a portrait one.</summary>
    public int LongSide { get; set; }

    /// <summary>The SHORT side of the frame in pixels.</summary>
    public int ShortSide { get; set; }

    /// <summary>The SVT-AV1 speed preset. The scale runs 0 (slowest, best) to 13 (fastest, worst).</summary>
    public int Preset { get; set; }

    /// <summary>The constant rate factor handed to SVT-AV1.</summary>
    public int Crf { get; set; }

    /// <summary>The Opus bit rate in kilobits per second.</summary>
    public int AudioKilobitsPerSecond { get; set; }
}

/// <summary>One output file: a source clip, a resolution rung and a container profile.</summary>
public sealed class CorpusItem
{
    /// <summary>The Public-Domain recording this file is derived from.</summary>
    public SourceClip Source { get; set; }

    /// <summary>The resolution rung and its encoder settings.</summary>
    public OutputTier Tier { get; set; }

    /// <summary>The container profile, which decides the folder, the muxer flags and the extension.</summary>
    public CorpusProfile Profile { get; set; }

    /// <summary>The output frame width in pixels - the TRUE pixel width, with rotation already applied.</summary>
    public int Width { get; set; }

    /// <summary>The output frame height in pixels - the TRUE pixel height, with rotation already applied.</summary>
    public int Height { get; set; }

    /// <summary>The folder the file is written into, relative to the authoring root.</summary>
    public string FolderName { get; set; }

    /// <summary>The output file's name, extension included.</summary>
    public string FileName { get; set; }

    /// <summary>The value handed to <c>-f</c>.</summary>
    public string ContainerFormat { get; set; }

    /// <summary>The relative path of the file inside the authoring folder, for logs and the manifest.</summary>
    public string RelativePath => FolderName + "/" + FileName;

    /// <summary>Resolves the output file's full path under an authoring root.</summary>
    /// <param name="authoringRoot">The folder that holds <c>MP4</c> and the generated siblings.</param>
    /// <returns>The full path the encoder writes to.</returns>
    public string ResolveOutputPath(string authoringRoot) =>
        Path.Combine(authoringRoot, FolderName, FileName);

    /// <summary>The frame size as it is written in logs and the manifest.</summary>
    public string Dimensions =>
        Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The whole corpus, as data: two clips by three resolutions by three container profiles, eighteen files.
/// </summary>
/// <remarks>
/// <para>
/// The encoder settings live here rather than in the command builder so that the table can be read - and
/// argued with - without reading any FFmpeg argument code. The reasoning behind the numbers:
/// </para>
/// <list type="bullet">
///   <item><description>
///     PRESET. SVT-AV1's speed preset runs 0 (slowest, best) to 13 (fastest, worst), and cost scales with the
///     pixel count, so the 2160p rung takes the faster preset (6) and the 720p rung the slower one (4). That
///     keeps the wall clock of a whole regeneration roughly flat across the three rungs instead of being
///     dominated by 4K, and the whole eighteen-file run inside a few minutes on a many-core machine.
///   </description></item>
///   <item><description>
///     CRF. AV1's rate factor is resolution-relative: the same number looks better the more pixels are
///     hiding the error. 28 at the 2160p rung, 26 at 1080p and 24 at 720p land the three rungs at a similar
///     perceived quality. These are DEMO assets meant to be LOOKED AT, not archival masters and not the
///     smallest thing that decodes, so the rate factors are chosen for a clip that still looks like the phone
///     recording it came from - around 6 Mbit/s at 2160p - while leaving every file an order of magnitude
///     under the twenty-five megabyte ceiling a single demo asset should not cross. Measured on the way here:
///     preset 8 / crf 38 gave a 12.5 MB corpus that was visibly soft at 2160p, and preset 6 / crf 30 gave
///     23.5 MB; this rung lands the whole eighteen-file corpus in the mid thirties.
///   </description></item>
///   <item><description>
///     KEYFRAME INTERVAL. Sixty frames - two seconds at 30 fps - on every rung. The interval is set
///     explicitly rather than left to the encoder's default (which is far longer) because these files exist
///     to be SEEKED: a cue every two seconds is what makes a scrub land quickly.
///   </description></item>
///   <item><description>
///     AUDIO. 128 kbit/s stereo Opus at the 2160p and 1080p rungs, 96 kbit/s at 720p - the 720p rung is the
///     "small file" rung and the audio should shrink with the picture. 48 kHz throughout, which is Opus's
///     own internal rate and the rate both sources were recorded at, so nothing is resampled.
///   </description></item>
/// </list>
/// </remarks>
public static class CorpusPlan
{
    /// <summary>The keyframe interval in frames - two seconds at 30 frames per second.</summary>
    public const int KeyframeIntervalFrames = 60;

    /// <summary>The frame rate every output is conformed to.</summary>
    public const int FramesPerSecond = 30;

    /// <summary>The Opus sample rate every output uses.</summary>
    public const int AudioSampleRateHz = 48000;

    /// <summary>The channel count every output uses.</summary>
    public const int AudioChannels = 2;

    /// <summary>The pixel format every output uses - the one the streamable profile recommends.</summary>
    public const string PixelFormat = "yuv420p";

    /// <summary>The FFmpeg encoder name for the video.</summary>
    public const string VideoEncoder = "libsvtav1";

    /// <summary>The FFmpeg encoder name for the audio.</summary>
    public const string AudioEncoder = "libopus";

    /// <summary>The scaler the resize filter is asked for.</summary>
    public const string ScalerFlags = "lanczos";

    /// <summary>The two Public-Domain recordings, in the order they are processed.</summary>
    public static IReadOnlyList<SourceClip> Sources { get; } = new[]
    {
        new SourceClip { Key = "landscape", FileName = "landscape_video_test_4k.mp4", IsPortrait = false },
        new SourceClip { Key = "portrait", FileName = "portrait_video_test_4k.mp4", IsPortrait = true },
    };

    /// <summary>The three resolution rungs, biggest first.</summary>
    public static IReadOnlyList<OutputTier> Tiers { get; } = new[]
    {
        new OutputTier { Key = "4k", LongSide = 3840, ShortSide = 2160, Preset = 6, Crf = 28, AudioKilobitsPerSecond = 128 },
        new OutputTier { Key = "hd", LongSide = 1920, ShortSide = 1080, Preset = 5, Crf = 26, AudioKilobitsPerSecond = 128 },
        new OutputTier { Key = "720p", LongSide = 1280, ShortSide = 720, Preset = 4, Crf = 24, AudioKilobitsPerSecond = 96 },
    };

    /// <summary>Builds the whole eighteen-file plan.</summary>
    /// <returns>Every file the tool produces, grouped folder by folder in the order they are written.</returns>
    public static IReadOnlyList<CorpusItem> Build()
    {
        List<CorpusItem> items = new List<CorpusItem>(18);

        foreach (CorpusProfile profile in new[] { CorpusProfile.Matroska, CorpusProfile.WebM, CorpusProfile.Mode1 })
        {
            foreach (SourceClip source in Sources)
            {
                foreach (OutputTier tier in Tiers)
                {
                    // A portrait source is STORED landscape with a rotation in its metadata. The rotation is
                    // applied while decoding, so the long side of the frame becomes the HEIGHT here and the
                    // file that comes out has true portrait pixels with nothing left for a player to rotate.
                    int width = source.IsPortrait ? tier.ShortSide : tier.LongSide;
                    int height = source.IsPortrait ? tier.LongSide : tier.ShortSide;

                    items.Add(new CorpusItem
                    {
                        Source = source,
                        Tier = tier,
                        Profile = profile,
                        Width = width,
                        Height = height,
                        FolderName = FolderFor(profile),
                        FileName = source.Key + "_" + tier.Key + ExtensionFor(profile),
                        ContainerFormat = ContainerFormatFor(profile),
                    });
                }
            }
        }

        return items;
    }

    /// <summary>The folder a profile's files are written into.</summary>
    /// <param name="profile">The container profile.</param>
    /// <returns>The folder name, relative to the authoring root.</returns>
    public static string FolderFor(CorpusProfile profile)
    {
        switch (profile)
        {
            case CorpusProfile.Matroska: return "MKV";
            case CorpusProfile.WebM: return "WebM";
            case CorpusProfile.Mode1: return "CodeBrix-Mode1";
            default: throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    /// <summary>The file extension a profile's files carry.</summary>
    /// <param name="profile">The container profile.</param>
    /// <returns>The extension, leading dot included.</returns>
    public static string ExtensionFor(CorpusProfile profile)
    {
        switch (profile)
        {
            case CorpusProfile.Matroska: return ".mkv";
            case CorpusProfile.WebM: return ".webm";
            case CorpusProfile.Mode1: return ".cbv";
            default: throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    /// <summary>The value a profile hands to FFmpeg's <c>-f</c>.</summary>
    /// <param name="profile">The container profile.</param>
    /// <returns>The muxer name.</returns>
    public static string ContainerFormatFor(CorpusProfile profile)
    {
        switch (profile)
        {
            case CorpusProfile.Matroska: return "matroska";
            case CorpusProfile.WebM: return "webm";

            // Mode1 IS WebM - the same muxer, the same doctype, the same streams. The only difference is
            // where the cues sit and what the file is called.
            case CorpusProfile.Mode1: return "webm";
            default: throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }
}
