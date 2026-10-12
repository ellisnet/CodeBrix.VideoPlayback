using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using CodeBrix.Audio.FlacWriter;
using CodeBrix.Audio.Opus;
using CodeBrix.Audio.Vorbis;
using CodeBrix.VideoEncoding.Av1;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Encoding;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Core.Output;

// Both the playback core and the encoder contract name their codec-id class VideoCodecIds; the container's is
// the one a FLAC track is declared with.
using ContainerCodecIds = CodeBrix.VideoPlayback.Decoding.VideoCodecIds;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// The in-process passes of the master (<c>.cbvmaster</c>, Mode3) flavour - the picture, the alpha plane and the
/// lossless sound - of the bespoke (Mode2) flavour's managed path - the picture and its Vorbis sound - and of the
/// WebM-profile (Mode1) flavour's managed path - the picture and its Opus sound - decoded through whichever
/// video-processing engine the application registered and encoded in process.
/// </summary>
/// <remarks>
/// <para>
/// THE ENGINE. The one the application registered with <c>VideoProcessors</c> - and ONLY that one: with none
/// registered, authoring refuses (<see cref="NoEngineRegistered" />). A library does not choose the process's
/// engine. Everything below goes through the engine-neutral contract only, so the same passes run on any engine
/// that can decode the source, Android's included.
/// </para>
/// <para>
/// THE ENCODERS. AV1 for the picture and the alpha plane, FLAC, Vorbis or Opus for the sound - all in process,
/// none needing anything installed.
/// </para>
/// </remarks>
internal static class InProcessAuthoring
{
    /// <summary>What the dry run and the result call the optional ffmpeg pass that shapes the source.</summary>
    internal const string PreparePassLabel = "prepare pass";

    /// <summary>The label of the picture pass.</summary>
    internal const string VideoPassLabel = "video pass";

    /// <summary>The label of the alpha pass.</summary>
    internal const string AlphaPassLabel = "alpha pass";

    /// <summary>The label of the sound pass.</summary>
    internal const string AudioPassLabel = "audio pass";

    /// <summary>The label of the WebM-profile flavour's managed mux, listed after its encoding passes.</summary>
    internal const string MuxPassLabel = "mux pass";

    /// <summary>The one rate Opus codes at, and so the rate the in-process Opus pass decodes its sound at.</summary>
    internal const int OpusSampleRate = 48000;

    /// <summary>The Opus encoder complexity the in-process pass uses - libopus's own default, as ffmpeg leaves it.</summary>
    internal const int OpusComplexity = 10;

    /// <summary>The most channels the in-process Opus encoder takes (channel mapping family 0).</summary>
    internal const int MaximumOpusChannels = 2;

    /// <summary>The length of an Opus identification header with channel mapping family 0.</summary>
    internal const int OpusHeadLength = 19;

    /// <summary>The FLAC compression level the sound is written at - the reference encoder's own default.</summary>
    internal const int FlacCompressionLevel = 5;

    /// <summary>The largest libaom speed setting the master flavour asks for.</summary>
    internal const int MaximumCpuUsed = 8;

    private const int AudioChunkFrames = 4096;

    /// <summary>
    /// Lets a test decide whether the ffmpeg path counts as available, for the flow it runs in only; null - the
    /// default - asks the machine.
    /// </summary>
    internal static readonly AsyncLocal<bool?> FFmpegPathAvailableOverride = new AsyncLocal<bool?>();

    /// <summary>
    /// Lets a test hand the in-process passes an engine of its own, for the flow it runs in only, WITHOUT
    /// registering it process-wide (the registry cannot take an engine back out); null - the default - resolves
    /// the engine as an application's run does.
    /// </summary>
    internal static readonly AsyncLocal<IVideoProcessor> EngineOverride = new AsyncLocal<IVideoProcessor>();

    /// <summary>
    /// Lets a test see the library behave as if NO engine were registered, for the flow it runs in only, without
    /// touching the process-wide registry (which cannot take an engine back out, and in which other tests have
    /// registered the ffmpeg engine). False - the default - asks the registry. <see cref="EngineOverride" /> still
    /// wins over it.
    /// </summary>
    internal static readonly AsyncLocal<bool> NothingRegisteredOverride = new AsyncLocal<bool>();

    /// <summary>What every authoring entry point says when the application registered no engine.</summary>
    internal const string NoEngineRegistered =
        "Authoring decodes through the registered video-processing engine and none is registered. Call "
        + "CodeBrixVideoProcessing.Register() (the ffmpeg engine, desktop) or CodeBrixVideoProcessingAndroid.Register() "
        + "(Android) once at start-up.";

    /// <summary>
    /// Decides whether a bespoke request runs as FFmpeg command lines or in process, and why when it falls back.
    /// </summary>
    /// <param name="request">The bespoke request.</param>
    /// <param name="probeBinaries">
    /// True to have the engine look for <c>ffmpeg</c> and <c>ffprobe</c> to find out whether they are there; false - a dry run - to
    /// judge from the registered engine alone.
    /// </param>
    /// <param name="fallbackReason">
    /// Why an FFmpeg encoder request is being encoded in process instead, or null when it is not, or when the
    /// request asked for <see cref="AuthoringVideoEncoder.Av1Package" /> outright.
    /// </param>
    /// <returns>True for the FFmpeg command lines.</returns>
    /// <remarks>
    /// <see cref="AuthoringVideoEncoder.Av1Package" /> ALWAYS runs in process. Either FFmpeg encoder runs as
    /// FFmpeg command lines when the ffmpeg engine is the one in use and can be run, and FALLS BACK to the
    /// in-process path otherwise - no ffmpeg on the machine, or another engine registered (Android's).
    /// </remarks>
    internal static bool UsesFFmpegPath(VideoAuthoringRequest request, bool probeBinaries, out string fallbackReason)
    {
        fallbackReason = null;
        if (request.Video.Encoder == AuthoringVideoEncoder.Av1Package) return false;

        string asked = request.Video.Encoder == AuthoringVideoEncoder.LibAomAv1
            ? AuthoringEncoderNames.LibAomAv1
            : AuthoringEncoderNames.LibSvtAv1;
        bool? forced = FFmpegPathAvailableOverride.Value;
        IVideoProcessor engine = ResolveEngine();

        if (forced.HasValue)
        {
            if (forced.Value) return true;
            fallbackReason = "the ffmpeg path is not available here";
        }
        else if (!IsFFmpegEngine(engine))
        {
            fallbackReason = "the registered video-processing engine is '" + engine.Name + "', not ffmpeg";
        }
        else if (probeBinaries && !AuthoringTools.TryVerify(engine, out _, out _))
        {
            fallbackReason = "ffmpeg and ffprobe cannot be run on this machine";
        }
        else
        {
            return true;
        }

        fallbackReason = "the request asked for " + asked + " through ffmpeg, and " + fallbackReason + ", so the "
            + "picture and the sound were encoded in process instead, exactly as Video.Encoder = "
            + "AuthoringVideoEncoder.Av1Package would: AV1 by the in-process encoder (libaom at cpu-used "
            + Number(CpuUsedFor(request.Video)) + ", cq-level " + Number(request.Video.ConstantRateFactor)
            + " - slower than SVT-AV1, and the same rate factor does not give identical quality) and "
            + DescribeInProcessSound(request) + ".";
        return false;
    }

    // What the fallback note says becomes of the sound: the bespoke flavour's Vorbis (its text is unchanged from
    // before the WebM-profile flavour had an in-process path), the WebM-profile flavour's Opus or - in a Matroska
    // document that asked for it - Vorbis, then the managed WebM / Matroska muxer.
    private static string DescribeInProcessSound(VideoAuthoringRequest request)
    {
        if (request.Flavour != VideoAuthoringFlavour.WebMProfile) return "Vorbis by the in-process Vorbis encoder";

        string muxer = " - and the file was muxed by the managed "
            + (request.Container == AuthoringContainerFormat.Matroska ? "Matroska" : "WebM") + " muxer";
        if (!request.Audio.Include) return "no sound, as asked" + muxer;

        return (IsOpus(request) ? "Opus by the in-process Opus encoder" : "Vorbis by the in-process Vorbis encoder")
            + muxer;
    }

