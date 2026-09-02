using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Tools;

/// <summary>
/// The <c>cbvmux</c> verb: builds a bespoke <c>.cbv</c> file out of an encoder's IVF and Ogg output, plus
/// caption and chapter files.
/// </summary>
/// <remarks>
/// It is the command-line face of <see cref="CbvAuthoring" />, and it is how the committed <c>.cbv</c> samples
/// under <c>tests/assets</c> are regenerated. Nothing but an encoder is needed to produce its inputs.
/// </remarks>
public static class CbvMuxCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="args">The switches describing the inputs and the output.</param>
    /// <returns>0 when the file was written, 1 when it could not be, 2 when the command line was wrong.</returns>
    public static int Run(string[] args)
    {
        CbvAuthoringRequest request = new CbvAuthoringRequest();
        string synthetic = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (!TryTake(args, ref i, out string output)) return Missing(arg);
                    request.OutputPath = output;
                    break;

                case "--video":
                    if (!TryTake(args, ref i, out string video)) return Missing(arg);
                    request.VideoIvfPath = video;
                    break;

                case "--audio":
                    if (!TryTake(args, ref i, out string audio)) return Missing(arg);
                    request.AudioOggPath = audio;
                    break;

                case "--chapters":
                    if (!TryTake(args, ref i, out string chapters)) return Missing(arg);
                    request.ChaptersPath = chapters;
                    break;

                case "--audio-language":
                    if (!TryTake(args, ref i, out string audioLanguage)) return Missing(arg);
                    request.AudioLanguage = audioLanguage;
                    break;

                case "--audio-name":
                    if (!TryTake(args, ref i, out string audioName)) return Missing(arg);
                    request.AudioName = audioName;
                    break;

                case "--video-name":
                    if (!TryTake(args, ref i, out string videoName)) return Missing(arg);
                    request.VideoName = videoName;
                    break;

                case "--captions":
                    if (!TryTake(args, ref i, out string captions)) return Missing(arg);
                    if (!TryAddCaptions(request, captions)) return 2;
                    break;

                case "--synthetic-video":
                    if (!TryTake(args, ref i, out synthetic)) return Missing(arg);
                    break;

                default:
                    Console.Error.WriteLine($"cbvmux: '{arg}' is not a switch this verb knows.");
                    WriteUsage();
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            Console.Error.WriteLine("cbvmux: --output is required.");
            WriteUsage();
            return 2;
        }

        if (RefusesOpusAudio(request.AudioOggPath)) return 1;

        if (synthetic != null) return WriteSyntheticVideo(request, synthetic);

        CbvAuthoringResult result = CbvAuthoring.Write(request);
        Console.WriteLine(result.ToString());
        return 1 - Math.Sign(new FileInfo(result.Path).Length);
    }

    // THE BESPOKE FILE'S REASON TO EXIST. This verb writes the bespoke container and nothing else, and a
    // bespoke ".cbv" has to play with CodeBrix.VideoPlayback - whose CodeBrix.Audio dependency has Vorbis
    // built in - plus a video decoder package, and nothing else. An Ogg full of Opus would put a third
    // package between the file and the machine that plays it, so it is refused here rather than muxed. The
    // core muxer itself stays permissive on purpose: it is the vehicle for the packet-seam session tests.
    private static bool RefusesOpusAudio(string oggPath)
    {
        if (string.IsNullOrWhiteSpace(oggPath) || !File.Exists(oggPath)) return false;

        string codec;

        try
        {
            using OggAudioStream audio = OggAudioStream.Open(oggPath);
            codec = audio.CodecId;
        }
        catch (VideoPlaybackException)
        {
            // Not a readable Ogg stream: let the ordinary path report that in its own words.
            return false;
        }

        if (!string.Equals(codec, VideoCodecIds.Opus, StringComparison.Ordinal)) return false;

        Console.Error.WriteLine(
            $"cbvmux: '{oggPath}' carries Opus audio, and a bespoke '.cbv' file must play with "
            + "CodeBrix.VideoPlayback and a video decoder package and NOTHING else. Opus needs the playing "
            + "application to reference CodeBrix.Audio.Opus and call CodeBrixAudioOpus.Register(); Vorbis "
            + "plays with the core package alone. Encode the sound as Vorbis, or author the WebM-profile "
            + "flavour with the authoring library, where Opus is the default and is fully supported.");

        return true;
    }

    private static int WriteSyntheticVideo(CbvAuthoringRequest request, string specification)
    {
        if (!TryParseSynthetic(specification, out int frames, out int width, out int height, out double frameRate))
        {
            Console.Error.WriteLine(
                "cbvmux: --synthetic-video takes <frames>x<width>x<height>@<fps>, for example 60x64x36@25.");
            return 2;
        }

        RawVideoDescriptor descriptor = new RawVideoDescriptor(
            width,
            height,
            8,
            VideoPixelLayout.I420,
            new VideoColorInfo(
                VideoColorPrimaries.Bt709,
                VideoTransferCharacteristics.Bt709,
                VideoMatrixCoefficients.Bt709,
                VideoColorRange.Limited,
                VideoChromaSiting.Vertical));

        TimeSpan frameDuration = TimeSpan.FromSeconds(1.0 / frameRate);

        using CbvMuxer muxer = CbvMuxer.Create(request.OutputPath);
        int track = muxer.AddVideoTrack(
            VideoCodecIds.Raw,
            RawVideoFormat.CreateDescriptor(descriptor),
            width,
            height,
            width,
            height,
            8,
            VideoPixelLayout.I420,
            descriptor.Color,
            null,
            frameDuration,
            null,
            string.IsNullOrEmpty(request.VideoName) ? "synthetic uncompressed video" : request.VideoName);

        if (request.ChaptersPath != null)
        {
            muxer.AddChapters(Chapters.FfMetadataChapters.ReadFile(request.ChaptersPath));
        }

        // --audio is honoured here as well as on the ordinary authoring path. It used to be accepted and
        // then silently dropped, which produced a file that looked right and had no sound in it.
        int audioTrack = 0;
        List<TimedChunk> audioChunks = new List<TimedChunk>();
        string audioCodec = null;

        if (!string.IsNullOrWhiteSpace(request.AudioOggPath))
        {
            using OggAudioStream audio = OggAudioStream.Open(request.AudioOggPath);

            audioCodec = audio.CodecId;

            TimeSpan codecDelay = audio.PreSkipSamples > 0 && audio.SampleRate > 0
                ? TimeSpan.FromTicks((long)audio.PreSkipSamples * TimeSpan.TicksPerSecond / audio.SampleRate)
                : TimeSpan.Zero;

            TimeSpan seekPreRoll = string.Equals(audio.CodecId, VideoCodecIds.Opus, StringComparison.Ordinal)
                ? TimeSpan.FromMilliseconds(80)
                : TimeSpan.Zero;

            while (audio.TryReadPacket(out OggAudioPacket packet))
            {
                audioChunks.Add(new TimedChunk(packet.Data.ToArray(), packet.Timestamp, packet.Duration, true));
            }

            audioTrack = muxer.AddAudioTrack(
                audio.CodecId,
                audio.CodecPrivate,
                audio.SampleRate,
                audio.Channels,
                audio.PreSkipSamples,
                audio.TrailingTrimSamples,
                codecDelay,
                seekPreRoll,
                request.AudioLanguage,
                request.AudioName);
        }

        byte[] frame = new byte[RawVideoFormat.GetFrameByteCount(descriptor)];
        int audioIndex = 0;

        for (int i = 0; i < frames; i++)
        {
            TimeSpan timestamp = TimeSpan.FromTicks(frameDuration.Ticks * i);

            // Interleave: every audio packet that belongs before this video frame goes out first, so the
            // file plays without the reader having to look ahead.
            while (audioIndex < audioChunks.Count && audioChunks[audioIndex].Timestamp <= timestamp)
            {
                TimedChunk chunk = audioChunks[audioIndex++];
                muxer.WriteChunk(audioTrack, chunk.Data, chunk.Timestamp, chunk.Duration, true);
            }

            FillFrame(frame, descriptor, i);
            muxer.WriteChunk(track, frame, timestamp, frameDuration, i % 10 == 0);
        }

        while (audioIndex < audioChunks.Count)
        {
            TimedChunk chunk = audioChunks[audioIndex++];
            muxer.WriteChunk(audioTrack, chunk.Data, chunk.Timestamp, chunk.Duration, true);
        }

        muxer.Complete();

        Console.WriteLine(
            $"{request.OutputPath}: {new FileInfo(request.OutputPath).Length:N0} bytes, {frames} uncompressed "
            + $"{width}x{height} frames at {frameRate.ToString("0.###", CultureInfo.InvariantCulture)} per second"
            + (audioCodec == null
                ? ", no audio"
                : $", {audioChunks.Count} {audioCodec} audio packets"));

        return 0;
    }

    /// <summary>One packet on its way into the file, waiting for its turn in presentation order.</summary>
    private readonly struct TimedChunk
    {
        internal TimedChunk(byte[] data, TimeSpan timestamp, TimeSpan duration, bool isKeyFrame)
        {
            Data = data;
            Timestamp = timestamp;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        internal byte[] Data { get; }

        internal TimeSpan Timestamp { get; }

        internal TimeSpan Duration { get; }

        internal bool IsKeyFrame { get; }
    }

    private static void FillFrame(byte[] frame, in RawVideoDescriptor descriptor, int frameNumber)
    {
        int offset = 0;
        for (int plane = 0; plane < 3; plane++)
        {
            int width = RawVideoFormat.GetPlaneWidth(descriptor, plane);
            int height = RawVideoFormat.GetPlaneHeight(descriptor, plane);
            if (width == 0 || height == 0) continue;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    frame[offset++] = plane == 0
                        ? (byte)(16 + ((frameNumber * 3 + x + y) % 220))
                        : (byte)(128 + ((plane == 1 ? x : y) % 40) - 20);
                }
            }
        }
    }

    private static bool TryParseSynthetic(
        string specification,
        out int frames,
        out int width,
        out int height,
        out double frameRate)
    {
        frames = 0;
        width = 0;
        height = 0;
        frameRate = 25;

        string[] halves = specification.Split('@');
        string[] parts = halves[0].Split('x');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)) return false;

        if (halves.Length > 1
            && !double.TryParse(halves[1], NumberStyles.Float, CultureInfo.InvariantCulture, out frameRate))
        {
            return false;
        }

        return frames > 0 && width > 0 && height > 0 && frameRate > 0;
    }

    private static bool TryAddCaptions(CbvAuthoringRequest request, string specification)
    {
        string[] parts = specification.Split(':');
        if (parts.Length < 2)
        {
            Console.Error.WriteLine(
                "cbvmux: --captions takes <path>:<bcp47>[:<name>[:<flags>]], where flags is any of "
                + "default, forced and sdh joined by '+'.");
            return false;
        }

        CaptionTrackFlags flags = CaptionTrackFlags.None;
        if (parts.Length >= 4)
        {
            foreach (string flag in parts[3].Split('+'))
            {
                switch (flag.Trim().ToLowerInvariant())
                {
                    case "default":
                        flags |= CaptionTrackFlags.Default;
                        break;

                    case "forced":
                        flags |= CaptionTrackFlags.Forced;
                        break;

                    case "sdh":
                    case "hearing-impaired":
                        flags |= CaptionTrackFlags.HearingImpaired;
                        break;

                    case "":
                        break;

                    default:
                        Console.Error.WriteLine($"cbvmux: '{flag}' is not a caption flag this verb knows.");
                        return false;
                }
            }
        }

        request.Captions.Add(new CbvCaptionInput(
            parts[0],
            parts[1],
            parts.Length >= 3 ? parts[2] : null,
            flags));

        return true;
    }

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static int Missing(string argument)
    {
        Console.Error.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "cbvmux: {0} needs a value.", argument));
        return 2;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine("  cbvmux --output <out.cbv> [--video <in.ivf>] [--audio <in.ogg>]");
        Console.Error.WriteLine("         [--chapters <chapters.ffmeta>] [--audio-language <bcp47>]");
        Console.Error.WriteLine("         [--audio-name <name>] [--video-name <name>]");
        Console.Error.WriteLine("         [--captions <path>:<bcp47>[:<name>[:default+forced+sdh]]] ...");
        Console.Error.WriteLine("         [--synthetic-video <frames>x<width>x<height>@<fps>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  The --audio stream must be Vorbis. A bespoke '.cbv' file plays with");
        Console.Error.WriteLine("  CodeBrix.VideoPlayback and a video decoder package and nothing else, and");
        Console.Error.WriteLine("  Opus would need CodeBrix.Audio.Opus on the machine that plays it.");
    }
}
