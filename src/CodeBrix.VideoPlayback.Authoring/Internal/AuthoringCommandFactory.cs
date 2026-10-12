using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Enums;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Builds every FFmpeg command line this library runs, through the CodeBrix.VideoProcessing argument model.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here shells out: the wrapper owns the process, the argument rendering and the invariant-culture
/// formatting of every number on the line. Three facts about it are load-bearing and are stated rather than
/// rediscovered - there is exactly ONE <c>-vf</c> per output, so the whole video chain is built in a single
/// call; <c>-autorotate</c> is an INPUT option, so it goes in the input callback where the builder renders
/// it ahead of the matching <c>-i</c>; and an argument chain is single-use, so every command gets a fresh
/// one.
/// </para>
/// <para>
/// Codecs and containers are named as STRINGS rather than through the wrapper's catalogue properties. Those
/// properties launch FFmpeg to read its capability listing, and a command line has to be renderable on a
/// machine that has no FFmpeg at all.
/// </para>
/// </remarks>
internal static class AuthoringCommandFactory
{
    /// <summary>Builds the one pass that writes a WebM-profile file.</summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <returns>The processor, whose <c>Arguments</c> is the rendered line.</returns>
    internal static FFMpegArgumentProcessor BuildWebMProfile(VideoAuthoringRequest request, ResolvedLutChain lut)
    {
        FFMpegArguments arguments = FFMpegArguments.FromFileInput(
            request.SourcePath,
            false,
            input => input.AutoRotate(request.Video.AutoRotate));

        foreach (AuthoringCaptionInput caption in request.Captions)
        {
            arguments = arguments.AddFileInput(caption.Path, false);
        }

        bool hasChapters = !string.IsNullOrWhiteSpace(request.ChaptersPath);
        int chapterFileIndex = request.Captions.Count + 1;
        if (hasChapters)
        {
            // The chapter file is an ordinary INPUT and -map_metadata names its index. Passing the text to
            // the wrapper's AddMetaData would work too, but it writes a temporary file with a random name
            // into the command line, and a command line that a manifest records has to be the same every
            // run and has to name a file a reader can go and look at.
            arguments = arguments.AddFileInput(request.ChaptersPath, false);
            arguments = arguments.MapMetaData(chapterFileIndex);
        }

        return arguments.OutputToFile(request.OutputPath, true, output =>
        {
            // ffmpeg switches its automatic stream selection OFF for an output the moment that output carries
            // any -map at all. Caption inputs are always mapped, so beside them the picture and the sound
            // have to be mapped too, whatever the switch says. Measured 2026-09-02: with the switch off and
            // one caption input, the finished file held the caption track and nothing else.
            bool mapPictureAndSound = request.SelectStreamsExplicitly || request.Captions.Count > 0;
            if (mapPictureAndSound)
            {
                output.SelectStream(0, 0, Channel.Video);
                if (request.Audio.Include) output.SelectStream(0, 0, Channel.Audio);
            }
            else
            {
                // Left to itself, ffmpeg would also pick the source's first subtitle stream and push it
                // through a subtitle ENCODER nobody asked for (webvtt for WebM, ass for Matroska). Captions
                // come from the request's Captions inputs and from nowhere else, and there are none on this
                // path, so subtitle recording is switched off for the output.
                output.WithCustomArgument("-sn");
            }

            for (int i = 0; i < request.Captions.Count; i++)
            {
                output.SelectStream(0, i + 1, Channel.Subtitle);
            }

            // ffmpeg copies chapters from the FIRST input that has any. When the source carries chapters of
            // its own, that input is the source and a chapter file given as a later input would lose - so
            // the chapter file's index is named outright. With no chapter file, a source's own chapters would
            // arrive with their titles stripped by -map_metadata -1, so they are kept out - which is what the
            // bespoke flavour does anyway. Chapters come from the chapter file and from nowhere else. (The
            // corpus under tests/assets/authoring was rebuilt on 2026-09-02 when this line gained the token.)
            output.WithCustomArgument(
                hasChapters
                    ? "-map_chapters " + chapterFileIndex.ToString(CultureInfo.InvariantCulture)
                    : "-map_chapters -1");

            if (!hasChapters && !request.CopySourceMetadata) output.WithoutMetadata();

            // Each of these is emitted ONLY when the request sets it, so a request without them renders exactly
            // the line it always did (the command-line corpus pins that). An explicit title wins over the chapter
            // file's file-level title= that -map_metadata brings in.
            if (!string.IsNullOrEmpty(request.Title)) output.WithMetadata("title", request.Title);

            ApplyVideo(output, request, lut);
            ApplyAudio(output, request);
            ApplyTrackMetadata(output, request);
            ApplyCaptions(output, request);

            // The extension never decides the container: a WebM-profile .cbv file is a WebM file inside, so
            // the muxer is always named.
            output.ForceFormat(FormatNameFor(request.Container));

            if (request.CuesToFront) output.CuesToFront(true);
        });
    }

