using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoProcessing;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// The media the export tests make and inspect: masters authored on the spot from FFmpeg's own generators, backdrop
/// clips and images, and the outside opinion on every MP4 - ffprobe for its streams and tags, ffmpeg for its pixels
/// and its sound, and a box walk for where the index sits. Nothing here is third-party media.
/// </summary>
internal static class ExportTestMedia
{
    /// <summary>The width of every authored master.</summary>
    public const int Width = 128;

    /// <summary>The height of every authored master.</summary>
    public const int Height = 72;

    /// <summary>The frame rate of every authored master.</summary>
    public const int FramesPerSecond = 10;

    private static readonly object Gate = new object();
    private static bool registered;

    /// <summary>
    /// Registers the decoder an export reads its pictures with - the TEST project's job, as it is an application's:
    /// AV1 from CodeBrix.VideoPlayback.Dav1d. Vorbis and FLAC are built into the audio core. Opus is NOT registered
    /// process-wide - other tests in this process assert that it is not - see <see cref="UseOpusDecoder" />.
    /// </summary>
    public static void RegisterDecoders()
    {
        lock (Gate)
        {
            if (registered) return;
            CodeBrixVideoPlaybackDav1d.Register();
            registered = true;
        }
    }

    /// <summary>
    /// Lets the export decode Opus in the calling flow only: the Opus package's own factory - what
    /// <c>CodeBrixAudioOpus.Register()</c> would add to the shared audio output process-wide - is offered to the
    /// export's ordinary codec lookup for this flow, so the lookup itself is what gets tested.
    /// </summary>
    /// <returns>Disposing it takes the factory away again.</returns>
    public static IDisposable UseOpusDecoder()
    {
        ExportDecoding.FlowPacketCodecFactories.Value = new IPacketCodecFactory[] { new OpusPacketCodecFactory() };
        return new OverrideScope();
    }

    private sealed class OverrideScope : IDisposable
    {
        public void Dispose() => ExportDecoding.FlowPacketCodecFactories.Value = null;
    }

    /// <summary>Skips the calling test when ffmpeg, ffprobe or an AV1 encoder is missing; registers the decoders otherwise.</summary>
    public static void Require()
    {
        SyntheticSource.SkipWithoutFFmpeg();
        RegisterDecoders();
    }

