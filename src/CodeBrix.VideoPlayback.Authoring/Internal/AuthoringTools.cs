using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Enums;
using CodeBrix.VideoProcessing.Helpers;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Checks that the one external tool this library needs is actually there, and which of the encoders this
/// library can ask for it was built with.
/// </summary>
/// <remarks>
/// FFmpeg is the ONLY thing that has to be installed to author a <c>.cbv</c> file - not mkvmerge, not the
/// dav1d command-line tool, not Python, not anything else. Everything else in the chain is CodeBrix code. A
/// message that just said "not found" would leave a reader guessing, so the message names both binaries and
/// says where they were looked for.
/// <para>
/// A missing ENCODER is a warning, never a failure of the check: a build without SVT-AV1 still authors with
/// libaom, by request or through the AV1 encoder fallback, so refusing it here would refuse a working machine.
/// </para>
/// </remarks>
internal static class AuthoringTools
{
    /// <summary>The first sentence of every explanation of a missing SVT-AV1 encoder.</summary>
    internal const string MissingSvtAv1 =
        "The SVT-AV1 encoder library (libsvtav1) is not present on this machine, or the ffmpeg being used could "
        + "not find it.";

    /// <summary>What to do about a missing SVT-AV1 encoder, when the fallback is not already on.</summary>
    internal const string SvtAv1Remedies =
        "To author AV1 without installing anything, opt in to the libaom-av1 fallback: set "
        + "AllowAv1EncoderFallback = true on the VideoAuthoringRequest, or for every run with "
        + "GlobalFFOptions.Configure(o => o.AllowAv1EncoderFallback = true). Or ask for libaom-av1 outright with "
        + "Video.Encoder = AuthoringVideoEncoder.LibAomAv1, or install an ffmpeg build that includes libsvtav1.";

    /// <summary>What is said when the fallback was on and could not help, and the wrapper did not say why.</summary>
    internal const string FallbackCouldNotHelp =
        "AllowAv1EncoderFallback is on, but the fallback to libaom-av1 could not be used. Install an ffmpeg build "
        + "that includes libsvtav1 or libaom-av1.";