    /// <summary>Builds the video pass of a bespoke file: an AV1 elementary stream in an IVF wrapper.</summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <param name="ivfPath">Where the intermediate video goes.</param>
    /// <returns>The processor.</returns>
    internal static FFMpegArgumentProcessor BuildBespokeVideo(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string ivfPath)
    {
        return FFMpegArguments
            .FromFileInput(request.SourcePath, false, input => input.AutoRotate(request.Video.AutoRotate))
            .OutputToFile(ivfPath, true, output =>
            {
                if (request.SelectStreamsExplicitly) output.SelectStream(0, 0, Channel.Video);
                if (!request.CopySourceMetadata) output.WithoutMetadata();

                ApplyVideo(output, request, lut);

                output.DisableChannel(Channel.Audio);
                output.ForceFormat("ivf");
            });
    }

    /// <summary>Builds the audio pass of a bespoke file: an Ogg stream the muxer reads packets out of.</summary>
    /// <param name="request">The request.</param>
    /// <param name="oggPath">Where the intermediate audio goes.</param>
    /// <returns>The processor.</returns>
    internal static FFMpegArgumentProcessor BuildBespokeAudio(VideoAuthoringRequest request, string oggPath)
    {
        return FFMpegArguments
            .FromFileInput(request.SourcePath, false)
            .OutputToFile(oggPath, true, output =>
            {
                if (request.SelectStreamsExplicitly) output.SelectStream(0, 0, Channel.Audio);
                if (!request.CopySourceMetadata) output.WithoutMetadata();

                ApplyAudio(output, request);

                output.DisableChannel(Channel.Video);
                output.ForceFormat("ogg");
            });
    }

    /// <summary>
    /// Builds the optional prepare pass of an in-process run (a master file, or a bespoke file encoded in
    /// process): the source's picture shaped by FFmpeg - scaled,
    /// conformed to a frame rate, graded, turned the right way up - into a LOSSLESS intermediate the in-process
    /// passes then read instead of the source.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What the colour-grade chain reduced to.</param>
    /// <param name="intermediatePath">Where the intermediate goes (Matroska).</param>
    /// <param name="alpha">Whether the file carries alpha, so the intermediate keeps an alpha channel.</param>
    /// <returns>The processor.</returns>
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
    /// A frame rate given for <see cref="AuthoringFrameRateMode.Encoder" /> is applied on this pass's output,
    /// the place FFmpeg's encoder-side rate conversion has always lived; the intermediate is constant-rate either
    /// way.
    /// </para>
    /// </remarks>
    internal static FFMpegArgumentProcessor BuildPreparePass(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string intermediatePath,
        bool alpha)
    {
        AuthoringVideoSettings video = request.Video;

        return FFMpegArguments
            .FromFileInput(request.SourcePath, false, input => input.AutoRotate(video.AutoRotate))
            .OutputToFile(intermediatePath, true, output =>
            {
                if (request.SelectStreamsExplicitly) output.SelectStream(0, 0, Channel.Video);
                output.WithoutMetadata();

                output.WithVideoCodec(MasterIntermediateCodec);

                if (video.FrameRate > 0d && video.FrameRateMode == AuthoringFrameRateMode.Encoder)
                {
                    output.WithFramerate(video.FrameRate);
                }

                string pixelFormat = PreparedPixelFormatFor(video, alpha);

                output.WithVideoFilters(filters =>
                {
                    if (!video.FrameSize.IsSourceSize) filters.Custom(RenderScaleFilter(video));

                    if (video.FrameRate > 0d && video.FrameRateMode == AuthoringFrameRateMode.Filter)
                    {
                        filters.Fps(video.FrameRate);
                    }

                    if (lut != null && lut.HasGrade) filters.Lut3D(lut.FilterPath);

                    filters.Format(pixelFormat);
                });

                output.ForcePixelFormat(pixelFormat);
                output.DisableChannel(Channel.Audio);
                output.WithCustomArgument("-sn -dn");
                output.ForceFormat("matroska");
            });
    }