    /// <summary>True when a request's sound resolves to Opus - the WebM-profile flavour's default.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True for Opus.</returns>
    internal static bool IsOpus(VideoAuthoringRequest request) =>
        string.Equals(
            FFmpegVocabulary.AudioEncoderNameFor(request.Audio.Codec, request.Flavour),
            AuthoringEncoderNames.LibOpus,
            StringComparison.Ordinal);

    /// <summary>
    /// The engine authoring works through: a test's override when one is set, else the one the application
    /// registered. There is no fallback - the ffmpeg command lines run through the engine's external tools too.
    /// </summary>
    /// <returns>The engine.</returns>
    /// <exception cref="VideoAuthoringException">No engine is registered (<see cref="NoEngineRegistered" />).</exception>
    internal static IVideoProcessor ResolveEngine()
    {
        IVideoProcessor forced = EngineOverride.Value;
        if (forced != null) return forced;

        if (!NothingRegisteredOverride.Value && VideoProcessors.TryGetCurrent(out IVideoProcessor processor))
        {
            return processor;
        }

        throw new VideoAuthoringException(NoEngineRegistered);
    }

    /// <summary>
    /// True when an engine can run external tools (<see cref="VideoProcessorCapabilities.ExternalTools" />) - the
    /// ffmpeg engine - and so is the engine with an ffmpeg prepare pass and the ffmpeg command-line flavours. Every
    /// other engine has its picture shaped in process instead (<see cref="ManagedFrameShaping" />). Judged from the
    /// engine alone; whether ffmpeg and ffprobe are actually there is <see cref="AuthoringTools" />' question.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>True for an engine with external tools.</returns>
    internal static bool IsFFmpegEngine(IVideoProcessor engine) => engine.TryGetExternalTools(out _);

    /// <summary>Reports whether the in-process passes have an engine to decode with.</summary>
    /// <param name="engine">The engine <see cref="ResolveEngine" /> chose.</param>
    /// <param name="problem">Why not, or an empty string.</param>
    /// <returns>
    /// True for any engine without external tools, and for an engine with them (the ffmpeg engine) when it can
    /// find <c>ffmpeg</c> and <c>ffprobe</c>.
    /// </returns>
    internal static bool TryVerifyEngine(IVideoProcessor engine, out string problem)
    {
        if (!IsFFmpegEngine(engine))
        {
            problem = string.Empty;
            return true;
        }

        return AuthoringTools.TryVerify(engine, out _, out problem);
    }

    /// <summary>
    /// The settings that shape the picture before it is encoded - by the ffmpeg prepare pass on the ffmpeg engine,
    /// in process (<see cref="ManagedFrameShaping" />) on any other - judged from the request alone: everything
    /// except a rotated source, which only reading the source can reveal.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="hasGrade">Whether the colour-grade chain reduced to a table.</param>
    /// <returns>The setting names, empty when the source can be read as it is.</returns>
    internal static List<string> SettingsNeedingPreparePass(VideoAuthoringRequest request, bool hasGrade)
    {
        List<string> settings = new List<string>(3);
        AuthoringVideoSettings video = request.Video;

        if (!video.FrameSize.IsSourceSize) settings.Add("FrameSize");
        if (video.FrameRate > 0d) settings.Add("FrameRate/FrameRateMode");
        if (hasGrade) settings.Add("Luts");

        return settings;
    }

    /// <summary>
    /// Refuses a 10-bit master on an engine that cannot hand over 10-bit frames.
    /// </summary>
    /// <param name="engine">The engine in use.</param>
    /// <param name="request">The request.</param>
    /// <exception cref="VideoAuthoringException">The request asks for 10 bits and the engine cannot deliver them.</exception>
    internal static void RequireBitDepthSupport(IVideoProcessor engine, VideoAuthoringRequest request)
    {
        if (request.Video.BitDepth <= 8 || engine.Supports(VideoProcessorCapabilities.HighBitDepthFrameSource)) return;

        throw new VideoAuthoringException(
            "The request asks for a " + Number(request.Video.BitDepth) + "-bit master, and the '" + engine.Name
            + "' engine cannot deliver frames deeper than 8 bits (it lacks "
            + "VideoProcessorCapabilities.HighBitDepthFrameSource). Set Video.BitDepth to 8, or author on an engine "
            + "that can - the desktop ffmpeg engine can.");
    }

    /// <summary>The libaom cq-level a constant rate factor asks for: the same 0..63 number.</summary>
    /// <param name="constantRateFactor">0..63.</param>
    /// <returns>The Core quality that comes nearest; the exact cq-level rides in the codec options.</returns>
    internal static VideoQuality QualityFor(int constantRateFactor)
    {
        int level = (int)Math.Round(
            constantRateFactor * (double)VideoQuality.WorstQualityLevel / 63d,
            MidpointRounding.AwayFromZero);
        return VideoQuality.ConstantQuality(Math.Max(VideoQuality.BestQualityLevel, Math.Min(VideoQuality.WorstQualityLevel, level)));
    }

    /// <summary>The libaom speed the master flavour runs at for a request's speed preset.</summary>
    /// <param name="video">The video settings.</param>
    /// <returns>0..8.</returns>
    internal static int CpuUsedFor(AuthoringVideoSettings video) => Math.Min(MaximumCpuUsed, video.SpeedPreset);

    /// <summary>True when the colour pass fills fully transparent pixels.</summary>
    /// <param name="video">The video settings.</param>
    /// <param name="alpha">Whether the file carries alpha.</param>
    /// <returns>True when both say so.</returns>
    internal static bool FillsTransparentPixels(AuthoringVideoSettings video, bool alpha) =>
        alpha && video.FillTransparentPixels;

