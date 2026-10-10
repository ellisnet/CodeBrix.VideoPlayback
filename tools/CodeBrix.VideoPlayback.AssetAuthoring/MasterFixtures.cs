using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback.Authoring;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoProcessing;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>
/// Writes the two small master (Mode3, <c>.cbvmaster</c>) fixtures under <c>tests/assets</c> from synthetic
/// sources, through the authoring library's own master flavour.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is third-party media: the picture is FFmpeg's <c>testsrc2</c> pattern, the alpha channel a
/// <c>geq</c> horizontal gradient (transparent on the left, opaque on the right), and the sound a 440 Hz
/// <c>sine</c> tone. The source is kept in a lossless intermediate (FFV1 in Matroska, <c>yuva420p</c>, PCM
/// audio) in a temporary folder that is deleted afterwards.
/// </para>
/// <list type="bullet">
///   <item><description><c>av1-alpha-flac.cbvmaster</c> - picture, alpha plane and a FLAC track.</description></item>
///   <item><description><c>av1-flac.cbvmaster</c> - the same picture and sound with the alpha left out.</description></item>
/// </list>
/// <code>
/// dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --master-fixtures
/// </code>
/// </remarks>
internal static class MasterFixtures
{
    /// <summary>The fixture width.</summary>
    internal const int Width = 128;

    /// <summary>The fixture height.</summary>
    internal const int Height = 72;

    /// <summary>The fixture frame rate.</summary>
    internal const int FramesPerSecond = 12;

    /// <summary>The fixture length, in seconds.</summary>
    internal const double DurationSeconds = 1.0;

    /// <summary>The key-frame interval, in frames.</summary>
    internal const int KeyFrameInterval = 4;

    /// <summary>Writes both fixtures into a folder.</summary>
    /// <param name="assetsFolder">The folder - normally the repository's <c>tests/assets</c>.</param>
    /// <returns>0 on success, 1 on failure.</returns>
    internal static int Write(string assetsFolder)
    {
        string work = Path.Combine(Path.GetTempPath(), "codebrix-master-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            string source = WriteSource(Path.Combine(work, "source.mkv"));

            foreach ((string name, AuthoringAlphaMode alpha) in new[]
                     {
                         ("av1-alpha-flac.cbvmaster", AuthoringAlphaMode.Auto),
                         ("av1-flac.cbvmaster", AuthoringAlphaMode.Exclude),
                     })
            {
                VideoAuthoringRequest request = new VideoAuthoringRequest
                {
                    Flavour = VideoAuthoringFlavour.Master,
                    SourcePath = source,
                    OutputPath = Path.Combine(assetsFolder, name),
                    TemporaryFolder = work,
                    AllowAv1EncoderFallback = true,
                };

                request.Video.SpeedPreset = 8;
                request.Video.ConstantRateFactor = 50;
                request.Video.KeyframeIntervalFrames = KeyFrameInterval;
                request.Video.Alpha = alpha;
                request.Audio.SampleRateHz = 48000;
                request.Audio.Channels = 2;

                VideoAuthoringResult result = CbvAuthor.Write(request);
                Console.WriteLine(
                    name + "  " + result.SizeInBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
                foreach (string note in result.Notes) Console.WriteLine("    note: " + note);
                foreach (AuthoringCommand command in result.Commands) Console.WriteLine("    [" + command.Label + "] ffmpeg " + command.Arguments);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("master fixtures: " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes the lossless synthetic source: testsrc2 with a horizontal alpha gradient, and a sine tone.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>The path.</returns>
    internal static string WriteSource(string path)
    {
        string size = Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
        string duration = DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string video = "testsrc2=size=" + size
            + ":rate=" + FramesPerSecond.ToString(CultureInfo.InvariantCulture)
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
}