    /// <summary>The lossless codec of the master flavour's prepared intermediate.</summary>
    internal const string MasterIntermediateCodec = "ffv1";

    /// <summary>The pixel format of a prepared intermediate that carries alpha.</summary>
    internal const string MasterAlphaPixelFormat = "yuva420p";

    /// <summary>The pixel format of a 10-bit master's prepared intermediate that carries alpha.</summary>
    internal const string MasterAlphaPixelFormat10 = "yuva420p10le";

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

    private static void ApplyVideo(FFMpegArgumentOptions output, VideoAuthoringRequest request, ResolvedLutChain lut)
    {
        AuthoringVideoSettings video = request.Video;

        output.WithVideoCodec(EncoderNameFor(video.Encoder));

        if (video.Encoder == AuthoringVideoEncoder.LibAomAv1)
        {
            // libaom's speed knob is -cpu-used, and its rate factor only takes effect with the bit rate
            // pinned to zero. SVT-AV1 needs neither.
            output.WithCustomArgument("-cpu-used " + video.SpeedPreset.ToString(CultureInfo.InvariantCulture));
            output.WithConstantRateFactor(video.ConstantRateFactor);
            output.WithCustomArgument("-b:v 0");
        }
        else
        {
            output.WithSpeedPreset(video.SpeedPreset);
            output.WithConstantRateFactor(video.ConstantRateFactor);
        }

        output.ForcePixelFormat(AuthoringVideoSettings.PixelFormat);

        if (video.FrameRate > 0d && video.FrameRateMode == AuthoringFrameRateMode.Encoder)
        {
            output.WithFramerate(video.FrameRate);
        }

        if (video.KeyframeIntervalFrames > 0)
        {
            output.WithCustomArgument("-g " + video.KeyframeIntervalFrames.ToString(CultureInfo.InvariantCulture));
        }

        // ONE -vf for the whole chain: ffmpeg keeps only the last one it is given, and the wrapper throws
        // rather than let a second call quietly discard the first.
        output.WithVideoFilters(filters =>
        {
            if (!video.FrameSize.IsSourceSize)
            {
                // The RAW custom overload, not the (key, value) one. The aspect-preserving forms render an
                // ffmpeg expression whose commas are already escaped for the filtergraph; the key/value
                // overload escapes for two more unescape passes and would turn each one into three
                // backslashes, which ffmpeg then refuses.
                filters.Custom(RenderScaleFilter(video));
            }

            if (video.FrameRate > 0d && video.FrameRateMode == AuthoringFrameRateMode.Filter)
            {
                filters.Fps(video.FrameRate);
            }

            if (lut != null && lut.HasGrade)
            {
                // The wrapper escapes the path for both of ffmpeg's unescape passes, so a colon, a space or
                // a backslash in it survives.
                filters.Lut3D(lut.FilterPath);
            }

            // LAST, so whatever the chain did - and lut3d works in RGB, so it did convert - the encoder is
            // handed the 8-bit 4:2:0 the streamable profile recommends.
            filters.Format(AuthoringVideoSettings.PixelFormat);
        });
    }

