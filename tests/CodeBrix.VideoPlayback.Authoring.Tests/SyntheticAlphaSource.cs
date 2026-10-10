using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Makes the synthetic sources the master (Mode3) tests author from: a clip WITH an alpha channel, and a
/// plain FLAC stream - out of FFmpeg's own generators, nothing downloaded and nothing third-party.
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
    public static string WriteClip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string size = SyntheticSource.Width.ToString(CultureInfo.InvariantCulture) + "x"
            + SyntheticSource.Height.ToString(CultureInfo.InvariantCulture);
        string duration = SyntheticSource.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + SyntheticSource.FramesPerSecond.ToString(CultureInfo.InvariantCulture)
            + ":duration=" + duration
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

    /// <summary>Writes a plain <c>.flac</c> file of a tone, with FFmpeg's native encoder.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="bitDepth">16 or 24.</param>
    /// <param name="seconds">How long.</param>
    /// <returns>The path.</returns>
    public static string WriteFlac(string path, int bitDepth, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string audio = "sine=frequency=440:sample_rate=48000:duration="
            + seconds.ToString("0.###", CultureInfo.InvariantCulture);

        FFMpegArguments
            .FromFileInput(audio, false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output => output
                .WithAudioCodec("flac")
                .WithCustomArgument(bitDepth == 24 ? "-sample_fmt s32 -bits_per_raw_sample 24" : "-sample_fmt s16")
                .WithCustomArgument("-ac 2")
                .ForceFormat("flac"))
            .ProcessSynchronously();

        return path;
    }
}
