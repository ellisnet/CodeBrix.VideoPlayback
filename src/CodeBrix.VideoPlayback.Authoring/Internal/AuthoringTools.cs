using System;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Helpers;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Checks that the one external tool this library needs is actually there.
/// </summary>
/// <remarks>
/// FFmpeg is the ONLY thing that has to be installed to author a <c>.cbv</c> file - not mkvmerge, not the
/// dav1d command-line tool, not Python, not anything else. Everything else in the chain is CodeBrix code. A
/// message that just said "not found" would leave a reader guessing, so the message names both binaries and
/// says where they were looked for.
/// </remarks>
internal static class AuthoringTools
{
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

    /// <summary>Throws unless ffmpeg and ffprobe can be run.</summary>
    /// <exception cref="VideoAuthoringException">One of them could not be found.</exception>
    internal static void Verify()
    {
        if (TryVerify(out string problem)) return;

        throw new VideoAuthoringException(problem);
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
}