    /// <summary>Reports whether ffmpeg and ffprobe can be run.</summary>
    /// <param name="problem">What is missing, or an empty string when nothing is.</param>
    /// <returns>True when both were found.</returns>
    internal static bool TryVerify(out string problem)
    {
        try
        {
            FFMpegHelper.VerifyFFMpegExists(GlobalFFOptions.Current);
            FFProbeHelper.VerifyFFProbeExists(GlobalFFOptions.Current);
        }
        catch (Exception ex)
        {
            problem = Describe(ex);
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Reports whether ffmpeg and ffprobe can be run, and warns about missing encoders.</summary>
    /// <param name="problem">What is missing, or an empty string when nothing is.</param>
    /// <param name="warnings">One sentence per missing encoder; empty when none is missing or the tools are not there.</param>
    /// <returns>True when both binaries were found, whatever the encoders.</returns>
    internal static bool TryVerify(out string problem, out IReadOnlyList<string> warnings)
    {
        if (!TryVerify(out problem))
        {
            warnings = Array.Empty<string>();
            return false;
        }

        HashSet<string> encoders;
        try
        {
            encoders = new HashSet<string>(
                FFMpeg.GetCodecs().Where(codec => codec.EncodingSupported).Select(codec => codec.Name),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // The binaries run, so this is not a problem with the tools - it is a question that got no answer.
            warnings = new[]
            {
                "The encoders in this ffmpeg build could not be listed, so none of them was checked. The tool "
                + "reported: " + FirstLine(ex.Message),
            };
            return true;
        }

        warnings = DescribeEncoderWarnings(encoders.Contains);
        return true;
    }

    /// <summary>Throws unless ffmpeg and ffprobe can be run.</summary>
    /// <exception cref="VideoAuthoringException">One of them could not be found.</exception>
    internal static void Verify()
    {
        if (TryVerify(out string problem)) return;

        throw new VideoAuthoringException(problem);
    }

    /// <summary>Throws unless ffmpeg and ffprobe can be run, and warns about missing encoders.</summary>
    /// <param name="warnings">One sentence per missing encoder; empty when none is missing.</param>
    /// <exception cref="VideoAuthoringException">One of the binaries could not be found.</exception>
    internal static void Verify(out IReadOnlyList<string> warnings)
    {
        if (TryVerify(out string problem, out warnings)) return;

        throw new VideoAuthoringException(problem);
    }

    /// <summary>The warnings for a build, given a way to ask whether it has an encoder.</summary>
    /// <param name="hasEncoder">Answers whether the build has the named encoder.</param>
    /// <returns>One sentence per encoder this library can ask for that the build does not have.</returns>
    internal static IReadOnlyList<string> DescribeEncoderWarnings(Func<string, bool> hasEncoder)
    {
        List<string> warnings = new List<string>();

        bool svtAv1 = hasEncoder(AuthoringEncoderNames.LibSvtAv1);
        bool aomAv1 = hasEncoder(AuthoringEncoderNames.LibAomAv1);

        if (!svtAv1 && !aomAv1)
        {
            warnings.Add(
                "Neither AV1 encoder this library can use is in this ffmpeg build - not libsvtav1, the default, and "
                + "not libaom-av1 - so no video can be authored with it, and AllowAv1EncoderFallback cannot help. "
                + "Install an ffmpeg build that includes libsvtav1.");
        }
        else if (!svtAv1)
        {
            warnings.Add(MissingSvtAv1 + " Authoring with the default encoder will fail. " + SvtAv1Remedies);
        }
        else if (!aomAv1)
        {
            warnings.Add(
                "The libaom AV1 encoder (libaom-av1) is not in this ffmpeg build, so a request that sets "
                + "Video.Encoder = AuthoringVideoEncoder.LibAomAv1 will fail. The default encoder, libsvtav1, is "
                + "present, so nothing else is affected.");
        }

        if (!hasEncoder(AuthoringEncoderNames.LibOpus))
        {
            warnings.Add(
                "The Opus encoder (libopus) is not in this ffmpeg build, so a WebM-profile request that includes "
                + "sound - Opus is that flavour's default - will fail. Set Audio.Codec = "
                + "AuthoringAudioCodec.LibVorbis, leave the sound out, or install an ffmpeg build that includes "
                + "libopus.");
        }

        if (!hasEncoder(AuthoringEncoderNames.LibVorbis))
        {
            warnings.Add(
                "The Vorbis encoder (libvorbis) is not in this ffmpeg build, so a bespoke request that includes "
                + "sound - Vorbis is the only codec that flavour takes - will fail, as will any request that sets "
                + "Audio.Codec = AuthoringAudioCodec.LibVorbis. Install an ffmpeg build that includes libvorbis.");
        }

        return warnings;
    }

    private static string Describe(Exception ex)
    {
        string folder = GlobalFFOptions.Current.BinaryFolder;
        string where = string.IsNullOrEmpty(folder)
            ? "on the PATH"
            : "in the configured binary folder '" + folder + "' and then on the PATH";

        return "Authoring needs 'ffmpeg' and 'ffprobe', and they were looked for " + where + ". They are the ONE "
            + "external tool this library requires - everything else in the chain is CodeBrix code. On a "
            + "Debian-based machine 'sudo apt install ffmpeg' supplies both; check the encoders with "
            + "'ffmpeg -encoders | grep -E \"libsvtav1|libopus|libvorbis\"'. Point the library at a private "
            + "build instead with GlobalFFOptions.Configure(o => o.BinaryFolder = ...). The tool reported: "
            + ex.Message;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int newline = text.IndexOfAny(new[] { '\r', '\n' });
        return newline < 0 ? text : text.Substring(0, newline);
    }
}
