using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Makes the synthetic sources the master (Mode3) tests author from - a clip WITH an alpha channel, and one
/// whose transparent half hides black under it - out of FFmpeg's own generators, nothing downloaded and nothing
/// third-party; and decodes sound back to raw PCM for comparison.
/// </summary>
/// <remarks>
/// The alpha channel is a horizontal gradient - 0 (transparent) in the left column, 255 (opaque) in the right
/// one - in every frame, so a decoded alpha plane can be checked column by column. The clip is stored losslessly
/// (FFV1, <c>yuva420p</c>, PCM) so nothing is lost before the library under test encodes it.
/// </remarks>
public static class SyntheticAlphaSource
{
    /// <summary>Writes the clip with an alpha channel and a tone.</summary>
    /// <param name="path">Where to write it (Matroska).</param>
    /// <returns>The path.</returns>
    public static string WriteClip(string path) => WriteClip(path, SyntheticSource.Width, SyntheticSource.Height);

    /// <summary>Writes the clip with an alpha channel and a tone at a given size - odd sizes included.</summary>
    /// <param name="path">Where to write it (Matroska).</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <returns>The path.</returns>
    public static string WriteClip(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // testsrc2 rounds an odd size down to even, so an odd clip is made one larger and cropped exactly.
        bool odd = (width & 1) != 0 || (height & 1) != 0;
        string size = ((width + 1) & ~1).ToString(CultureInfo.InvariantCulture) + "x"
            + ((height + 1) & ~1).ToString(CultureInfo.InvariantCulture);
        string crop = odd
            ? ",crop=" + width.ToString(CultureInfo.InvariantCulture) + ":"
                + height.ToString(CultureInfo.InvariantCulture) + ":0:0:exact=1"
            : string.Empty;
        string duration = SyntheticSource.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + SyntheticSource.FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=" + duration
            + crop
            + ",format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='255*X/(W-1)'";
        string audio = "sine=frequency=440:sample_rate=48000:duration=" + duration;

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .AddFileInput(audio, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => output
                .WithVideoCodec("ffv1")
                .WithAudioCodec("pcm_s16le")
                .WithCustomArgument("-ac 2")
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        return path;
    }

    /// <summary>
    /// Writes a silent clip whose LEFT half is fully transparent and black and whose RIGHT half is fully opaque
    /// and white - the source the transparent-pixel fill is checked against.
    /// </summary>
    /// <param name="path">Where to write it (Matroska, FFV1 <c>yuva420p</c>).</param>
    /// <returns>The path.</returns>
    public static string WriteEdgeClip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string size = SyntheticSource.Width.ToString(CultureInfo.InvariantCulture) + "x"
            + SyntheticSource.Height.ToString(CultureInfo.InvariantCulture);

        // floor(2*X/W) is 0 across the left half and 1 across the right one; the expressions carry no comma,
        // so nothing in them needs escaping for the filter graph.
        string video = "color=c=black:size=" + size
            + ":rate=" + SyntheticSource.FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=1"
            + ",format=yuva420p,geq=lum='16+219*floor(2*X/W)':cb='128':cr='128':a='255*floor(2*X/W)'";

        FFMpegArguments
            .FromFileInput(video, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => output
                .WithVideoCodec("ffv1")
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        return path;
    }

    /// <summary>
    /// Writes the clip with an alpha channel and stamps it with a quarter-turn display rotation, the way a phone
    /// recording held upright is stored.
    /// </summary>
    /// <param name="path">Where to write it (Matroska).</param>
    /// <returns>The path.</returns>
    public static string WriteRotatedClip(string path)
    {
        string stored = WriteClip(Path.Combine(Path.GetDirectoryName(path), "unrotated-" + Path.GetFileName(path)));

        FFMpegArguments
            .FromFileInput(stored, false, input => input.WithCustomArgument("-display_rotation 90"))
            .OutputToFile(path, true, output => output
                .CopyChannel()
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        return path;
    }

    /// <summary>Decodes a file's first sound stream to raw 16-bit little-endian PCM and returns the bytes.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="rawPath">Where the raw PCM is written on the way.</param>
    /// <returns>The samples, interleaved.</returns>
    public static byte[] DecodeSound(string path, string rawPath)
    {
        FFMpegArguments
            .FromFileInput(path, false)
            .OutputToFile(rawPath, true, output => output
                .WithCustomArgument("-vn -map 0:a:0")
                .WithAudioCodec("pcm_s16le")
                .ForceFormat("s16le"))
            .ProcessSynchronously();

        return File.ReadAllBytes(rawPath);
    }
}
