using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoProcessing.Core.Tools;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

// =====================================================================================================================
// THE FFMPEG VOCABULARY. This file is the ONLY place in CodeBrix.VideoPlayback.Authoring that composes or runs an
// ffmpeg command line: the encoder names on the line, the filter graphs, the -metadata escaping, the "time=" progress
// parse, the libsvtav1 -> libaom-av1 fallback rewrite, the parse of "ffmpeg -encoders" and the names of the two
// tools. It NEVER references the ffmpeg package (CodeBrix.VideoProcessing): it composes its lines with Core's
// ExternalToolArguments and runs them through the IExternalTools of the REGISTERED engine (CodeBrix.VideoProcessing
// .Core.Tools), so Authoring depends on CodeBrix.VideoProcessing.Core and nothing else of that family. The corpus
// manifest under tests/assets/authoring and CorpusCommandEquivalenceTests are the fence: a change to a rendered line
// is a corpus re-encode.
// =====================================================================================================================

/// <summary>
/// Composes every FFmpeg command line this library runs, and runs them through the registered engine's external
/// tools.
/// </summary>
/// <remarks>
/// <para>
/// Three facts about ffmpeg's command line are load-bearing and are stated rather than rediscovered - there is
/// exactly ONE <c>-vf</c> per output, so the whole video chain is built in a single place; <c>-autorotate</c> is an
/// INPUT option, so it is rendered ahead of the matching <c>-i</c>; and every command is composed afresh.
/// </para>
/// <para>
/// Codecs and containers are named as STRINGS. A command line has to be renderable on a machine that has no FFmpeg
/// at all, so nothing here asks ffmpeg what it has until a line is about to run.
/// </para>
/// <para>
/// The quoting is Core's (<see cref="ExternalToolArguments" />): a path is wrapped in one pair of double quotes,
/// every other token goes on the line exactly as composed here - including the <c>-metadata</c> values, whose
/// inner double quotes this file escapes itself, as the builder it replaces did.
/// </para>
/// </remarks>
internal static class FFmpegVocabulary
{
    /// <summary>The name the engine locates and runs ffmpeg by.</summary>
    internal const string FFmpegToolName = "ffmpeg";

    /// <summary>The name the engine locates ffprobe by.</summary>
    internal const string FFprobeToolName = "ffprobe";

    /// <summary>The arguments that make ffmpeg list the encoders it was built with.</summary>
    internal const string ListEncodersArguments = "-hide_banner -encoders";

    /// <summary>The lossless codec of the master flavour's prepared intermediate.</summary>
    internal const string MasterIntermediateCodec = "ffv1";

    /// <summary>The pixel format of a prepared intermediate that carries alpha.</summary>
    internal const string MasterAlphaPixelFormat = "yuva420p";

    /// <summary>The pixel format of a 10-bit master's prepared intermediate that carries alpha.</summary>
    internal const string MasterAlphaPixelFormat10 = "yuva420p10le";

    /// <summary>The largest libaom <c>-cpu-used</c> the AV1 encoder fallback rewrites a speed preset to.</summary>
    internal const int AomMaximumCpuUsed = 8;

    /// <summary>What the fallback says when the build has no libaom-av1 to fall back to.</summary>
    internal const string LibAomMissingProblem =
        "this ffmpeg has no libaom-av1 encoder either, so there was nothing to fall back to";

    /// <summary>How many of ffmpeg's last error-stream lines a failed pass reports.</summary>
    internal const int ErrorTailLines = 12;