    private static void ApplyAudio(FFMpegArgumentOptions output, VideoAuthoringRequest request)
    {
        AuthoringAudioSettings audio = request.Audio;

        if (!audio.Include)
        {
            output.DisableChannel(Channel.Audio);
            return;
        }

        string encoder = AudioEncoderNameFor(audio.Codec, request.Flavour);
        output.WithAudioCodec(encoder);

        if (string.Equals(encoder, AuthoringEncoderNames.LibVorbis, StringComparison.Ordinal)
            && audio.VorbisQuality.HasValue)
        {
            output.WithCustomArgument(
                "-q:a " + audio.VorbisQuality.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            output.WithAudioBitrate(audio.BitrateKilobitsPerSecond);
        }

        output.WithAudioSamplingRate(audio.SampleRateHz);
        output.WithCustomArgument("-ac " + audio.Channels.ToString(CultureInfo.InvariantCulture));
    }

    // The WebM-profile pass's picture and sound track metadata - the same three settings the bespoke muxer and the
    // in-process Matroska muxer write. Each only when set; the sound's only when there is sound.
    private static void ApplyTrackMetadata(FFMpegArgumentOptions output, VideoAuthoringRequest request)
    {
        if (!string.IsNullOrEmpty(request.Video.TrackName))
        {
            output.WithStreamMetadata(Channel.Video, 0, "title", request.Video.TrackName);
        }

        if (!request.Audio.Include) return;

        if (!string.IsNullOrEmpty(request.Audio.Language))
        {
            output.WithStreamMetadata(Channel.Audio, 0, "language", request.Audio.Language);
        }

        if (!string.IsNullOrEmpty(request.Audio.Name))
        {
            output.WithStreamMetadata(Channel.Audio, 0, "title", request.Audio.Name);
        }
    }

    private static void ApplyCaptions(FFMpegArgumentOptions output, VideoAuthoringRequest request)
    {
        if (request.Captions.Count == 0) return;

        // COPY, never re-encode. FFmpeg's webvtt ENCODER discards cue identifiers and positioning settings,
        // so a track that went through it would arrive stripped of exactly the parts a player needs.
        output.WithSubtitleCodec(AuthoringEncoderNames.SubtitleCopy);

        for (int i = 0; i < request.Captions.Count; i++)
        {
            AuthoringCaptionInput caption = request.Captions[i];

            if (caption.Language.Length > 0)
            {
                output.WithStreamMetadata(Channel.Subtitle, i, "language", caption.Language);
            }

            if (caption.Name.Length > 0)
            {
                output.WithStreamMetadata(Channel.Subtitle, i, "title", caption.Name);
            }
        }

        for (int i = 0; i < request.Captions.Count; i++)
        {
            List<Disposition> flags = DispositionsFor(request.Captions[i].Flags);
            if (flags.Count == 0) continue;

            output.WithDisposition(Channel.Subtitle, i, flags.ToArray());
        }
    }

    private static List<Disposition> DispositionsFor(CaptionTrackFlags flags)
    {
        List<Disposition> dispositions = new List<Disposition>(3);

        if ((flags & CaptionTrackFlags.Default) != 0) dispositions.Add(Disposition.Default);
        if ((flags & CaptionTrackFlags.Forced) != 0) dispositions.Add(Disposition.Forced);
        if ((flags & CaptionTrackFlags.HearingImpaired) != 0) dispositions.Add(Disposition.HearingImpaired);

        return dispositions;
    }

    private static string RenderScaleFilter(AuthoringVideoSettings video)
    {
        StringBuilder filter = new StringBuilder("scale=w=");
        filter.Append(Escape(video.FrameSize.RenderWidth()));
        filter.Append(":h=");
        filter.Append(Escape(video.FrameSize.RenderHeight()));
        filter.Append(":flags=");
        filter.Append(Escape(video.ScalerFlags));
        return filter.ToString();
    }

    // A comma would end the filter, so an expression that contains one escapes it ONCE - which is what the
    // filtergraph parser undoes on its way to the scale filter's own option parser.
    private static string Escape(string value) => value.Replace(",", "\\,");
}