    /// <summary>The repository's tests/assets folder, found by walking up from the test assembly, or null.</summary>
    public static string RepositoryAssets
    {
        get
        {
            DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null)
            {
                string candidate = Path.Combine(folder.FullName, "tests", "assets");
                if (File.Exists(Path.Combine(candidate, "ASSETS.txt"))) return candidate;
                folder = folder.Parent;
            }

            return null;
        }
    }

    /// <summary>A file in the repository's tests/assets folder; skips the test when it cannot be found.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The path.</returns>
    public static string RepositoryAsset(string name)
    {
        string folder = RepositoryAssets;
        string path = folder == null ? null : Path.Combine(folder, name);
        Assert.SkipUnless(path != null && File.Exists(path), "The repository asset '" + name + "' cannot be found.");
        return path;
    }

    /// <summary>
    /// Writes a lossless source WITH alpha whose LEFT half is fully transparent black and whose RIGHT half is fully
    /// opaque white, optionally with a 440 Hz tone.
    /// </summary>
    /// <param name="path">Where (Matroska).</param>
    /// <param name="seconds">How long.</param>
    /// <param name="withSound">True for a tone.</param>
    /// <returns>The path.</returns>
    public static string WriteHalvesSource(string path, double seconds, bool withSound) =>
        WriteSource(path, seconds, withSound, "lum='16+219*floor(2*X/W)':cb='128':cr='128':a='255*floor(2*X/W)'");

    /// <summary>Writes a lossless source of one colour - Y 81, Cb 90, Cr 240 (BT.601 red) - at a constant alpha of 128.</summary>
    /// <param name="path">Where (Matroska).</param>
    /// <returns>The path.</returns>
    public static string WriteHalfTransparentRedSource(string path) =>
        WriteSource(path, 1, false, "lum='81':cb='90':cr='240':a='128'");

    private static string WriteSource(string path, double seconds, bool withSound, string expressions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string duration = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        string video = "color=c=black:size=" + Width + "x" + Height + ":rate=" + FramesPerSecond + ":duration=" + duration
            + ",format=yuva420p,geq=" + expressions;

        FFMpegArguments arguments = FFMpegArguments.FromFileInput(video, false, input => input.ForceFormat("lavfi"));
        if (withSound)
        {
            arguments = arguments.AddFileInput(
                "sine=frequency=440:sample_rate=48000:duration=" + duration, false, input => input.ForceFormat("lavfi"));
        }

        arguments
            .OutputToFile(path, true, output =>
            {
                output.WithVideoCodec("ffv1").ForceFormat("matroska");
                if (withSound) output.WithAudioCodec("pcm_s16le").WithCustomArgument("-ac 2");
            })
            .ProcessSynchronously();

        return path;
    }

    /// <summary>Authors a master (Mode3) file from a source, fast.</summary>
    /// <param name="source">The source.</param>
    /// <param name="output">The <c>.cbvmaster</c> to write.</param>
    /// <param name="work">The work folder.</param>
    /// <param name="audio">0 = the source's sound, 1 = flagged silent, 2 = no audio track and no flag.</param>
    /// <returns>The output path.</returns>
    public static string AuthorMaster(string source, string output, string work, int audio)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Master,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 20;
        request.Video.KeyframeIntervalFrames = 10;
        if (audio == 1) request.Audio.Silent = true;
        if (audio == 2) request.Audio.Include = false;

        CbvAuthor.Write(request);
        return output;
    }

    /// <summary>Writes a backdrop clip of one colour, of a given size and length, with an optional 1 kHz tone at 44.1 kHz.</summary>
    /// <param name="path">Where (Matroska).</param>
    /// <param name="colour">The colour, as FFmpeg names it (<c>0x00FF00</c>).</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="seconds">How long.</param>
    /// <param name="withSound">True for a tone.</param>
    /// <returns>The path.</returns>
    public static string WriteBackdropClip(string path, string colour, int width, int height, double seconds, bool withSound)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string duration = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        FFMpegArguments arguments = FFMpegArguments.FromFileInput(
            "color=c=" + colour + ":size=" + width + "x" + height + ":rate=" + FramesPerSecond + ":duration=" + duration,
            false,
            input => input.ForceFormat("lavfi"));
        if (withSound)
        {
            arguments = arguments.AddFileInput(
                "sine=frequency=1000:sample_rate=44100:duration=" + duration, false, input => input.ForceFormat("lavfi"));
        }

        arguments
            .OutputToFile(path, true, output =>
            {
                output.WithVideoCodec("ffv1").WithCustomArgument("-pix_fmt yuv444p").ForceFormat("matroska");
                if (withSound) output.WithAudioCodec("pcm_s16le");
            })
            .ProcessSynchronously();

        return path;
    }

    /// <summary>Writes a PNG of one colour with CodeBrix.Imaging.</summary>
    /// <param name="path">Where.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>The path.</returns>
    public static string WritePng(string path, int width, int height, byte r, byte g, byte b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using Image<Rgba32> image = new Image<Rgba32>(width, height, new Rgba32(r, g, b, 255));
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>Runs ffprobe on a file and returns its JSON report.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The report's root.</returns>
    public static JsonElement Probe(string path)
    {
        string json = RunText("ffprobe", "-v error -print_format json -show_format -show_streams \"" + path + "\"");
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The first stream of a kind in an ffprobe report, or null.</summary>
    /// <param name="probe">The report.</param>
    /// <param name="codecType"><c>video</c> or <c>audio</c>.</param>
    /// <returns>The stream.</returns>
    public static JsonElement? Stream(JsonElement probe, string codecType)
    {
        foreach (JsonElement stream in probe.GetProperty("streams").EnumerateArray())
        {
            if (stream.GetProperty("codec_type").GetString() == codecType) return stream;
        }

        return null;
    }

    /// <summary>A number ffprobe reports as a string.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    public static double Number(JsonElement element, string name) =>
        double.Parse(element.GetProperty(name).GetString(), CultureInfo.InvariantCulture);

    /// <summary>The MP4's title tag, or null.</summary>
    /// <param name="probe">The report.</param>
    /// <returns>The title.</returns>
    public static string Title(JsonElement probe)
    {
        JsonElement format = probe.GetProperty("format");
        if (!format.TryGetProperty("tags", out JsonElement tags)) return null;
        return tags.TryGetProperty("title", out JsonElement title) ? title.GetString() : null;
    }

    /// <summary>True when the MP4's <c>moov</c> box comes before its <c>mdat</c> box - fast start.</summary>
    /// <param name="path">The MP4.</param>
    /// <returns>True for fast start.</returns>
    public static bool MoovBeforeMdat(string path)
    {
        using FileStream file = File.OpenRead(path);
        byte[] header = new byte[16];
        long position = 0;
        int moov = -1;
        int mdat = -1;
        int index = 0;
        while (position + 8 <= file.Length)
        {
            file.Position = position;
            file.ReadExactly(header, 0, 8);
            long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                size = 0;
                for (int i = 8; i < 16; i++) size = (size << 8) | header[i];
            }
            else if (size == 0)
            {
                size = file.Length - position;
            }

            if (type == "moov" && moov < 0) moov = index;
            if (type == "mdat" && mdat < 0) mdat = index;
            if (size < 8) break;
            position += size;
            index++;
        }

        return moov >= 0 && mdat >= 0 && moov < mdat;
    }

    /// <summary>Decodes every picture of a file to packed RGB24 with ffmpeg.</summary>
    /// <param name="path">The file.</param>
    /// <param name="work">Where the raw file goes.</param>
    /// <returns>The bytes; frame <c>n</c> starts at <c>n * width * height * 3</c>.</returns>
    public static byte[] DecodeRgb(string path, string work)
    {
        string raw = Path.Combine(work, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".rgb");
        RunText("ffmpeg", "-v error -y -i \"" + path + "\" -f rawvideo -pix_fmt rgb24 \"" + raw + "\"");
        return File.ReadAllBytes(raw);
    }

    /// <summary>The mean colour of a box of pixels in one decoded frame.</summary>
    /// <param name="rgb">The decoded frames.</param>
    /// <param name="frame">The frame index.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="centreX">The box centre.</param>
    /// <param name="centreY">The box centre.</param>
    /// <returns>The mean red, green and blue.</returns>
    public static (int R, int G, int B) Mean(byte[] rgb, int frame, int width, int height, int centreX, int centreY)
    {
        long r = 0;
        long g = 0;
        long b = 0;
        int count = 0;
        long start = (long)frame * width * height * 3;
        for (int y = centreY - 4; y < centreY + 4; y++)
        {
            for (int x = centreX - 4; x < centreX + 4; x++)
            {
                long i = start + (((long)y * width) + x) * 3;
                r += rgb[i];
                g += rgb[i + 1];
                b += rgb[i + 2];
                count++;
            }
        }

        return ((int)(r / count), (int)(g / count), (int)(b / count));
    }

    /// <summary>Decodes a file's sound to mono 32-bit float PCM at its own rate with ffmpeg.</summary>
    /// <param name="path">The file.</param>
    /// <param name="work">Where the raw file goes.</param>
    /// <returns>The samples.</returns>
    public static float[] DecodeMono(string path, string work)
    {
        string raw = Path.Combine(work, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".f32");
        RunText("ffmpeg", "-v error -y -i \"" + path + "\" -vn -ac 1 -f f32le \"" + raw + "\"");
        byte[] bytes = File.ReadAllBytes(raw);
        float[] samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>The root-mean-square level of a stretch of samples.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="from">The first sample.</param>
    /// <param name="count">How many.</param>
    /// <returns>The level.</returns>
    public static double Rms(float[] samples, int from, int count)
    {
        double sum = 0;
        int end = Math.Min(samples.Length, from + count);
        for (int i = from; i < end; i++) sum += samples[i] * (double)samples[i];
        return end > from ? Math.Sqrt(sum / (end - from)) : 0;
    }

    private static string RunText(string program, string arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60000))
        {
            process.Kill(true);
            throw new TimeoutException(program + " " + arguments + " did not finish in a minute.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(program + " " + arguments + " failed: " + error.Result);
        }

        return output;
    }
}
