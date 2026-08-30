using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Authoring;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>
/// Turns one corpus plan entry into an authoring request, and hands it to the authoring library.
/// </summary>
/// <remarks>
/// <para>
/// This class USED to build the FFmpeg command lines itself, as the working prototype of the authoring
/// helper this program planned to expose. That helper now exists - CodeBrix.VideoPlayback.Authoring - so the
/// prototype is gone and what is left here is the translation from a corpus plan entry into a
/// <see cref="VideoAuthoringRequest" />. Every decision the prototype documented survives as a setting:
/// explicit stream selection, dropped source metadata, the lanczos scaler, auto-rotation on the input, one
/// video filter chain, and the single line that moves the cues to the front.
/// </para>
/// <para>
/// The three FFmpeg-muxed profiles produce EXACTLY the command lines the committed manifest records, and
/// there is a test that says so - which is what lets the eighteen files already in the repository stand
/// unregenerated while the fourth folder is added beside them.
/// </para>
/// </remarks>
public static class CorpusEncoder
{
    /// <summary>Builds the authoring request for one corpus file.</summary>
    /// <param name="item">The file to build the request for.</param>
    /// <param name="sourcePath">The Public-Domain MP4 the file is derived from.</param>
    /// <param name="outputPath">Where the encoder writes.</param>
    /// <param name="validateProfile">False to skip the streamable-profile check on the finished file.</param>
    /// <returns>The request, ready to render or to run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item" /> is null.</exception>
    public static VideoAuthoringRequest BuildRequest(
        CorpusItem item,
        string sourcePath,
        string outputPath,
        bool validateProfile = true)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = item.Flavour,
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Container = item.Container,
            CuesToFront = item.CuesToFront,

            // The phone writes a third, timed-metadata stream beside the picture and the sound. Nothing
            // downstream wants it, and a container that carries it is no longer the plain audio-and-video
            // file this corpus is meant to be, so both wanted streams are named explicitly instead of
            // relying on FFmpeg's default stream selection.
            SelectStreamsExplicitly = true,

            // Drops the source's global metadata - the recording's creation time and the device strings.
            // These are demo assets, so they carry no provenance of their own beyond the README beside them.
            CopySourceMetadata = false,

            // The three off-the-shelf folders are the corpus's NEGATIVE control: they are muxed the way
            // anything on the internet is muxed and they fail the "cues sit before the first cluster" rule
            // on purpose. The report is still recorded; it is just not a reason to stop.
            ValidateProfile = validateProfile,
            FailWhenProfileFails = false,
        };

        request.Video.Encoder = AuthoringVideoEncoder.LibSvtAv1;
        request.Video.SpeedPreset = item.Tier.Preset;
        request.Video.ConstantRateFactor = item.Tier.Crf;

        // The plan states both dimensions, because a portrait source has to come out at the portrait size the
        // plan predicted rather than at whatever the aspect ratio would have given.
        request.Video.FrameSize = AuthoringFrameSize.Exact(item.Width, item.Height);
        request.Video.ScalerFlags = CorpusPlan.ScalerFlags;

        // Conforms the picture to exactly 30 frames per second. Both sources were recorded at a hair under 30
        // (the phone's clock, not its intent); pinning the rate at the ENCODER makes the output
        // constant-frame-rate, which is what makes the keyframe interval below mean two seconds.
        request.Video.FrameRate = CorpusPlan.FramesPerSecond;
        request.Video.FrameRateMode = AuthoringFrameRateMode.Encoder;
        request.Video.KeyframeIntervalFrames = CorpusPlan.KeyframeIntervalFrames;

        // -autorotate is an INPUT option: FFmpeg only honours it before the -i it belongs to. It is FFmpeg's
        // default, but the portrait source is the whole reason this corpus exists in two orientations, so the
        // intention is written down rather than inherited. With it on, the display matrix is applied while
        // decoding and the frames that reach the filter chain are already TRUE portrait - taller than they
        // are wide - with no rotation left to write out.
        request.Video.AutoRotate = true;

        request.Audio.Codec = item.AudioCodec;
        request.Audio.BitrateKilobitsPerSecond = item.Tier.AudioKilobitsPerSecond;
        request.Audio.SampleRateHz = CorpusPlan.AudioSampleRateHz;
        request.Audio.Channels = CorpusPlan.AudioChannels;

        return request;
    }

    /// <summary>Renders the command lines for one corpus file without starting anything.</summary>
    /// <param name="item">The file to build the commands for.</param>
    /// <param name="sourcePath">The Public-Domain MP4 the file is derived from.</param>
    /// <param name="outputPath">Where the encoder writes.</param>
    /// <returns>One command for the three FFmpeg-muxed profiles, two for Mode2.</returns>
    public static IReadOnlyList<AuthoringCommand> BuildCommands(CorpusItem item, string sourcePath, string outputPath) =>
        CbvAuthor.RenderCommands(BuildRequest(item, sourcePath, outputPath));

    /// <summary>Encodes one corpus file, reporting progress as it goes.</summary>
    /// <param name="item">The file to produce.</param>
    /// <param name="sourcePath">The Public-Domain MP4 the file is derived from.</param>
    /// <param name="outputPath">Where the encoder writes.</param>
    /// <param name="sourceDuration">The source's duration, so progress can be reported as a percentage.</param>
    /// <param name="onProgress">Called with a whole-number percentage as the encode advances.</param>
    /// <param name="validateProfile">False to skip the streamable-profile check on the finished file.</param>
    /// <returns>What the authoring library produced, commands and profile report included.</returns>
    public static VideoAuthoringResult Encode(
        CorpusItem item,
        string sourcePath,
        string outputPath,
        TimeSpan sourceDuration,
        Action<AuthoringProgress> onProgress,
        bool validateProfile = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        VideoAuthoringRequest request = BuildRequest(item, sourcePath, outputPath, validateProfile);
        request.SourceDuration = sourceDuration;
        request.ProgressCallback = onProgress;

        return CbvAuthor.Write(request);
    }
}