    private static readonly Regex ProgressRegex = new Regex(@"time=(\d\d:\d\d:\d\d.\d\d?)", RegexOptions.Compiled);
    private static readonly Regex DurationRegex = new Regex(@"^(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d+))?", RegexOptions.Compiled);
    private static readonly Regex UnknownEncoderRegex = new Regex(@"Unknown encoder '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex EncoderListingRegex =
        new Regex(@"^\s*[VASD\.][F\.][S\.][X\.][B\.][D\.]\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex VideoCodecTokenRegex =
        new Regex(@"^\s*-((?:c|codec):v(?::\d+)?|vcodec)\s+(\S+)\s*$", RegexOptions.Compiled);
    private static readonly Regex NumericPresetTokenRegex =
        new Regex(@"^\s*-preset(?::v(?::\d+)?)?\s+(-?\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex ConstantRateFactorTokenRegex =
        new Regex(@"^\s*-crf(?::v(?::\d+)?)?\s+\S+\s*$", RegexOptions.Compiled);
    private static readonly Regex SvtAv1ParamsTokenRegex = new Regex(@"^\s*-svtav1-params(?:\s|$)", RegexOptions.Compiled);
    private static readonly Regex VideoBitrateTokenRegex = new Regex(@"^\s*-(?:b:v(?::\d+)?|vb)(?:\s|$)", RegexOptions.Compiled);

    private static readonly char[] FilterValueSpecialCharacters = { '\\', '\'', ':', ',', ';', '[', ']', '=' };

    #region Composing

    /// <summary>Composes the one pass that writes a WebM-profile file.</summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <returns>The command line.</returns>
    internal static CommandLine BuildWebMProfile(VideoAuthoringRequest request, ResolvedLutChain lut)
    {
        CommandLine line = new CommandLine();
        line.AddInput(request.SourcePath, AutoRotateToken(request.Video.AutoRotate));

        foreach (AuthoringCaptionInput caption in request.Captions)
        {
            line.AddInput(caption.Path);
        }

        bool hasChapters = !string.IsNullOrWhiteSpace(request.ChaptersPath);
        int chapterFileIndex = request.Captions.Count + 1;
        if (hasChapters)
        {
            // The chapter file is an ordinary INPUT and -map_metadata names its index - rendered straight after
            // that input, where the builder this replaced put it. Writing the text to a temporary file would work
            // too, but a command line that a manifest records has to be the same every run and has to name a file
            // a reader can go and look at.
            line.AddInput(request.ChaptersPath);
            line.AddInputToken("-map_metadata " + Number(chapterFileIndex));
        }

        // ffmpeg switches its automatic stream selection OFF for an output the moment that output carries any -map
        // at all. Caption inputs are always mapped, so beside them the picture and the sound have to be mapped too,
        // whatever the switch says. Measured 2026-09-02: with the switch off and one caption input, the finished
        // file held the caption track and nothing else.
        bool mapPictureAndSound = request.SelectStreamsExplicitly || request.Captions.Count > 0;
        if (mapPictureAndSound)
        {
            line.Add(MapToken(0, "v", 0));
            if (request.Audio.Include) line.Add(MapToken(0, "a", 0));
        }
        else
        {
            // Left to itself, ffmpeg would also pick the source's first subtitle stream and push it through a
            // subtitle ENCODER nobody asked for (webvtt for WebM, ass for Matroska). Captions come from the
            // request's Captions inputs and from nowhere else, and there are none on this path, so subtitle
            // recording is switched off for the output.
            line.Add("-sn");
        }

        for (int i = 0; i < request.Captions.Count; i++)
        {
            line.Add(MapToken(i + 1, "s", 0));
        }

        // ffmpeg copies chapters from the FIRST input that has any. When the source carries chapters of its own,
        // that input is the source and a chapter file given as a later input would lose - so the chapter file's
        // index is named outright. With no chapter file, a source's own chapters would arrive with their titles
        // stripped by -map_metadata -1, so they are kept out - which is what the bespoke flavour does anyway.
        // Chapters come from the chapter file and from nowhere else. (The corpus under tests/assets/authoring was
        // rebuilt on 2026-09-02 when this line gained the token.)
        line.Add(hasChapters ? "-map_chapters " + Number(chapterFileIndex) : "-map_chapters -1");

        if (!hasChapters && !request.CopySourceMetadata) line.Add(WithoutMetadataToken);

        // Each of these is emitted ONLY when the request sets it, so a request without them renders exactly the
        // line it always did (the command-line corpus pins that). An explicit title wins over the chapter file's
        // file-level title= that -map_metadata brings in.
        if (!string.IsNullOrEmpty(request.Title)) line.Add(MetadataToken(string.Empty, "title", request.Title));

        AddVideo(line, request, lut);
        AddAudio(line, request);
        AddTrackMetadata(line, request);
        AddCaptions(line, request);

        // The extension never decides the container: a WebM-profile .cbv file is a WebM file inside, so the muxer
        // is always named.
        line.Add("-f " + FormatNameFor(request.Container));

        if (request.CuesToFront) line.Add("-cues_to_front 1");

        line.SetOutput(request.OutputPath);
        return line;
    }

    /// <summary>Composes the video pass of a bespoke file: an AV1 elementary stream in an IVF wrapper.</summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <param name="ivfPath">Where the intermediate video goes.</param>
    /// <returns>The command line.</returns>
    internal static CommandLine BuildBespokeVideo(VideoAuthoringRequest request, ResolvedLutChain lut, string ivfPath)
    {
        CommandLine line = new CommandLine();
        line.AddInput(request.SourcePath, AutoRotateToken(request.Video.AutoRotate));

        if (request.SelectStreamsExplicitly) line.Add(MapToken(0, "v", 0));
        if (!request.CopySourceMetadata) line.Add(WithoutMetadataToken);

        AddVideo(line, request, lut);

        line.Add("-an");
        line.Add("-f ivf");
        line.SetOutput(ivfPath);
        return line;
    }

    /// <summary>Composes the audio pass of a bespoke file: an Ogg stream the muxer reads packets out of.</summary>
    /// <param name="request">The request.</param>
    /// <param name="oggPath">Where the intermediate audio goes.</param>
    /// <returns>The command line.</returns>
    internal static CommandLine BuildBespokeAudio(VideoAuthoringRequest request, string oggPath)
    {
        CommandLine line = new CommandLine();
        line.AddInput(request.SourcePath);

        if (request.SelectStreamsExplicitly) line.Add(MapToken(0, "a", 0));
        if (!request.CopySourceMetadata) line.Add(WithoutMetadataToken);

        AddAudio(line, request);

        line.Add("-vn");
        line.Add("-f ogg");
        line.SetOutput(oggPath);
        return line;
    }

    /// <summary>
    /// Composes the optional prepare pass of an in-process run (a master file, or a bespoke or WebM-profile file
    /// encoded in process): the source's picture shaped by FFmpeg - scaled, conformed to a frame rate, graded,
    /// turned the right way up - into a LOSSLESS intermediate the in-process passes then read instead of the source.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <param name="intermediatePath">Where the intermediate goes (Matroska).</param>
    /// <param name="alpha">Whether the file carries alpha, so the intermediate keeps an alpha channel.</param>
    /// <returns>The command line.</returns>
    /// <remarks>
    /// <para>
    /// The chain is the bespoke video pass's own - <c>scale</c>, <c>fps</c>, <c>lut3d</c>, then <c>format</c>
    /// LAST - and the rotation is applied on the way in exactly as there. Only the end differs: FFV1, which is
    /// lossless, in <c>yuva420p</c> when alpha is carried (so a source with no alpha channel yields a fully
    /// opaque one) and <c>yuv420p</c> otherwise, with no sound - the sound pass reads the source itself.
    /// </para>
    /// <para>
    /// A 10-bit master (<see cref="AuthoringVideoSettings.BitDepth" /> = 10) is prepared as <c>yuv420p10le</c>, or
    /// <c>yuva420p10le</c> with alpha, so the in-process passes read 10-bit frames from it; at 8 bits - the
    /// default, and the only depth the command-line corpus uses - the line is unchanged.
    /// </para>
    /// <para>
    /// A frame rate given for <see cref="AuthoringFrameRateMode.Encoder" /> is an <c>fps</c> filter at the END of
    /// the chain (after <c>lut3d</c>, ahead of <c>format</c>), exactly as on the encoding passes - see
    /// the filter-chain notes in this file; the intermediate is constant-rate either way.
    /// </para>
    /// <para>
    /// The scale filter's flags gain <c>accurate_rnd+full_chroma_int</c> on THIS pass only: the prepare pass is
    /// what the in-process shaping is measured against, and FFmpeg's default rounding and chroma interpolation
    /// are the coarser of the two. The encoding passes' lines are left as they are.
    /// </para>
    /// </remarks>
    internal static CommandLine BuildPreparePass(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string intermediatePath,
        bool alpha)
    {
        AuthoringVideoSettings video = request.Video;

        CommandLine line = new CommandLine();
        line.AddInput(request.SourcePath, AutoRotateToken(video.AutoRotate));

        if (request.SelectStreamsExplicitly) line.Add(MapToken(0, "v", 0));
        line.Add(WithoutMetadataToken);

        line.Add("-c:v " + MasterIntermediateCodec);

        string pixelFormat = PreparedPixelFormatFor(video, alpha);
        line.Add(VideoFilterToken(video, lut, pixelFormat, PrepareScalerFlags));

        line.Add("-pix_fmt " + pixelFormat);
        line.Add("-an");
        line.Add("-sn -dn");
        line.Add("-f matroska");
        line.SetOutput(intermediatePath);
        return line;
    }

    /// <summary>The pixel format the prepare pass writes for a request.</summary>
    /// <param name="video">The video settings.</param>
    /// <param name="alpha">Whether the intermediate keeps an alpha channel.</param>
    /// <returns><c>yuv420p</c> / <c>yuva420p</c>, or their 10-bit little-endian forms at 10 bits.</returns>
    internal static string PreparedPixelFormatFor(AuthoringVideoSettings video, bool alpha)
    {
        if (video.BitDepth > 8) return alpha ? MasterAlphaPixelFormat10 : AuthoringVideoSettings.PixelFormat10;
        return alpha ? MasterAlphaPixelFormat : AuthoringVideoSettings.PixelFormat;
    }

    /// <summary>The muxer name a container choice hands to <c>-f</c>.</summary>
    /// <param name="container">The container.</param>
    /// <returns>The muxer name.</returns>
    internal static string FormatNameFor(AuthoringContainerFormat container) =>
        container == AuthoringContainerFormat.Matroska ? "matroska" : "webm";

    /// <summary>The encoder name an encoder choice hands to <c>-c:v</c>.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The encoder name.</returns>
    internal static string EncoderNameFor(AuthoringVideoEncoder encoder) =>
        encoder == AuthoringVideoEncoder.LibAomAv1
            ? AuthoringEncoderNames.LibAomAv1
            : AuthoringEncoderNames.LibSvtAv1;

    /// <summary>The encoder name an audio choice hands to <c>-c:a</c>, with the flavour default resolved.</summary>
    /// <param name="codec">The requested codec.</param>
    /// <param name="flavour">The flavour, which decides what "default" means.</param>
    /// <returns>The encoder name.</returns>
    internal static string AudioEncoderNameFor(AuthoringAudioCodec codec, VideoAuthoringFlavour flavour)
    {
        switch (codec)
        {
            case AuthoringAudioCodec.LibOpus: return AuthoringEncoderNames.LibOpus;
            case AuthoringAudioCodec.LibVorbis: return AuthoringEncoderNames.LibVorbis;
            case AuthoringAudioCodec.Flac: return AuthoringEncoderNames.Flac;
            default:
                return flavour switch
                {
                    VideoAuthoringFlavour.Bespoke => AuthoringEncoderNames.LibVorbis,
                    VideoAuthoringFlavour.Master => AuthoringEncoderNames.Flac,
                    _ => AuthoringEncoderNames.LibOpus,
                };
        }
    }

    private const string WithoutMetadataToken = "-map_metadata -1";

    private static string AutoRotateToken(bool enabled) => enabled ? "-autorotate" : "-noautorotate";

    // "-map 0:v:0" - input file, stream type, stream index of that type.
    private static string MapToken(int inputFileIndex, string streamType, int streamIndex) =>
        "-map " + Number(inputFileIndex) + ":" + streamType + ":" + Number(streamIndex);

    // -metadata[:s:<type>:<index>] key="value" - the value in double quotes with each inner double quote escaped
    // ONCE (R1: Core passes the token verbatim, so this escaping is Authoring's own and always was).
    private static string MetadataToken(string streamSpecifier, string key, string value) =>
        "-metadata" + streamSpecifier + " " + key + "=\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    private static string StreamSpecifier(string streamType, int index) => ":s:" + streamType + ":" + Number(index);

    private static void AddVideo(CommandLine line, VideoAuthoringRequest request, ResolvedLutChain lut)
    {
        AuthoringVideoSettings video = request.Video;

        line.Add("-c:v " + EncoderNameFor(video.Encoder));

        if (video.Encoder == AuthoringVideoEncoder.LibAomAv1)
        {
            // libaom's speed knob is -cpu-used, and its rate factor only takes effect with the bit rate pinned to
            // zero. SVT-AV1 needs neither.
            line.Add("-cpu-used " + Number(video.SpeedPreset));
            line.Add("-crf " + Number(video.ConstantRateFactor));
            line.Add("-b:v 0");
        }
        else
        {
            line.Add("-preset " + Number(video.SpeedPreset));
            line.Add("-crf " + Number(video.ConstantRateFactor));
        }

        line.Add("-pix_fmt " + AuthoringVideoSettings.PixelFormat);

        if (video.KeyframeIntervalFrames > 0)
        {
            line.Add("-g " + Number(video.KeyframeIntervalFrames));
        }

        // LAST in the chain is the format, so whatever the chain did - and lut3d works in RGB, so it did convert -
        // the encoder is handed the 8-bit 4:2:0 the streamable profile recommends.
        line.Add(VideoFilterToken(video, lut, AuthoringVideoSettings.PixelFormat, null));
    }

    // The scale flags the prepare pass adds to the request's own: exact rounding and full chroma interpolation,
    // which is what the managed scaler is measured against (see BuildPreparePass).
    private static readonly string[] PrepareScalerFlags = { "accurate_rnd", "full_chroma_int" };

    // ONE -vf for the whole chain: ffmpeg keeps only the last one it is given. The filters are joined with ", " and
    // the whole chain is wrapped in double quotes.
    //
    // THE FRAME RATE IS ALWAYS THE fps FILTER. FrameRateMode only decides WHERE in the chain it sits: Filter puts it
    // straight after scale, so a grade is never computed for a frame that is about to be dropped; Encoder puts it at
    // the end, after lut3d and ahead of format, where the output-side -r used to act. It used to BE -r, and -r
    // picks frames differently from fps when it LOWERS a rate (measured with FFmpeg 7.1, 10 -> 5 frames a second
    // over a 2-second clip: -r 5 wrote 12 frames, and not the ones fps=5 keeps; fps=5 wrote 10). The in-process
    // shaping follows the fps filter in both modes, so both paths now select the same frames. Dropping whole frames
    // before or after a per-frame grade gives the same pictures; the output is constant-rate either way.
    private static string VideoFilterToken(
        AuthoringVideoSettings video,
        ResolvedLutChain lut,
        string pixelFormat,
        string[] extraScalerFlags)
    {
        List<string> filters = new List<string>(5);
        bool convertsRate = video.FrameRate > 0d;
        string fps = convertsRate ? "fps=" + video.FrameRate.ToString(CultureInfo.InvariantCulture) : null;

        if (!video.FrameSize.IsSourceSize)
        {
            // Raw: the aspect-preserving forms render an ffmpeg expression whose commas are already escaped
            // ONCE for the filtergraph (RenderScaleFilter); escaping again would hand ffmpeg stray backslashes.
            filters.Add(RenderScaleFilter(video, extraScalerFlags));
        }

        if (convertsRate && video.FrameRateMode == AuthoringFrameRateMode.Filter) filters.Add(fps);

        if (lut != null && lut.HasGrade)
        {
            // The path is escaped for both of ffmpeg's unescape passes, so a colon, a space or a backslash in it
            // survives.
            filters.Add("lut3d=file=" + EscapeFilterValue(lut.FilterPath) + ":interp=tetrahedral");
        }

        if (convertsRate && video.FrameRateMode == AuthoringFrameRateMode.Encoder) filters.Add(fps);

        filters.Add("format=" + EscapeFilterValue(pixelFormat));

        return "-vf \"" + string.Join(", ", filters) + "\"";
    }

    private static void AddAudio(CommandLine line, VideoAuthoringRequest request)
    {
        AuthoringAudioSettings audio = request.Audio;

        if (!audio.Include)
        {
            line.Add("-an");
            return;
        }

        string encoder = AudioEncoderNameFor(audio.Codec, request.Flavour);
        line.Add("-c:a " + encoder.ToLowerInvariant());

        if (string.Equals(encoder, AuthoringEncoderNames.LibVorbis, StringComparison.Ordinal)
            && audio.VorbisQuality.HasValue)
        {
            line.Add("-q:a " + audio.VorbisQuality.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            line.Add("-b:a " + Number(audio.BitrateKilobitsPerSecond) + "k");
        }

        line.Add("-ar " + Number(audio.SampleRateHz));
        line.Add("-ac " + Number(audio.Channels));
    }

    // The WebM-profile pass's picture and sound track metadata - the same three settings the bespoke muxer and the
    // in-process Matroska muxer write. Each only when set; the sound's only when there is sound.
    private static void AddTrackMetadata(CommandLine line, VideoAuthoringRequest request)
    {
        if (!string.IsNullOrEmpty(request.Video.TrackName))
        {
            line.Add(MetadataToken(StreamSpecifier("v", 0), "title", request.Video.TrackName));
        }

        if (!request.Audio.Include) return;

        if (!string.IsNullOrEmpty(request.Audio.Language))
        {
            line.Add(MetadataToken(StreamSpecifier("a", 0), "language", request.Audio.Language));
        }

        if (!string.IsNullOrEmpty(request.Audio.Name))
        {
            line.Add(MetadataToken(StreamSpecifier("a", 0), "title", request.Audio.Name));
        }
    }

    private static void AddCaptions(CommandLine line, VideoAuthoringRequest request)
    {
        if (request.Captions.Count == 0) return;

        // COPY, never re-encode. FFmpeg's webvtt ENCODER discards cue identifiers and positioning settings, so a
        // track that went through it would arrive stripped of exactly the parts a player needs.
        line.Add("-c:s " + AuthoringEncoderNames.SubtitleCopy.ToLowerInvariant());

        for (int i = 0; i < request.Captions.Count; i++)
        {
            AuthoringCaptionInput caption = request.Captions[i];

            if (caption.Language.Length > 0)
            {
                line.Add(MetadataToken(StreamSpecifier("s", i), "language", caption.Language));
            }

            if (caption.Name.Length > 0)
            {
                line.Add(MetadataToken(StreamSpecifier("s", i), "title", caption.Name));
            }
        }

        for (int i = 0; i < request.Captions.Count; i++)
        {
            List<string> flags = DispositionsFor(request.Captions[i].Flags);
            if (flags.Count == 0) continue;

            line.Add("-disposition:s:" + Number(i) + " " + string.Join("+", flags));
        }
    }

    private static List<string> DispositionsFor(CaptionTrackFlags flags)
    {
        List<string> dispositions = new List<string>(3);

        if ((flags & CaptionTrackFlags.Default) != 0) dispositions.Add("default");
        if ((flags & CaptionTrackFlags.Forced) != 0) dispositions.Add("forced");
        if ((flags & CaptionTrackFlags.HearingImpaired) != 0) dispositions.Add("hearing_impaired");

        return dispositions;
    }

    private static string RenderScaleFilter(AuthoringVideoSettings video, string[] extraScalerFlags)
    {
        StringBuilder filter = new StringBuilder("scale=w=");
        filter.Append(EscapeCommas(video.FrameSize.RenderWidth()));
        filter.Append(":h=");
        filter.Append(EscapeCommas(video.FrameSize.RenderHeight()));
        filter.Append(":flags=");
        filter.Append(EscapeCommas(WithScalerFlags(video.ScalerFlags, extraScalerFlags)));
        return filter.ToString();
    }

    // The request's scaler flags with each extra flag appended ("+name") unless the request already names it.
    private static string WithScalerFlags(string flags, string[] extraScalerFlags)
    {
        if (extraScalerFlags == null || extraScalerFlags.Length == 0) return flags;

        HashSet<string> present = new HashSet<string>(
            flags.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        StringBuilder combined = new StringBuilder(flags);
        foreach (string extra in extraScalerFlags)
        {
            if (present.Add(extra)) combined.Append('+').Append(extra);
        }

        return combined.ToString();
    }

    // A comma would end the filter, so an expression that contains one escapes it ONCE - which is what the
    // filtergraph parser undoes on its way to the scale filter's own option parser.
    private static string EscapeCommas(string value) => value.Replace(",", "\\,");

    // A filter option VALUE escaped for BOTH of ffmpeg's unescape passes (the filtergraph's, then the filter's own
    // option parser's): every special character gets a backslash, twice over.
    internal static string EscapeFilterValue(string value) =>
        EscapeOnce(EscapeOnce(value ?? string.Empty, FilterValueSpecialCharacters), FilterValueSpecialCharacters);

    private static string EscapeOnce(string value, char[] specialCharacters)
    {
        if (value.Length == 0 || value.IndexOfAny(specialCharacters) < 0) return value;

        StringBuilder builder = new StringBuilder(value.Length + 8);
        foreach (char character in value)
        {
            if (Array.IndexOf(specialCharacters, character) >= 0) builder.Append('\\');
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Running

    /// <summary>
    /// Runs one composed pass through the engine's tools: the AV1 encoder fallback first when the request allows
    /// it, progress parsed from ffmpeg's error stream, a failure described, and the line that ran recorded.
    /// </summary>
    /// <param name="tools">The registered engine's external tools.</param>
    /// <param name="line">The composed line; the fallback may rewrite it.</param>
    /// <param name="request">The request (progress callback, duration, cancellation, fallback switch).</param>
    /// <param name="label">The pass - "one pass", "video pass", "audio pass" or "prepare pass".</param>
    /// <param name="passNumber">Which pass this is, from 1.</param>
    /// <param name="passCount">How many passes the run has.</param>
    /// <param name="commands">Where the line that ran is recorded.</param>
    /// <param name="notes">Where the fallback's notes go, each prefixed with the label.</param>
    /// <exception cref="VideoAuthoringException">ffmpeg exited with a non-zero code, or could not be run.</exception>
    /// <exception cref="OperationCanceledException">The request's token was cancelled; ffmpeg was killed.</exception>
    internal static void Run(
        IExternalTools tools,
        CommandLine line,
        VideoAuthoringRequest request,
        string label,
        int passNumber,
        int passCount,
        List<AuthoringCommand> commands,
        List<string> notes)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        List<string> fallbackNotes = new List<string>();
        string fallbackProblem = null;
        ErrorStream errors = new ErrorStream(request, label, passNumber, passCount);
        string arguments = line.Render();

        ExternalToolResult result;
        try
        {
            // The request can turn the AV1 encoder fallback ON for its own passes - and only the request: the
            // ffmpeg package's process-wide switch is that package's, and Authoring does not reach it.
            if (request.AllowAv1EncoderFallback && line.RequestsSvtAv1())
            {
                HashSet<string> encoders = ListEncoders(tools, cancellationToken);
                fallbackProblem = ApplyAv1EncoderFallback(line, encoders.Contains, fallbackNotes);
                arguments = line.Render();
            }

            ExternalToolCommand command = new ExternalToolCommand(FFmpegToolName, line.ToArguments())
            {
                OnStandardErrorLine = errors.Receive,

                // Zero KILLS the child outright instead of asking it to quit tidily first. That is deliberate:
                // FFmpeg's graceful "q" quit hangs on the machine this library is developed on, and the
                // half-written file is being thrown away anyway, so there is nothing a tidy finish protects.
                KillGracePeriod = TimeSpan.Zero,
            };

            result = tools.Run(command, cancellationToken);
        }
        catch (Exception ex)
        {
            // A cancelled pass can come back looking like any other failure. The token is the authority on which
            // it was.
            cancellationToken.ThrowIfCancellationRequested();

            throw new VideoAuthoringException(
                DescribePassFailure(label, arguments, request.AllowAv1EncoderFallback, null, fallbackProblem)
                + " " + ex.Message,
                ex);
        }

        // A killed process can also report an exit code; a pass that was cancelled is a cancelled pass.
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Succeeded)
        {
            throw new VideoAuthoringException(
                DescribePassFailure(
                    label, arguments, request.AllowAv1EncoderFallback, errors.UnknownEncoder, fallbackProblem)
                + DescribeExit(result.ExitCode, errors.Tail()));
        }

        errors.Finish();

        // Recorded AFTER the pass. When the AV1 encoder fallback rewrote the line before FFmpeg started, this is
        // the line that really ran - not the one the request rendered.
        commands.Add(new AuthoringCommand(label, arguments));

        foreach (string note in fallbackNotes)
        {
            notes.Add(label + ": " + note);
        }
    }

    /// <summary>
    /// The message a failed FFmpeg pass is reported with: which pass failed, an explanation when FFmpeg did not have
    /// an encoder the pass asked for, and the command line.
    /// </summary>
    /// <param name="label">The pass - "one pass", "video pass" or "audio pass".</param>
    /// <param name="arguments">The arguments the pass ran with.</param>
    /// <param name="fallbackOn">Whether the request turned the AV1 encoder fallback on.</param>
    /// <param name="missingEncoder">The encoder ffmpeg said it does not know, or null.</param>
    /// <param name="fallbackProblem">Why the fallback could not help, when it was on and could not; else null.</param>
    /// <returns>
    /// "The video pass failed. The command was: ffmpeg ..." - with, between the two sentences, the explanation of a
    /// missing encoder when <paramref name="missingEncoder" /> is set.
    /// </returns>
    internal static string DescribePassFailure(
        string label,
        string arguments,
        bool fallbackOn,
        string missingEncoder,
        string fallbackProblem)
    {
        string explanation = ExplainMissingEncoder(missingEncoder, fallbackOn, fallbackProblem);

        return "The " + label + " failed. "
            + (explanation.Length == 0 ? string.Empty : explanation + " ")
            + "The command was: " + FFmpegToolName + " " + arguments;
    }

    /// <summary>What follows the failure sentence: ffmpeg's exit code and the end of its error stream.</summary>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="tail">The last lines ffmpeg wrote to its error stream.</param>
    /// <returns>The text, starting with a newline.</returns>
    internal static string DescribeExit(int exitCode, IReadOnlyList<string> tail)
    {
        string text = "\n" + FFmpegToolName + " exited with code " + exitCode.ToString(CultureInfo.InvariantCulture);
        if (tail == null || tail.Count == 0) return text + " and wrote nothing to its error stream.";

        return text + ". The end of what it wrote to its error stream:\n" + string.Join("\n", tail);
    }

    private static string ExplainMissingEncoder(string missingEncoder, bool fallbackOn, string fallbackProblem)
    {
        if (string.IsNullOrEmpty(missingEncoder)) return string.Empty;

        if (!string.Equals(missingEncoder, AuthoringEncoderNames.LibSvtAv1, StringComparison.Ordinal))
        {
            return "The ffmpeg being used has no '" + missingEncoder + "' encoder. Install an ffmpeg build "
                + "that includes it, or ask the request for an encoder this build has.";
        }

        if (!fallbackOn) return AuthoringTools.MissingSvtAv1 + " " + AuthoringTools.SvtAv1Remedies;

        // The fallback was on and still could not help - in practice because this build has no libaom-av1 either.
        return string.IsNullOrEmpty(fallbackProblem)
            ? AuthoringTools.MissingSvtAv1 + " " + AuthoringTools.FallbackCouldNotHelp
            : AuthoringTools.MissingSvtAv1 + " AllowAv1EncoderFallback is on, but the fallback to libaom-av1 could "
                + "not be used: " + fallbackProblem + ". Install an ffmpeg build that includes libsvtav1 or libaom-av1.";
    }

    /// <summary>
    /// Reads the time out of one line of ffmpeg's progress output (<c>time=HH:MM:SS.ss</c>).
    /// </summary>
    /// <param name="line">The error-stream line.</param>
    /// <param name="time">The time reached, or zero.</param>
    /// <returns>True when the line carried a time.</returns>
    internal static bool TryParseProgressTime(string line, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        Match match = ProgressRegex.Match(line ?? string.Empty);
        if (!match.Success) return false;

        Match parts = DurationRegex.Match(match.Groups[1].Value);
        if (!parts.Success) return true;

        long hours = long.Parse(parts.Groups[1].Value, CultureInfo.InvariantCulture);
        long minutes = long.Parse(parts.Groups[2].Value, CultureInfo.InvariantCulture);
        long seconds = long.Parse(parts.Groups[3].Value, CultureInfo.InvariantCulture);
        long ticks = (hours * TimeSpan.TicksPerHour) + (minutes * TimeSpan.TicksPerMinute) + (seconds * TimeSpan.TicksPerSecond);
        time = TimeSpan.FromTicks(ticks + FractionTicks(parts.Groups[4].Value));
        return true;
    }

    /// <summary>The percentage a time reached is of the whole, rounded to two places as ffmpeg's wrapper did.</summary>
    /// <param name="reached">The time reached.</param>
    /// <param name="total">The whole duration.</param>
    /// <returns>The percentage.</returns>
    internal static double PercentOf(TimeSpan reached, TimeSpan total) =>
        Math.Round(reached.TotalSeconds / total.TotalSeconds * 100, 2);

    private static long FractionTicks(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return 0;

        // Seven digits are ticks; pad shorter fractions ("83" is 830 ms) and round longer ones.
        if (digits.Length <= 7) return long.Parse(digits.PadRight(7, '0'), CultureInfo.InvariantCulture);

        long ticks = long.Parse(digits.Substring(0, 7), CultureInfo.InvariantCulture);
        return digits[7] >= '5' ? ticks + 1 : ticks;
    }

    /// <summary>The encoder an "Unknown encoder 'x'" line names, or null.</summary>
    /// <param name="line">One line of ffmpeg's error stream.</param>
    /// <returns>The encoder name, or null.</returns>
    internal static string FindUnknownEncoder(string line)
    {
        Match match = UnknownEncoderRegex.Match(line ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Asks ffmpeg which encoders it was built with.</summary>
    /// <param name="tools">The engine's tools.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The encoder names; empty when the listing failed.</returns>
    internal static HashSet<string> ListEncoders(IExternalTools tools, CancellationToken cancellationToken)
    {
        ExternalToolArguments arguments = new ExternalToolArguments().Add(ListEncodersArguments);
        ExternalToolResult result = tools.Run(new ExternalToolCommand(FFmpegToolName, arguments), cancellationToken);
        return ParseEncoderListing(result.StandardOutput);
    }

    /// <summary>Parses the encoder names out of <c>ffmpeg -encoders</c>.</summary>
    /// <param name="listing">The listing's standard output.</param>
    /// <returns>The names (<c>libsvtav1</c>, <c>libopus</c>, ...).</returns>
    internal static HashSet<string> ParseEncoderListing(string listing)
    {
        HashSet<string> encoders = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(listing)) return encoders;

        foreach (string line in listing.Split('\n'))
        {
            Match match = EncoderListingRegex.Match(line.TrimEnd('\r'));
            if (match.Success) encoders.Add(match.Groups[1].Value);
        }

        return encoders;
    }

    /// <summary>
    /// The libsvtav1 -> libaom-av1 rewrite: when the build lacks libsvtav1 and has libaom-av1, the output's
    /// <c>-c:v libsvtav1</c> becomes <c>-c:v libaom-av1</c>, a numeric <c>-preset</c> becomes <c>-cpu-used</c>
    /// (capped at 8), SVT-AV1's own parameters are dropped and <c>-b:v 0</c> follows <c>-crf</c>.
    /// </summary>
    /// <param name="line">The line, rewritten in place when the fallback applies.</param>
    /// <param name="hasEncoder">Answers whether the build has an encoder.</param>
    /// <param name="notes">What was changed, one sentence each.</param>
    /// <returns>Why the fallback could not help, or null when it was not needed or was applied.</returns>
    internal static string ApplyAv1EncoderFallback(CommandLine line, Func<string, bool> hasEncoder, List<string> notes)
    {
        if (!line.RequestsSvtAv1() || hasEncoder(AuthoringEncoderNames.LibSvtAv1)) return null;
        if (!hasEncoder(AuthoringEncoderNames.LibAomAv1)) return LibAomMissingProblem;

        notes.Add(
            "libsvtav1 (SVT-AV1) is not available in this ffmpeg, so the request's AllowAv1EncoderFallback encoded AV1 "
            + "with libaom-av1 instead.");

        List<string> options = line.OutputOptions;
        bool hasVideoBitrate = options.Exists(token => VideoBitrateTokenRegex.IsMatch(token));
        List<string> rewritten = new List<string>(options.Count + 1);

        foreach (string token in options)
        {
            Match codec = VideoCodecTokenRegex.Match(token);
            if (codec.Success && codec.Groups[2].Value == AuthoringEncoderNames.LibSvtAv1)
            {
                rewritten.Add("-" + codec.Groups[1].Value + " " + AuthoringEncoderNames.LibAomAv1);
                continue;
            }

            Match preset = NumericPresetTokenRegex.Match(token);
            if (preset.Success
                && int.TryParse(preset.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                int cpuUsed = Math.Clamp(value, 0, AomMaximumCpuUsed);
                rewritten.Add("-cpu-used " + Number(cpuUsed));
                notes.Add(cpuUsed == value
                    ? "-preset " + Number(value) + " became -cpu-used " + Number(cpuUsed) + "."
                    : "-preset " + Number(value) + " became -cpu-used " + Number(cpuUsed)
                        + ", the nearest value libaom-av1 accepts.");
                continue;
            }

            if (SvtAv1ParamsTokenRegex.IsMatch(token))
            {
                notes.Add("\"" + token + "\" was dropped: those settings belong to SVT-AV1 and libaom-av1 does not accept them.");
                continue;
            }

            rewritten.Add(token);

            // libaom-av1 only treats -crf as a constant quality when the bit rate is pinned to zero.
            if (ConstantRateFactorTokenRegex.IsMatch(token) && !hasVideoBitrate)
            {
                rewritten.Add("-b:v 0");
                notes.Add("-b:v 0 was added after -crf, which libaom-av1 needs for the rate factor to set the quality.");
            }
        }

        options.Clear();
        options.AddRange(rewritten);
        return null;
    }

    /// <summary>Finds ffmpeg and ffprobe through the engine's tools.</summary>
    /// <param name="tools">The engine's tools.</param>
    /// <param name="ffmpeg">Where ffmpeg is, or where it was looked for.</param>
    /// <param name="ffprobe">Where ffprobe is, or where it was looked for.</param>
    /// <returns>True when both were found.</returns>
    internal static bool LocateTools(IExternalTools tools, out ExternalTool ffmpeg, out ExternalTool ffprobe)
    {
        ffmpeg = tools.Locate(FFmpegToolName);
        ffprobe = tools.Locate(FFprobeToolName);
        return ffmpeg != null && ffmpeg.Found && ffprobe != null && ffprobe.Found;
    }

    #endregion

    /// <summary>One composed ffmpeg command line: its inputs, then one output's options, then the output.</summary>
    internal sealed class CommandLine
    {
        private readonly List<KeyValuePair<string, bool>> head = new List<KeyValuePair<string, bool>>();
        private string outputPath;

        /// <summary>The output's options, in order - one token per option, as the line renders them.</summary>
        internal List<string> OutputOptions { get; } = new List<string>();

        /// <summary>True when the output asks for libsvtav1, the encoder the fallback can stand in for.</summary>
        /// <returns>True for <c>-c:v libsvtav1</c>.</returns>
        internal bool RequestsSvtAv1()
        {
            foreach (string token in OutputOptions)
            {
                Match codec = VideoCodecTokenRegex.Match(token);
                if (codec.Success && codec.Groups[2].Value == AuthoringEncoderNames.LibSvtAv1) return true;
            }

            return false;
        }

        /// <summary>Adds an input: its options, then <c>-i "path"</c>.</summary>
        /// <param name="path">The input file.</param>
        /// <param name="option">An input option rendered ahead of it, or null.</param>
        internal void AddInput(string path, string option = null)
        {
            if (!string.IsNullOrEmpty(option)) head.Add(new KeyValuePair<string, bool>(option, false));
            head.Add(new KeyValuePair<string, bool>("-i", false));
            head.Add(new KeyValuePair<string, bool>(RequireRenderablePath(path), true));
        }

        /// <summary>Adds a token among the inputs (after the last one added).</summary>
        /// <param name="token">The token.</param>
        internal void AddInputToken(string token) => head.Add(new KeyValuePair<string, bool>(token, false));

        /// <summary>Adds an option of the output.</summary>
        /// <param name="token">The option, as it renders.</param>
        internal void Add(string token) => OutputOptions.Add(token);

        /// <summary>Sets the output file, which is overwritten (<c>-y</c>).</summary>
        /// <param name="path">The output file.</param>
        internal void SetOutput(string path) => outputPath = RequireRenderablePath(path);

        /// <summary>The line as Core's argument list.</summary>
        /// <returns>The arguments.</returns>
        internal ExternalToolArguments ToArguments()
        {
            ExternalToolArguments arguments = new ExternalToolArguments();

            foreach (KeyValuePair<string, bool> token in head)
            {
                if (token.Value) arguments.AddPath(token.Key);
                else arguments.Add(token.Key);
            }

            arguments.AddRange(OutputOptions);
            arguments.AddPath(outputPath);
            arguments.Add("-y");
            return arguments;
        }

        /// <summary>The line as it runs and as it is recorded - without the tool's name.</summary>
        /// <returns>The rendered arguments.</returns>
        internal string Render() => ToArguments().Render();

        // Core refuses a path it cannot put on a command line correctly (one holding a double quote, or ending in a
        // backslash); the request hears about it as an authoring failure, naming the path.
        private static string RequireRenderablePath(string path)
        {
            try
            {
                new ExternalToolArguments().AddPath(path);
            }
            catch (ArgumentException ex)
            {
                throw new VideoAuthoringException(
                    "The path '" + path + "' cannot be put on an ffmpeg command line: " + ex.Message, ex);
            }

            return path;
        }
    }

    // ffmpeg's error stream, line by line as it arrives: the progress in it, the encoder it said it does not know,
    // and the last few lines for a failure message.
    private sealed class ErrorStream
    {
        private readonly object gate = new object();
        private readonly Queue<string> tail = new Queue<string>(ErrorTailLines);
        private readonly VideoAuthoringRequest request;
        private readonly string label;
        private readonly int passNumber;
        private readonly int passCount;
        private readonly bool reportsProgress;
        private int lastReported = -1;

        public ErrorStream(VideoAuthoringRequest request, string label, int passNumber, int passCount)
        {
            this.request = request;
            this.label = label;
            this.passNumber = passNumber;
            this.passCount = passCount;
            reportsProgress = request.ProgressCallback != null && request.SourceDuration > TimeSpan.Zero;
        }

        public string UnknownEncoder { get; private set; }

        public void Receive(string line)
        {
            lock (gate)
            {
                if (tail.Count == ErrorTailLines) tail.Dequeue();
                tail.Enqueue(line ?? string.Empty);

                if (UnknownEncoder == null) UnknownEncoder = FindUnknownEncoder(line);

                if (reportsProgress && TryParseProgressTime(line, out TimeSpan reached))
                {
                    Report(PercentOf(reached, request.SourceDuration));
                }
            }
        }

        // A pass that finished is at 100 percent, whatever the last progress line said.
        public void Finish()
        {
            if (!reportsProgress) return;

            lock (gate) Report(100.0);
        }

        public IReadOnlyList<string> Tail()
        {
            lock (gate) return tail.ToArray();
        }

        // Whole percents only, and never backwards.
        private void Report(double percent)
        {
            int whole = (int)percent;
            if (whole <= lastReported) return;
            lastReported = whole;
            request.ProgressCallback(new AuthoringProgress(label, passNumber, passCount, whole));
        }
    }
}