    /// <summary>The settings line the video pass is listed with, in a dry run and in a result.</summary>
    /// <param name="request">The request.</param>
    /// <param name="input">What the pass reads - the source, or the prepared intermediate.</param>
    /// <param name="ivfPath">The intermediate it writes.</param>
    /// <param name="fill">Whether fully transparent pixels are filled.</param>
    /// <param name="size">The size token - <see cref="DryRunSize" /> or <see cref="EncodedSize" />.</param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeVideoPass(
        VideoAuthoringRequest request,
        string input,
        string ivfPath,
        bool fill,
        string size)
    {
        AuthoringVideoSettings video = request.Video;
        string keyInterval = video.KeyframeIntervalFrames > 0
            ? video.KeyframeIntervalFrames.ToString(CultureInfo.InvariantCulture)
            : "auto";

        return "av1 i420 " + Number(video.BitDepth) + "-bit " + size + " good-quality cq-level " + Number(video.ConstantRateFactor)
            + " cpu-used " + Number(CpuUsedFor(video))
            + " keyint " + keyInterval
            + (fill ? " fill-transparent-pixels" : string.Empty)
            + " <- " + input + " -> " + ivfPath;
    }

    /// <summary>The settings line the alpha pass is listed with.</summary>
    /// <param name="request">The request.</param>
    /// <param name="input">What the pass reads, or null for a fully opaque plane made without reading anything.</param>
    /// <param name="ivfPath">The intermediate it writes.</param>
    /// <param name="size">The size token - <see cref="DryRunSize" /> or <see cref="EncodedSize" />.</param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeAlphaPass(VideoAuthoringRequest request, string input, string ivfPath, string size)
    {
        AuthoringVideoSettings video = request.Video;

        return "av1 gray " + Number(video.BitDepth) + "-bit " + size + " full-range realtime cq-level " + Number(video.ResolvedAlphaConstantRateFactor)
            + " cpu-used " + Number(CpuUsedFor(video))
            + " key-frames-of-video-pass"
            + " <- " + (input ?? "fully-opaque-plane") + " -> " + ivfPath;
    }

    /// <summary>
    /// The size a dry run lists: the exact frame size when the request names one, otherwise the decoded size
    /// cropped to even, which only reading the source can turn into numbers.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>For example <c>size 1280x720</c> or <c>size decoded-cropped-to-even</c>.</returns>
    internal static string DryRunSize(VideoAuthoringRequest request)
    {
        AuthoringFrameSize frameSize = request.Video.FrameSize;
        return frameSize.Kind == AuthoringFrameSizeKind.Exact
            ? "size " + Number(frameSize.Width) + "x" + Number(frameSize.Height)
            : "size decoded-cropped-to-even";
    }

    /// <summary>The size a real run lists: what was encoded, and what it was cropped from when it was.</summary>
    /// <param name="picture">What the video pass wrote.</param>
    /// <returns>For example <c>size 128x72</c> or <c>size 128x72 cropped-from 129x73</c>.</returns>
    internal static string EncodedSize(VideoPassResult picture)
    {
        string size = "size " + Number(picture.Width) + "x" + Number(picture.Height);
        if (picture.Width == picture.Info.Width && picture.Height == picture.Info.Height) return size;

        return size + " cropped-from " + Number(picture.Info.Width) + "x" + Number(picture.Info.Height);
    }

    /// <summary>The settings line the audio pass is listed with.</summary>
    /// <param name="request">The request.</param>
    /// <param name="input">What the pass reads.</param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeAudioPass(VideoAuthoringRequest request, string input)
    {
        AuthoringAudioSettings audio = request.Audio;

        return "flac " + Number(audio.FlacBitDepth) + "-bit " + Number(audio.SampleRateHz) + " Hz "
            + Number(audio.Channels) + " channel(s) compression-level " + Number(FlacCompressionLevel)
            + " <- " + input + " -> packets";
    }

    /// <summary>The settings line the bespoke flavour's in-process audio pass is listed with.</summary>
    /// <param name="request">The request.</param>
    /// <param name="input">What the pass reads.</param>
    /// <param name="oggPath">The Ogg file it writes.</param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeVorbisPass(VideoAuthoringRequest request, string input, string oggPath)
    {
        AuthoringAudioSettings audio = request.Audio;
        string rate = audio.VorbisQuality.HasValue
            ? "quality " + audio.VorbisQuality.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : Number(audio.BitrateKilobitsPerSecond) + " kbit/s";

        return "vorbis " + rate + " " + Number(audio.SampleRateHz) + " Hz " + Number(audio.Channels)
            + " channel(s) <- " + input + " -> " + oggPath;
    }

    /// <summary>
    /// What a master run with <c>Audio.Silent</c> lists where its audio pass would be: no pass runs, and the file is
    /// flagged silent.
    /// </summary>
    internal const string SilentAudioPass = "silent - no audio pass; the file is flagged Silent (no audio track)";

    /// <summary>The settings line the WebM-profile flavour's in-process Opus pass is listed with.</summary>
    /// <param name="request">The request.</param>
    /// <param name="input">What the pass reads.</param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeOpusPass(VideoAuthoringRequest request, string input)
    {
        AuthoringAudioSettings audio = request.Audio;

        return "opus " + Number(audio.BitrateKilobitsPerSecond) + " kbit/s " + Number(OpusSampleRate) + " Hz "
            + Number(audio.Channels) + " channel(s) vbr complexity " + Number(OpusComplexity)
            + " <- " + input + " -> packets";
    }

    /// <summary>The settings line the WebM-profile flavour's managed mux is listed with.</summary>
    /// <param name="request">The request.</param>
    /// <param name="ivfPath">The picture's intermediate.</param>
    /// <param name="audio">
    /// What the sound arrives as - <c>packets</c> for Opus, the Ogg file for Vorbis - or null for no sound.
    /// </param>
    /// <returns>A deterministic, human-readable line.</returns>
    internal static string DescribeMux(VideoAuthoringRequest request, string ivfPath, string audio)
    {
        string text = "mux " + (request.Container == AuthoringContainerFormat.Matroska ? "matroska" : "webm")
            + (request.CuesToFront ? " cues-to-front" : " cues-at-end")
            + " " + Number(request.Captions.Count) + " caption track(s)"
            + (string.IsNullOrWhiteSpace(request.ChaptersPath) ? " no chapters" : " chapters " + request.ChaptersPath)
            + " <- " + ivfPath
            + (audio == null ? string.Empty : " + " + audio)
            + " -> " + request.OutputPath;

        return text;
    }

    /// <summary>
    /// The in-process Opus encoder's settings for a request, with the meaning the libopus command line gives them:
    /// the bit rate is the TARGET bit rate of the whole stream, all channels together, coded with variable bit
    /// rate at complexity 10 for general audio - libopus's defaults, which ffmpeg leaves alone.
    /// </summary>
    /// <param name="audio">The audio settings.</param>
    /// <returns>The encoder options.</returns>
    internal static OpusEncoderOptions OpusOptionsFor(AuthoringAudioSettings audio) =>
        new OpusEncoderOptions
        {
            Bitrate = audio.BitrateKilobitsPerSecond * 1000,
            Profile = OpusEncodingProfile.Music,
            UseVariableBitrate = true,
            Complexity = OpusComplexity,
        };

    /// <summary>
    /// Builds an Opus identification header - <c>OpusHead</c>, RFC 7845 section 5.1 - for channel mapping family
    /// 0: the bytes a Matroska <c>A_OPUS</c> track carries as its codec-private data.
    /// </summary>
    /// <param name="channels">1 or 2.</param>
    /// <param name="preSkip">The encoder's pre-skip, in 48 kHz samples.</param>
    /// <param name="inputSampleRate">The rate the encoder was given, informational only.</param>
    /// <returns>The 19 header bytes.</returns>
    internal static byte[] BuildOpusHead(int channels, int preSkip, int inputSampleRate)
    {
        byte[] head = new byte[OpusHeadLength];
        System.Text.Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10, 2), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12, 4), (uint)inputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16, 2), 0);
        head[18] = 0;
        return head;
    }

    /// <summary>
    /// Encodes the sound as Opus, in process, and hands it over as the packets the WebM muxer takes: the
    /// <c>OpusHead</c> as codec-private data, every packet on the stream's own timeline (the first at zero, the
    /// pre-skip still in - the muxer states it as the track's <c>CodecDelay</c>) and the padding at the very end
    /// as the trailing trim.
    /// </summary>
    /// <param name="engine">The engine to decode with.</param>
    /// <param name="request">The request.</param>
    /// <param name="input">The file to read - always the source.</param>
    /// <param name="duration">The sound's duration if known, for progress; zero when not.</param>
    /// <param name="progress">Called with 0..100 as the pass advances, or null.</param>
    /// <param name="cancellationToken">Stops the pass between chunks.</param>
    /// <returns>The track, its header and its packets.</returns>
    /// <exception cref="VideoAuthoringException">The encoder's stream is not what the container needs.</exception>
    /// <remarks>
    /// The encoder package's packet encoder hands over bare Opus packets and the identification header, so
    /// nothing is written to disk on the way: the packets are drained after every chunk (an undrained queue holds
    /// the whole stream) and after the final pad. The header the encoder states is checked against the one built
    /// here from its own pre-skip, so a header the container would misread never reaches the file.
    /// </remarks>
    internal static MatroskaPacketAudioInput EncodeOpus(
        IVideoProcessor engine,
        VideoAuthoringRequest request,
        string input,
        TimeSpan duration,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        AuthoringAudioSettings settings = request.Audio;
        AudioSourceOptions sourceOptions = new AudioSourceOptions
        {
            SampleRate = OpusSampleRate,
            Channels = settings.Channels,
        };

        long framesRead = 0;

        using IAudioSampleSource source = engine.OpenAudio(input, sourceOptions);
        int sampleRate = source.Info.SampleRate;
        int channels = source.Info.Channels;
        if (sampleRate != OpusSampleRate || channels != settings.Channels)
        {
            throw new VideoAuthoringException(
                "The '" + engine.Name + "' engine delivered the sound of '" + input + "' at " + Number(sampleRate)
                + " Hz in " + Number(channels) + " channel(s), and the Opus pass asked for "
                + Number(OpusSampleRate) + " Hz in " + Number(settings.Channels) + ".");
        }

        using OpusPacketEncoder encoder = new OpusPacketEncoder(channels, OpusSampleRate, OpusOptionsFor(settings));
        byte[] head = BuildOpusHead(channels, encoder.PreSkip, OpusSampleRate);
        string problem = DescribeOpusHeadProblem(encoder.IdentificationHeader.Span, head);
        if (problem != null)
        {
            throw new VideoAuthoringException(
                "The Opus encoder's identification header cannot be stored as the track's codec-private data: "
                + problem + ".");
        }

        MatroskaPacketAudioInput audio = new MatroskaPacketAudioInput(
            ContainerCodecIds.Opus, head, OpusSampleRate, channels);

        long expectedFrames = duration > TimeSpan.Zero ? (long)(duration.TotalSeconds * sampleRate) : 0;
        int lastPercent = -1;
        float[] buffer = new float[AudioChunkFrames * channels];
        int read;

        while ((read = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Encode(buffer.AsSpan(0, read * channels));
            DrainOpusPackets(encoder, audio);
            framesRead += read;
            Report(progress, framesRead, expectedFrames, ref lastPercent);
        }

        if (framesRead == 0)
        {
            throw new VideoAuthoringException(
                "The audio pass decoded no sound from '" + input + "'. Set Audio.Include to false for a source "
                + "without sound.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        encoder.Finish();
        DrainOpusPackets(encoder, audio);

        if (audio.Packets.Count == 0 || audio.Packets[0].Timestamp != TimeSpan.Zero)
        {
            throw new VideoAuthoringException(
                "The Opus encoder's stream " + (audio.Packets.Count == 0
                    ? "holds no packets"
                    : "starts at " + audio.Packets[0].Timestamp + " instead of at zero")
                + ", so its timing cannot be stated in the container.");
        }

        audio.TrailingTrimSamples = encoder.TrailingTrimSamples;
        return audio;
    }

    // Moves every packet the encoder has completed onto the track, oldest first.
    private static void DrainOpusPackets(OpusPacketEncoder encoder, MatroskaPacketAudioInput audio)
    {
        while (encoder.TryReadPacket(out OpusPacket packet))
        {
            audio.Packets.Add(new MatroskaAudioPacket(packet.Data.ToArray(), packet.Timestamp, packet.Duration));
        }
    }

    /// <summary>
    /// Checks that the identification header the Opus encoder states is the one this library would build for the
    /// stream: the <c>OpusHead</c> a Matroska <c>A_OPUS</c> track carries.
    /// </summary>
    /// <param name="stated">The header the encoder states.</param>
    /// <param name="built">The header built from the encoder's channel count and pre-skip.</param>
    /// <returns>Null when they agree; otherwise what is wrong.</returns>
    internal static string DescribeOpusHeadProblem(ReadOnlySpan<byte> stated, ReadOnlySpan<byte> built)
    {
        if (!stated.SequenceEqual(built))
        {
            return "the encoder stated " + Convert.ToHexString(stated) + " and the header for its channel count and "
                + "pre-skip is " + Convert.ToHexString(built);
        }

        return null;
    }

    /// <summary>
    /// The in-process Vorbis encoder's settings for a request, with the meaning the libvorbis command line gives
    /// them: a quality number q (-1 to 10) is Vorbis quality q / 10; otherwise the bit rate is the nominal
    /// (average) bit rate.
    /// </summary>
    /// <param name="audio">The audio settings.</param>
    /// <returns>The encoder options.</returns>
    internal static VorbisEncoderOptions VorbisOptionsFor(AuthoringAudioSettings audio)
    {
        if (audio.VorbisQuality.HasValue)
        {
            float quality = (float)(audio.VorbisQuality.Value / 10d);
            return new VorbisEncoderOptions
            {
                Mode = VorbisEncoderMode.Quality,
                Quality = Math.Max(VorbisEncoderOptions.MinimumQuality, Math.Min(VorbisEncoderOptions.MaximumQuality, quality)),
            };
        }

        return new VorbisEncoderOptions
        {
            Mode = VorbisEncoderMode.Bitrate,
            NominalBitrate = audio.BitrateKilobitsPerSecond * 1000,
        };
    }

    /// <summary>Encodes the sound as an Ogg Vorbis file, in process, for the bespoke muxer.</summary>
    /// <param name="engine">The engine to decode with.</param>
    /// <param name="request">The request.</param>
    /// <param name="input">The file to read - always the source.</param>
    /// <param name="oggPath">Where the Ogg file goes.</param>
    /// <param name="duration">The sound's duration if known, for progress; zero when not.</param>
    /// <param name="progress">Called with 0..100 as the pass advances, or null.</param>
    /// <param name="cancellationToken">Stops the pass between chunks.</param>
    internal static void EncodeVorbis(
        IVideoProcessor engine,
        VideoAuthoringRequest request,
        string input,
        string oggPath,
        TimeSpan duration,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        AuthoringAudioSettings settings = request.Audio;
        AudioSourceOptions sourceOptions = new AudioSourceOptions
        {
            SampleRate = settings.SampleRateHz,
            Channels = settings.Channels,
        };

        using IAudioSampleSource source = engine.OpenAudio(input, sourceOptions);
        int sampleRate = source.Info.SampleRate;
        int channels = source.Info.Channels;
        long expectedFrames = duration > TimeSpan.Zero ? (long)(duration.TotalSeconds * sampleRate) : 0;
        int lastPercent = -1;
        long framesRead = 0;

        using FileStream file = new FileStream(oggPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using (OggVorbisEncoder encoder = new OggVorbisEncoder(file, channels, sampleRate, VorbisOptionsFor(settings)))
        {
            float[] buffer = new float[AudioChunkFrames * channels];
            int read;

            while ((read = source.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoder.Encode(buffer.AsSpan(0, read * channels));
                framesRead += read;
                Report(progress, framesRead, expectedFrames, ref lastPercent);
            }

            cancellationToken.ThrowIfCancellationRequested();
            encoder.Flush();
        }

        if (framesRead == 0)
        {
            throw new VideoAuthoringException(
                "The audio pass decoded no sound from '" + input + "'. Set Audio.Include to false for a source "
                + "without sound.");
        }
    }

    /// <summary>Encodes the picture into an AV1 IVF file and reports which frames are key frames.</summary>
    /// <param name="engine">The engine to decode with.</param>
    /// <param name="request">The request.</param>
    /// <param name="input">The file to read - the source or the prepared intermediate.</param>
    /// <param name="alpha">Whether the file carries alpha, so the alpha plane is read for the edge fill.</param>
    /// <param name="stream">The analysed video stream of <paramref name="input" />, for its colour tags.</param>
    /// <param name="ivfPath">Where the IVF goes.</param>
    /// <param name="progress">Called with 0..100 as the pass advances, or null.</param>
    /// <param name="cancellationToken">Stops the pass between frames.</param>
    /// <param name="shaping">
    /// The in-process shaping to put round the decode - an engine other than ffmpeg standing in for the prepare
    /// pass - or null to encode the frames as they are decoded.
    /// </param>
    /// <returns>What the pass wrote.</returns>
    /// <exception cref="VideoAuthoringException">The source cannot be encoded as it stands.</exception>
    /// <remarks>
    /// The depth is <c>request.Video.BitDepth</c>: 8, or 10 for a master. At 10 bits every plane is two bytes a
    /// sample (little-endian, value in the low bits), the encoder is asked for 10-bit 4:2:0, and the
    /// transparent-pixel fill works on the 10-bit planes.
    /// </remarks>
    internal static VideoPassResult EncodeVideo(
        IVideoProcessor engine,
        VideoAuthoringRequest request,
        string input,
        bool alpha,
        VideoStreamInfo stream,
        string ivfPath,
        Action<int> progress,
        CancellationToken cancellationToken,
        ManagedFrameShaping shaping = null)
    {
        AuthoringVideoSettings video = request.Video;
        int depth = video.BitDepth;
        int bytes = depth > 8 ? 2 : 1;
        RequireBitDepthSupport(engine, request);

        VideoSourceOptions sourceOptions = new VideoSourceOptions
        {
            PixelLayout = VideoPixelLayout.I420,
            BitDepth = depth,
            IncludeAlpha = alpha && engine.Supports(VideoProcessorCapabilities.AlphaFrameSource),
        };

        using IVideoFrameSource source = OpenPicture(engine, input, sourceOptions, shaping, false);
        VideoSourceInfo info = source.Info;
        RequireCroppableSize(info.Width, info.Height, input);
        RequireYuvMatrix(engine, info, input);

        // 4:2:0 AV1 needs even dimensions: an odd last row or column is cropped away - never resampled - here
        // and identically in the alpha pass, so the two planes stay the same size. The prepare pass is not asked
        // to scale to even instead: one rule, in one place, on every engine, with no resample of the whole picture.
        int width = EvenFloor(info.Width);
        int height = EvenFloor(info.Height);
        bool cropped = width != info.Width || height != info.Height;

        VideoEncoderRequest encoderRequest = new VideoEncoderRequest
        {
            Width = width,
            Height = height,
            PixelLayout = VideoPixelLayout.I420,
            BitDepth = depth,
            FrameRate = info.FrameRate,
            Quality = QualityFor(video.ConstantRateFactor),
            MaxKeyFrameInterval = Math.Max(0, video.KeyframeIntervalFrames),
            ColorRange = info.ColorRange == VideoColorRange.Full ? VideoColorRange.Full : VideoColorRange.Limited,
            ColorMatrix = info.ColorMatrix,
            ColorPrimaries = stream != null ? stream.ColorPrimaries : VideoColorPrimaries.Unspecified,
            ColorTransfer = stream != null ? stream.ColorTransfer : VideoColorTransfer.Unspecified,
        };
        encoderRequest.CodecOptions["cq-level"] = Number(video.ConstantRateFactor);

        Av1EncoderOptions options = new Av1EncoderOptions
        {
            Usage = Av1EncoderUsage.GoodQuality,
            CpuUsed = CpuUsedFor(video),
        };

        bool fill = FillsTransparentPixels(video, alpha) && info.HasAlpha;
        TransparentPixelFill filler = fill ? new TransparentPixelFill() : null;
        int chromaWidth = width / 2;
        int chromaHeight = height / 2;
        byte[] y = fill ? new byte[width * height * bytes] : null;
        byte[] u = fill ? new byte[chromaWidth * chromaHeight * bytes] : null;
        byte[] v = fill ? new byte[chromaWidth * chromaHeight * bytes] : null;

        List<bool> keyFrames = new List<bool>();
        PacketSink sink = new PacketSink(keyFrames);
        int filledFrames = 0;
        long frames = 0;
        int lastPercent = -1;
        bool widened = false;

        using (Av1Encoder encoder = StartEncoder(encoderRequest, options, VideoPassLabel))
        using (IvfWriter writer = IvfWriter.CreateAv1(
            ivfPath, width, height, (uint)info.FrameRate.Denominator, (uint)info.FrameRate.Numerator))
        {
            while (source.TryReadFrame(out VideoFrameData frame))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (frame.BitDepth != depth)
                {
                    frame = WidenTo(frame, depth, input);
                    widened = true;
                }

                if (fill && frame.HasAlpha)
                {
                    CopyRows(frame.Y, y, width * bytes, height);
                    CopyRows(frame.U, u, chromaWidth * bytes, chromaHeight);
                    CopyRows(frame.V, v, chromaWidth * bytes, chromaHeight);

                    if (filler.Apply(
                        width, height, y, width * bytes, u, v, chromaWidth * bytes, frame.A.Data.Span, frame.A.Stride, bytes))
                    {
                        filledFrames++;
                    }

                    VideoFrameData filled = VideoFrameData.CreateI420(
                        width,
                        height,
                        VideoPlane.FromArray(y, width * bytes, height),
                        VideoPlane.FromArray(u, chromaWidth * bytes, chromaHeight),
                        VideoPlane.FromArray(v, chromaWidth * bytes, chromaHeight),
                        frame.Timestamp,
                        frame.FrameIndex,
                        default,
                        depth);
                    Encode(encoder, filled, false, VideoPassLabel);
                }
                else
                {
                    Encode(encoder, cropped ? CropI420(frame, width, height) : frame, false, VideoPassLabel);
                }

                sink.Drain(encoder, writer, VideoPassLabel);
                frames++;
                Report(progress, frames, info.EstimatedFrameCount, ref lastPercent);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Flush(encoder, VideoPassLabel);
            sink.Drain(encoder, writer, VideoPassLabel);
            writer.Complete();
        }

        if (frames == 0)
        {
            throw new VideoAuthoringException(
                "The video pass decoded no frames from '" + input + "', so there is no picture to write.");
        }

        if (keyFrames.Count != frames)
        {
            throw new VideoAuthoringException(
                "The video pass encoded " + Number(frames) + " frame(s) and the AV1 encoder returned "
                + Number(keyFrames.Count) + " packet(s); a master file needs exactly one packet per frame.");
        }

        ShapedVideoFrameSource shaped = source as ShapedVideoFrameSource;
        return new VideoPassResult(info, width, height, keyFrames, fill, filledFrames)
        {
            Shaping = shaping,
            Rotation = shaped != null ? shaped.Rotation : RotationAngle.None,
            TurnedWidth = shaped != null ? shaped.TurnedWidth : info.Width,
            TurnedHeight = shaped != null ? shaped.TurnedHeight : info.Height,
            Widened = widened,
        };
    }

    /// <summary>
    /// Opens the picture of a file, with the in-process shaping round it when there is one.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="input">The file.</param>
    /// <param name="options">What to ask the engine for.</param>
    /// <param name="shaping">The shaping, or null.</param>
    /// <param name="alphaOnly">True for the alpha pass's Gray frames made from the alpha plane.</param>
    /// <returns>The frame source; the caller disposes it.</returns>
    internal static IVideoFrameSource OpenPicture(
        IVideoProcessor engine,
        string input,
        VideoSourceOptions options,
        ManagedFrameShaping shaping,
        bool alphaOnly)
    {
        IVideoFrameSource source = engine.OpenVideo(input, options);
        if (shaping == null) return source;

        try
        {
            return shaping.Wrap(source, alphaOnly);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Widens an 8-bit frame an engine handed over when 10 bits were asked for: luma and chroma by
    /// <c>value &lt;&lt; 2</c> (studio levels stay studio levels - 16 becomes 64, 235 becomes 940), the alpha plane by
    /// bit replication (<c>value &lt;&lt; 2 | value &gt;&gt; 6</c>), so 255 - fully opaque - becomes 1023 and stays fully
    /// opaque.
    /// </summary>
    /// <param name="frame">The 8-bit 4:2:0 or Gray frame.</param>
    /// <param name="depth">The depth asked for.</param>
    /// <param name="input">The file, for the message.</param>
    /// <returns>The widened frame.</returns>
    /// <exception cref="VideoAuthoringException">The frame is not 8-bit, or the depth asked for is not 10.</exception>
    internal static VideoFrameData WidenTo(VideoFrameData frame, int depth, string input)
    {
        if (frame.BitDepth != 8 || depth != 10)
        {
            throw new VideoAuthoringException(
                "The engine handed over a " + Number(frame.BitDepth) + "-bit frame of '" + input + "' where "
                + Number(depth) + "-bit frames were asked for.");
        }

        VideoPlane a = frame.HasAlpha ? WidenPlane(frame.A, true) : default;
        if (frame.Layout == VideoPixelLayout.Gray)
        {
            return VideoFrameData.CreateGray(
                frame.Width, frame.Height, WidenPlane(frame.Y, false), frame.Timestamp, frame.FrameIndex, a, 10);
        }

        return VideoFrameData.CreateI420(
            frame.Width,
            frame.Height,
            WidenPlane(frame.Y, false),
            WidenPlane(frame.U, false),
            WidenPlane(frame.V, false),
            frame.Timestamp,
            frame.FrameIndex,
            a,
            10);
    }

    private static VideoPlane WidenPlane(VideoPlane plane, bool replicate)
    {
        byte[] wide = new byte[plane.RowLength * plane.Rows * 2];
        for (int row = 0; row < plane.Rows; row++)
        {
            ReadOnlySpan<byte> narrow = plane.GetRow(row);
            int offset = row * plane.RowLength * 2;
            for (int x = 0; x < narrow.Length; x++)
            {
                int value = narrow[x] << 2;
                if (replicate) value |= narrow[x] >> 6;
                wide[offset + (2 * x)] = (byte)value;
                wide[offset + (2 * x) + 1] = (byte)(value >> 8);
            }
        }

        return VideoPlane.FromArray(wide, plane.RowLength * 2, plane.Rows);
    }

    /// <summary>
    /// Encodes the alpha plane into a monochrome, full-range AV1 IVF file whose key frames are exactly the
    /// picture's, and checks the two are in lock step.
    /// </summary>
    /// <param name="engine">The engine to decode with.</param>
    /// <param name="request">The request.</param>
    /// <param name="input">The file to read, or null for a fully opaque plane made without reading anything.</param>
    /// <param name="picture">What the video pass wrote.</param>
    /// <param name="ivfPath">Where the IVF goes.</param>
    /// <param name="progress">Called with 0..100 as the pass advances, or null.</param>
    /// <param name="cancellationToken">Stops the pass between frames.</param>
    /// <exception cref="VideoAuthoringException">The plane did not come out in lock step with the picture.</exception>
    /// <remarks>
    /// When the video pass was shaped in process, the alpha plane is decoded again and given the SAME turn,
    /// scale and frame-rate conversion - never the grade - so the two stay frame for frame. At 10 bits the plane
    /// is 10-bit Gray; fully opaque is 1023.
    /// </remarks>
    internal static void EncodeAlpha(
        IVideoProcessor engine,
        VideoAuthoringRequest request,
        string input,
        VideoPassResult picture,
        string ivfPath,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        AuthoringVideoSettings video = request.Video;
        VideoSourceInfo info = picture.Info;
        int width = picture.Width;
        int height = picture.Height;
        int depth = video.BitDepth;
        int bytes = depth > 8 ? 2 : 1;

        VideoEncoderRequest encoderRequest = new VideoEncoderRequest
        {
            Width = width,
            Height = height,
            PixelLayout = VideoPixelLayout.Gray,
            BitDepth = depth,
            FrameRate = info.FrameRate,
            Quality = QualityFor(video.ResolvedAlphaConstantRateFactor),
            ColorRange = VideoColorRange.Full,
            ColorMatrix = VideoColorMatrix.Unspecified,
            ColorPrimaries = VideoColorPrimaries.Unspecified,
            ColorTransfer = VideoColorTransfer.Unspecified,
        };
        encoderRequest.CodecOptions["cq-level"] = Number(video.ResolvedAlphaConstantRateFactor);

        // REALTIME, because there forced key frames are EXACT: the good-quality usage also makes the frame after
        // each forced key frame a key frame. The picture's own key frames already include that second one when
        // it happens, and forcing exactly those here reproduces them one for one. No key frame of the encoder's
        // own, so the forced ones are the only ones.
        Av1EncoderOptions options = new Av1EncoderOptions
        {
            Usage = Av1EncoderUsage.Realtime,
            CpuUsed = CpuUsedFor(video),
            AutomaticKeyFrames = false,
        };

        IReadOnlyList<bool> pictureKeys = picture.KeyFrames;
        List<bool> keyFrames = new List<bool>(pictureKeys.Count);
        PacketSink sink = new PacketSink(keyFrames);
        long frames = 0;
        int lastPercent = -1;

        using (Av1Encoder encoder = StartEncoder(encoderRequest, options, AlphaPassLabel))
        using (IvfWriter writer = IvfWriter.CreateAv1(
            ivfPath, width, height, (uint)info.FrameRate.Denominator, (uint)info.FrameRate.Numerator))
        {
            if (input == null)
            {
                VideoPlane plane = VideoPlane.FromArray(OpaquePlane(width, height, depth), width * bytes, height);

                for (int index = 0; index < pictureKeys.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VideoFrameData frame = VideoFrameData.CreateGray(
                        width, height, plane, info.FrameRate.TimestampOf(index), index, default, depth);
                    Encode(encoder, frame, pictureKeys[index], AlphaPassLabel);
                    sink.Drain(encoder, writer, AlphaPassLabel);
                    frames++;
                    Report(progress, frames, pictureKeys.Count, ref lastPercent);
                }
            }
            else
            {
                VideoSourceOptions sourceOptions = new VideoSourceOptions
                {
                    PixelLayout = VideoPixelLayout.I420,
                    BitDepth = depth,
                    IncludeAlpha = true,
                };

                using IVideoFrameSource source = OpenPicture(engine, input, sourceOptions, picture.Shaping, true);
                byte[] opaque = null;

                while (source.TryReadFrame(out VideoFrameData frame))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // One frame more than the picture has would break lock step whatever it holds; it is
                    // counted, not encoded, and the comparison below names the difference.
                    if (frames >= pictureKeys.Count)
                    {
                        frames++;
                        continue;
                    }

                    if (frame.BitDepth != depth) frame = WidenTo(frame, depth, input);

                    VideoPlane plane;
                    if (frame.Layout == VideoPixelLayout.Gray)
                    {
                        // The shaped source has already made the plane - Gray from the alpha plane, opaque where
                        // a frame had none.
                        plane = CropPlane(frame.Y, width * bytes, height);
                    }
                    else if (frame.HasAlpha)
                    {
                        plane = CropPlane(frame.A, width * bytes, height);
                    }
                    else
                    {
                        opaque ??= OpaquePlane(width, height, depth);
                        plane = VideoPlane.FromArray(opaque, width * bytes, height);
                    }

                    VideoFrameData gray = VideoFrameData.CreateGray(
                        width, height, plane, frame.Timestamp, frame.FrameIndex, default, depth);
                    Encode(encoder, gray, pictureKeys[(int)frames], AlphaPassLabel);
                    sink.Drain(encoder, writer, AlphaPassLabel);
                    frames++;
                    Report(progress, frames, pictureKeys.Count, ref lastPercent);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Flush(encoder, AlphaPassLabel);
            sink.Drain(encoder, writer, AlphaPassLabel);
            writer.Complete();
        }

        string mismatch = DescribeLockStepMismatch(pictureKeys, frames, keyFrames);
        if (mismatch != null)
        {
            throw new VideoAuthoringException(
                "The alpha pass did not come out in lock step with the video pass: " + mismatch + " A master "
                + "file pairs the two frame for frame, so nothing was written.");
        }
    }

    /// <summary>A fully opaque plane: every sample the depth's maximum (255, or 1023 little-endian).</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="depth">8 or 10.</param>
    /// <returns>The tightly packed plane.</returns>
    internal static byte[] OpaquePlane(int width, int height, int depth)
    {
        if (depth <= 8)
        {
            byte[] narrow = new byte[width * height];
            Array.Fill(narrow, (byte)255);
            return narrow;
        }

        int maximum = (1 << depth) - 1;
        byte[] wide = new byte[width * height * 2];
        for (int i = 0; i < wide.Length; i += 2)
        {
            wide[i] = (byte)maximum;
            wide[i + 1] = (byte)(maximum >> 8);
        }

        return wide;
    }

    /// <summary>Says how an alpha plane fails to be in lock step with its picture.</summary>
    /// <param name="pictureKeys">Whether each picture frame is a key frame.</param>
    /// <param name="alphaFrames">How many frames the alpha pass read.</param>
    /// <param name="alphaKeys">Whether each alpha frame it encoded is a key frame.</param>
    /// <returns>Null when the two agree frame for frame; otherwise one sentence saying where they do not.</returns>
    internal static string DescribeLockStepMismatch(
        IReadOnlyList<bool> pictureKeys,
        long alphaFrames,
        IReadOnlyList<bool> alphaKeys)
    {
        if (alphaFrames != pictureKeys.Count || alphaKeys.Count != pictureKeys.Count)
        {
            long written = alphaFrames != pictureKeys.Count ? alphaFrames : alphaKeys.Count;
            return "the video pass wrote " + Number(pictureKeys.Count) + " frame(s) and the alpha pass "
                + Number(written) + ".";
        }

        for (int i = 0; i < pictureKeys.Count; i++)
        {
            if (pictureKeys[i] == alphaKeys[i]) continue;

            return "frame " + Number(i) + " is "
                + (pictureKeys[i]
                    ? "a key frame in the video pass and not in the alpha pass."
                    : "a key frame in the alpha pass and not in the video pass.");
        }

        return null;
    }

    /// <summary>Encodes the sound as FLAC packets, in memory, ready for the muxer.</summary>
    /// <param name="engine">The engine to decode with.</param>
    /// <param name="request">The request.</param>
    /// <param name="input">The file to read - always the source, which the prepare pass never touches.</param>
    /// <param name="duration">The sound's duration if known, for progress; zero when not.</param>
    /// <param name="progress">Called with 0..100 as the pass advances, or null.</param>
    /// <param name="cancellationToken">Stops the pass between chunks.</param>
    /// <returns>The track, its stream header and its packets.</returns>
    /// <exception cref="VideoAuthoringException">The FLAC stream header is not what the container needs.</exception>
    internal static CbvPacketAudioInput EncodeAudio(
        IVideoProcessor engine,
        VideoAuthoringRequest request,
        string input,
        TimeSpan duration,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        AuthoringAudioSettings settings = request.Audio;
        AudioSourceOptions sourceOptions = new AudioSourceOptions
        {
            SampleRate = settings.SampleRateHz,
            Channels = settings.Channels,
        };

        using IAudioSampleSource source = engine.OpenAudio(input, sourceOptions);
        int sampleRate = source.Info.SampleRate;
        int channels = source.Info.Channels;

        FlacEncoderOptions options = new FlacEncoderOptions
        {
            BitsPerSample = settings.FlacBitDepth,
            CompressionLevel = FlacCompressionLevel,
        };

        List<CbvAudioPacket> packets = new List<CbvAudioPacket>();
        long expectedFrames = duration > TimeSpan.Zero ? (long)(duration.TotalSeconds * sampleRate) : 0;
        int lastPercent = -1;
        byte[] header;

        using (FlacPacketEncoder encoder = new FlacPacketEncoder(channels, sampleRate, options))
        {
            float[] buffer = new float[AudioChunkFrames * channels];
            long framesRead = 0;
            int read;

            while ((read = source.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoder.Encode(buffer.AsSpan(0, read * channels));
                DrainFlac(encoder, sampleRate, packets);
                framesRead += read;
                Report(progress, framesRead, expectedFrames, ref lastPercent);
            }

            cancellationToken.ThrowIfCancellationRequested();
            encoder.Finish();
            DrainFlac(encoder, sampleRate, packets);
            header = encoder.StreamHeader.ToArray();

            string problem = DescribeFlacCodecPrivateProblem(
                header, sampleRate, channels, settings.FlacBitDepth, encoder.FramesWritten);
            if (problem != null)
            {
                throw new VideoAuthoringException(
                    "The FLAC encoder's stream header cannot be stored as the track's codec-private data: " + problem
                    + ".");
            }
        }

        if (packets.Count == 0)
        {
            throw new VideoAuthoringException(
                "The audio pass decoded no sound from '" + input + "'. Set Audio.Include to false for a source "
                + "without sound.");
        }

        CbvPacketAudioInput audio = new CbvPacketAudioInput(ContainerCodecIds.Flac, header, sampleRate, channels);
        foreach (CbvAudioPacket packet in packets) audio.Packets.Add(packet);
        return audio;
    }

    /// <summary>
    /// Checks a FLAC stream header is the codec-private data Matroska's <c>A_FLAC</c> convention - and so the
    /// bespoke container - expects: <c>fLaC</c>, then metadata blocks with a 34-byte STREAMINFO first and the
    /// last-block flag on the last one, with STREAMINFO stating the stream's real shape and its final length.
    /// </summary>
    /// <param name="header">The header bytes.</param>
    /// <param name="sampleRate">The sample rate the stream was encoded at.</param>
    /// <param name="channels">The channel count it was encoded with.</param>
    /// <param name="bitsPerSample">The bit depth it was encoded at.</param>
    /// <param name="totalFrames">The number of sample frames encoded.</param>
    /// <returns>Null when the header is right; otherwise what is wrong with it.</returns>
    internal static string DescribeFlacCodecPrivateProblem(
        ReadOnlySpan<byte> header,
        int sampleRate,
        int channels,
        int bitsPerSample,
        long totalFrames)
    {
        if (header.Length < 8 + 34 || header[0] != (byte)'f' || header[1] != (byte)'L' || header[2] != (byte)'a'
            || header[3] != (byte)'C')
        {
            return "it does not begin with 'fLaC' followed by a STREAMINFO block";
        }

        int offset = 4;
        bool first = true;
        while (true)
        {
            if (offset + 4 > header.Length) return "its metadata blocks run past its end without a last-block flag";

            bool last = (header[offset] & 0x80) != 0;
            int type = header[offset] & 0x7F;
            int length = (header[offset + 1] << 16) | (header[offset + 2] << 8) | header[offset + 3];

            if (first && (type != 0 || length != 34))
            {
                return "its first metadata block is type " + Number(type) + " of " + Number(length)
                    + " bytes, not the 34-byte STREAMINFO";
            }

            offset += 4 + length;
            if (offset > header.Length) return "a metadata block runs past its end";
            first = false;

            if (last) break;
        }

        if (offset != header.Length)
        {
            return "it carries " + Number(header.Length - offset) + " byte(s) after the block flagged as the last";
        }

        ReadOnlySpan<byte> info = header.Slice(8, 34);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(info.Slice(10, 8));
        int streamRate = (int)(packed >> 44);
        int streamChannels = (int)((packed >> 41) & 0x7) + 1;
        int streamBits = (int)((packed >> 36) & 0x1F) + 1;
        long streamTotal = (long)(packed & 0xFFFFFFFFFUL);

        if (streamRate != sampleRate || streamChannels != channels || streamBits != bitsPerSample)
        {
            return "its STREAMINFO says " + Number(streamRate) + " Hz, " + Number(streamChannels) + " channel(s), "
                + Number(streamBits) + "-bit, and the stream was encoded at " + Number(sampleRate) + " Hz, "
                + Number(channels) + " channel(s), " + Number(bitsPerSample) + "-bit";
        }

        if (streamTotal != totalFrames)
        {
            return "its STREAMINFO states " + Number(streamTotal) + " sample frame(s) and " + Number(totalFrames)
                + " were encoded - the header is not the final one";
        }

        return null;
    }

    /// <summary>The largest even number not above a dimension: what an odd one is cropped to.</summary>
    /// <param name="value">The dimension.</param>
    /// <returns>The even dimension.</returns>
    internal static int EvenFloor(int value) => value & ~1;

    /// <summary>
    /// The note an in-process run adds when it cropped an odd-sized picture to even dimensions, saying exactly
    /// what was cropped; null when nothing was.
    /// </summary>
    /// <param name="width">The decoded width.</param>
    /// <param name="height">The decoded height.</param>
    /// <returns>The note, or null.</returns>
    internal static string DescribeCrop(int width, int height)
    {
        bool column = (width & 1) != 0;
        bool row = (height & 1) != 0;
        if (!column && !row) return null;

        string what = column && row
            ? "the last column and the last row were"
            : column ? "the last column was" : "the last row was";

        return "the picture is " + Number(width) + "x" + Number(height) + ", and 4:2:0 AV1 needs even dimensions, so "
            + what + " cropped away (not resampled) and it was encoded at " + Number(EvenFloor(width)) + "x"
            + Number(EvenFloor(height)) + ". Set Video.FrameSize to choose the size yourself.";
    }

    /// <summary>Refuses a picture too small to crop to even dimensions - one pixel wide or high.</summary>
    /// <param name="width">The decoded width.</param>
    /// <param name="height">The decoded height.</param>
    /// <param name="input">The file it came from, for the message.</param>
    /// <exception cref="VideoAuthoringException">A dimension is 1.</exception>
    internal static void RequireCroppableSize(int width, int height, string input)
    {
        if (width >= 2 && height >= 2) return;

        throw new VideoAuthoringException(
            "The picture read from '" + input + "' is " + Number(width) + "x" + Number(height) + ". 4:2:0 AV1 needs "
            + "even dimensions, and a picture one pixel wide or high has nothing left once its odd row or column is "
            + "cropped.");
    }

    /// <summary>
    /// A view of an I420 frame's top-left even-sized part, at the frame's own depth: no copy, the planes' strides
    /// kept, the alpha plane left behind.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="width">The even width.</param>
    /// <param name="height">The even height.</param>
    /// <returns>The cropped frame.</returns>
    internal static VideoFrameData CropI420(VideoFrameData frame, int width, int height)
    {
        int bytes = frame.BytesPerSample;
        return VideoFrameData.CreateI420(
            width,
            height,
            CropPlane(frame.Y, width * bytes, height),
            CropPlane(frame.U, width / 2 * bytes, height / 2),
            CropPlane(frame.V, width / 2 * bytes, height / 2),
            frame.Timestamp,
            frame.FrameIndex,
            default,
            frame.BitDepth);
    }

    /// <summary>A view of a plane's top-left part.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="rowLength">The BYTES per row to keep (two a sample at 10 bits).</param>
    /// <param name="rows">The rows to keep.</param>
    /// <returns>The plane itself when nothing is cut, otherwise the view.</returns>
    internal static VideoPlane CropPlane(VideoPlane plane, int rowLength, int rows)
    {
        if (plane.RowLength == rowLength && plane.Rows == rows) return plane;
        return new VideoPlane(plane.Data, plane.Stride, rowLength, rows);
    }

    private static void CopyRows(VideoPlane plane, byte[] target, int rowLength, int rows)
    {
        for (int row = 0; row < rows; row++)
        {
            plane.GetRow(row).Slice(0, rowLength).CopyTo(target.AsSpan(row * rowLength, rowLength));
        }
    }

    /// <summary>
    /// The contract check on the matrix an engine reports: <see cref="VideoSourceInfo.ColorMatrix" /> names the
    /// matrix of the samples the engine DELIVERS, and the video pass asks for 4:2:0 YUV, which the RGB (identity)
    /// matrix never describes. An RGB source is fine - an engine delivering it as YUV converts it and names the
    /// matrix it converted with (the ffmpeg engine says BT.709) - so only an engine that misdescribes its own
    /// delivery is refused here, before the wrong tag reaches the file (the AV1 encoder refuses it outright).
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="info">What its frame source said.</param>
    /// <param name="input">The file, for the message.</param>
    /// <exception cref="VideoAuthoringException">The engine reports the RGB matrix for YUV samples.</exception>
    internal static void RequireYuvMatrix(IVideoProcessor engine, VideoSourceInfo info, string input)
    {
        if (info.ColorMatrix != VideoColorMatrix.Rgb) return;

        throw new VideoAuthoringException(
            "The '" + engine.Name + "' engine delivers the picture of '" + input + "' as " + info.PixelLayout
            + " (YUV) samples but reports the RGB (identity) colour matrix for them, which no YUV sample is in: the "
            + "engine misdescribes its delivery. An engine must report the matrix of the samples it delivers - for "
            + "an RGB source converted to YUV, the matrix it converted with - so the file can be tagged correctly. "
            + "This is a fault in the '" + engine.Name + "' engine, not in the source.");
    }

    private static Av1Encoder StartEncoder(VideoEncoderRequest request, Av1EncoderOptions options, string label)
    {
        Av1Encoder encoder = null;
        try
        {
            CodeBrixVideoEncodingAv1.EnsureLoaded();
            encoder = new Av1Encoder(request, options);
            encoder.Start();
            return encoder;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            encoder?.Dispose();
            throw new VideoAuthoringException("The " + label + " could not start its AV1 encoder: " + ex.Message, ex);
        }
    }

    private static void Encode(Av1Encoder encoder, VideoFrameData frame, bool forceKeyFrame, string label)
    {
        try
        {
            encoder.Encode(frame, forceKeyFrame);
        }
        catch (VideoEncodingException ex)
        {
            throw new VideoAuthoringException("The " + label + " failed in the AV1 encoder: " + ex.Message, ex);
        }
    }

    private static void Flush(Av1Encoder encoder, string label)
    {
        try
        {
            encoder.Flush();
        }
        catch (VideoEncodingException ex)
        {
            throw new VideoAuthoringException("The " + label + " failed in the AV1 encoder: " + ex.Message, ex);
        }
    }

    private static void DrainFlac(FlacPacketEncoder encoder, int sampleRate, List<CbvAudioPacket> packets)
    {
        while (encoder.TryReadPacket(out FlacPacket packet))
        {
            packets.Add(new CbvAudioPacket(
                packet.Data.ToArray(),
                TimeSpan.FromTicks(packet.FirstSample * TimeSpan.TicksPerSecond / sampleRate),
                TimeSpan.FromTicks((long)packet.SampleCount * TimeSpan.TicksPerSecond / sampleRate)));
        }
    }

    private static void Report(Action<int> progress, long done, long total, ref int lastPercent)
    {
        if (progress == null || total <= 0) return;

        int percent = (int)Math.Min(100, done * 100 / total);
        if (percent <= lastPercent) return;

        lastPercent = percent;
        progress(percent);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the encoder's packets to the IVF in frame-index order, recording each one's key-frame flag the way
    /// the muxer will read it - from the AV1 bitstream, not from anything the encoder claims.
    /// </summary>
    private sealed class PacketSink
    {
        private readonly List<bool> keyFrames;
        private Av1SequenceHeader sequenceHeader;

        internal PacketSink(List<bool> keyFrames)
        {
            this.keyFrames = keyFrames;
        }

        internal void Drain(Av1Encoder encoder, IvfWriter writer, string label)
        {
            while (true)
            {
                EncodedVideoPacket packet;
                try
                {
                    if (!encoder.TryReadPacket(out packet)) return;
                }
                catch (VideoEncodingException ex)
                {
                    throw new VideoAuthoringException("The " + label + " failed in the AV1 encoder: " + ex.Message, ex);
                }

                ReadOnlySpan<byte> data = packet.Data.Span;
                if (sequenceHeader == null
                    && Av1Bitstream.TryReadSequenceHeader(data, out Av1SequenceHeader header, out _, out _))
                {
                    sequenceHeader = header;
                }

                if (packet.FrameIndex != keyFrames.Count)
                {
                    throw new VideoAuthoringException(
                        "The " + label + "'s AV1 encoder returned frame " + Number(packet.FrameIndex) + " where frame "
                        + Number(keyFrames.Count) + " was due; a master file needs its frames in order.");
                }

                writer.WriteFrame(data, packet.FrameIndex);
                keyFrames.Add(Av1Bitstream.IsKeyFrame(data, sequenceHeader));
            }
        }
    }
}
