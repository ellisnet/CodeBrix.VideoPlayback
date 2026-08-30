using System;
using System.Globalization;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Enums;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Says which AV1 encoder the FFmpeg on THIS machine can actually run, so that the tests which encode ask
/// for one that is there.
/// </summary>
/// <remarks>
/// <para>
/// The library's default - and what the committed corpus was made with - is SVT-AV1. Not every FFmpeg build
/// carries it: the Windows "essentials" builds are built with libaom and WITHOUT libsvtav1, so a suite that
/// named libsvtav1 outright failed on a machine that was perfectly able to author.
/// </para>
/// <para>
/// None of the encoding tests are ABOUT SVT-AV1. They are about containers, captions, chapters, grades and
/// the streamable profile, and any AV1 encoder proves those equally well - so the answer is to encode with
/// whichever one is present rather than to skip. The tests that pin the exact command line ARE about the
/// encoder's name, but they render text instead of running FFmpeg, so they stay pinned to SVT-AV1 and are
/// untouched by any of this. Only a machine with no software AV1 encoder at all has to skip.
/// </para>
/// <para>
/// The hardware encoders an FFmpeg build may also list - av1_nvenc, av1_qsv, av1_amf, av1_vaapi - are
/// deliberately not considered: they need the matching device to be present and idle, which a test machine
/// cannot be asked for.
/// </para>
/// </remarks>
internal static class AuthoringEncoders
{
    private const string SvtAv1 = "libsvtav1";
    private const string AomAv1 = "libaom-av1";

    private static readonly bool HasSvtAv1 = CanEncodeWith(SvtAv1);
    private static readonly bool HasAomAv1 = CanEncodeWith(AomAv1);

    /// <summary>True when this machine's FFmpeg can encode AV1 at all.</summary>
    internal static bool IsAvailable => HasSvtAv1 || HasAomAv1;

    /// <summary>The encoder to author with here.</summary>
    internal static AuthoringVideoEncoder Encoder =>
        HasSvtAv1 ? AuthoringVideoEncoder.LibSvtAv1 : AuthoringVideoEncoder.LibAomAv1;

    /// <summary>What FFmpeg calls that encoder.</summary>
    internal static string Name => HasSvtAv1 ? SvtAv1 : AomAv1;

    /// <summary>
    /// The fastest setting that encoder's speed knob takes - 13 for SVT-AV1's <c>-preset</c>, 8 for libaom's
    /// <c>-cpu-used</c>. The tests want the fastest one there is; none of them looks at the picture quality.
    /// </summary>
    internal static int FastestSpeedPreset => HasSvtAv1 ? 13 : 8;

    /// <summary>Says what was looked for, for a skip message.</summary>
    internal static string Problem =>
        "Authoring needs an AV1 encoder, and neither '" + SvtAv1 + "' nor '" + AomAv1 + "' is in this "
        + "FFmpeg build. Check with 'ffmpeg -encoders | grep -E \"" + SvtAv1 + "|" + AomAv1 + "\"'. The "
        + "Windows 'essentials' builds carry libaom; a full build carries both.";

    /// <summary>Points a request's video settings at the encoder that is there, at its fastest setting.</summary>
    /// <param name="video">The settings to adjust.</param>
    internal static void ApplyFastest(AuthoringVideoSettings video)
    {
        video.Encoder = Encoder;
        video.SpeedPreset = FastestSpeedPreset;
    }

    /// <summary>
    /// Puts the same choice on a RAW FFmpeg output - the one the test fixtures use to make the clips that are
    /// authored FROM, which do not go through the library.
    /// </summary>
    /// <param name="output">The output to configure.</param>
    /// <param name="constantRateFactor">The rate factor to encode at.</param>
    internal static void ApplyFastestTo(FFMpegArgumentOptions output, int constantRateFactor)
    {
        output.WithVideoCodec(Name);

        if (Encoder == AuthoringVideoEncoder.LibAomAv1)
        {
            // libaom's speed knob is -cpu-used, and its rate factor only counts with the bit rate pinned to
            // zero - the same shape the authoring library renders for it.
            output.WithCustomArgument(
                "-cpu-used " + FastestSpeedPreset.ToString(CultureInfo.InvariantCulture));
            output.WithConstantRateFactor(constantRateFactor);
            output.WithCustomArgument("-b:v 0");
            return;
        }

        output.WithSpeedPreset(FastestSpeedPreset);
        output.WithConstantRateFactor(constantRateFactor);
    }

    private static bool CanEncodeWith(string name)
    {
        try
        {
            return FFMpeg.TryGetCodec(name, out Codec codec) && codec != null && codec.EncodingSupported;
        }
        catch (Exception)
        {
            // No FFmpeg to ask, or it answered with something unreadable. Either way there is no encoder
            // here; the caller's own FFmpeg check is what names that as the reason.
            return false;
        }
    }
}
