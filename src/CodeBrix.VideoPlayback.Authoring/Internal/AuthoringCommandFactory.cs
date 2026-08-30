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
        if (hasChapters)
        {
            // The chapter file is an ordinary INPUT and -map_metadata names its index. Passing the text to
            // the wrapper's AddMetaData would work too, but it writes a temporary file with a random name
            // into the command line, and a command line that a manifest records has to be the same every
            // run and has to name a file a reader can go and look at.
            arguments = arguments.AddFileInput(request.ChaptersPath, false);
            arguments = arguments.MapMetaData(request.Captions.Count + 1);
        }

        return arguments.OutputToFile(request.OutputPath, true, output =>
        {
            if (request.SelectStreamsExplicitly)
            {
                output.SelectStream(0, 0, Channel.Video);
                if (request.Audio.Include) output.SelectStream(0, 0, Channel.Audio);
            }

            for (int i = 0; i < request.Captions.Count; i++)
            {
                output.SelectStream(0, i + 1, Channel.Subtitle);
            }

            if (!hasChapters && !request.CopySourceMetadata) output.WithoutMetadata();

            ApplyVideo(output, request, lut);
            ApplyAudio(output, request);
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
            default:
                return flavour == VideoAuthoringFlavour.Bespoke
                    ? AuthoringEncoderNames.LibVorbis
                    : AuthoringEncoderNames.LibOpus;
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
