using System;
using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Enums;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>
/// Builds and runs the FFmpeg command line for one corpus file, entirely through the
/// CodeBrix.VideoProcessing argument model.
/// </summary>
/// <remarks>
/// <para>
/// THIS CLASS IS THE PROTOTYPE of the authoring helper the library side of this program will eventually
/// expose, so it is written to be read: one method builds the command, and the Mode1 difference is a single
/// clearly-labelled call rather than a second code path. Nothing here shells out to <c>ffmpeg</c> directly -
/// the library owns the process, the argument rendering and the invariant-culture formatting of every number
/// on the command line.
/// </para>
/// <para>
/// Three facts about the wrapper are load-bearing here and are worth stating rather than rediscovering:
/// there is exactly ONE <c>-vf</c> per output, so the whole video filter chain is built in a single
/// <c>WithVideoFilters</c> call; <c>-autorotate</c> is an INPUT option, so it goes in the input callback
/// where the builder renders it ahead of the matching <c>-i</c>; and an <see cref="FFMpegArguments" />
/// instance is single-use, so every file gets a fresh chain.
/// </para>
/// </remarks>
public static class CorpusEncoder
{
    /// <summary>Builds the runnable command for one corpus file without starting anything.</summary>
    /// <param name="item">The file to build the command for.</param>
    /// <param name="sourcePath">The Public-Domain MP4 the file is derived from.</param>
    /// <param name="outputPath">Where the encoder writes.</param>
    /// <returns>The processor, whose <see cref="FFMpegArgumentProcessor.Arguments" /> is the rendered line.</returns>
    public static FFMpegArgumentProcessor BuildCommand(CorpusItem item, string sourcePath, string outputPath)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        return FFMpegArguments
            .FromFileInput(sourcePath, true, input => input
                // -autorotate is an INPUT option: FFmpeg only honours it before the -i it belongs to. It is
                // FFmpeg's default, but the portrait source is the whole reason this corpus exists in two
                // orientations, so the intention is written down rather than inherited. With it on, the
                // display matrix is applied while decoding and the frames that reach the filter chain are
                // already TRUE portrait - taller than they are wide - with no rotation left to write out.
                .AutoRotate(true))
            .OutputToFile(outputPath, true, output =>
            {
                output
                    // The phone writes a third, timed-metadata stream beside the picture and the sound.
                    // Nothing downstream wants it, and a container that carries it is no longer the plain
                    // audio-and-video file this corpus is meant to be, so both wanted streams are named
                    // explicitly instead of relying on FFmpeg's default stream selection.
                    .SelectStream(0, 0, Channel.Video)
                    .SelectStream(0, 0, Channel.Audio)

                    // Drops the source's global metadata - the recording's creation time and the device
                    // strings. These are demo assets, so they carry no provenance of their own beyond the
                    // README beside them.
                    .WithoutMetadata()

                    .WithVideoCodec(CorpusPlan.VideoEncoder)
                    .WithSpeedPreset(item.Tier.Preset)
                    .WithConstantRateFactor(item.Tier.Crf)
                    .ForcePixelFormat(CorpusPlan.PixelFormat)

                    // Conforms the picture to exactly 30 frames per second. Both sources were recorded at a
                    // hair under 30 (the phone's clock, not its intent); pinning the rate makes the output
                    // constant-frame-rate, which is what makes the keyframe interval below mean two seconds.
                    .WithFramerate(CorpusPlan.FramesPerSecond)

                    // The keyframe interval, in frames. Set explicitly: SVT-AV1's own default is far longer
                    // than two seconds, and a long interval is exactly what makes a scrub feel slow.
                    .WithCustomArgument(
                        "-g " + CorpusPlan.KeyframeIntervalFrames.ToString(CultureInfo.InvariantCulture))

                    // ONE -vf for the whole chain. Custom(name, pairs) is used rather than Scale(w, h)
                    // because the scaler flags have to be named - the plain Scale helper renders no flags and
                    // would leave the choice of resampler to FFmpeg's default (bicubic).
                    .WithVideoFilters(filters => filters
                        .Custom(
                            "scale",
                            ("w", item.Width.ToString(CultureInfo.InvariantCulture)),
                            ("h", item.Height.ToString(CultureInfo.InvariantCulture)),
                            ("flags", CorpusPlan.ScalerFlags))

                        // Both sources are full-range 8-bit (yuvj420p). Converting inside the chain rather
                        // than leaving it to the encoder keeps the range conversion in the scaler, where it
                        // belongs, and makes the 8-bit 4:2:0 the streamable profile recommends explicit.
                        .Format(CorpusPlan.PixelFormat))

                    .WithAudioCodec(CorpusPlan.AudioEncoder)
                    .WithAudioBitrate(item.Tier.AudioKilobitsPerSecond)
                    .WithAudioSamplingRate(CorpusPlan.AudioSampleRateHz)
                    .WithCustomArgument(
                        "-ac " + CorpusPlan.AudioChannels.ToString(CultureInfo.InvariantCulture))

                    // The extension never decides the container here: Mode1 files are called .cbv and are
                    // WebM inside, so the muxer is always named.
                    .ForceFormat(item.ContainerFormat);

                if (item.Profile == CorpusProfile.Mode1)
                {
                    // THE ONE LINE THAT MAKES A MODE1 FILE. CuesToFront renders `-cues_to_front 1`, which
                    // tells the Matroska muxer to shift the clusters and put the index at the FRONT of the
                    // file. That is the rule the streamable profile is built around: a reader that has the
                    // first few kilobytes of the file already has the whole seek index, so the first scrub
                    // costs no extra round trip. Everything else about a Mode1 file - AV1, Opus, 8-bit
                    // 4:2:0, a declared duration, known element sizes - it already shares with the plain
                    // WebM file beside it.
                    output.CuesToFront(true);
                }
            });
    }

    /// <summary>Encodes one corpus file, reporting progress as it goes.</summary>
    /// <param name="item">The file to produce.</param>
    /// <param name="sourcePath">The Public-Domain MP4 the file is derived from.</param>
    /// <param name="outputPath">Where the encoder writes.</param>
    /// <param name="sourceDuration">The source's duration, so progress can be reported as a percentage.</param>
    /// <param name="onProgress">Called with a whole-number percentage as the encode advances.</param>
    /// <returns>The rendered command line that produced the file.</returns>
    public static string Encode(
        CorpusItem item,
        string sourcePath,
        string outputPath,
        TimeSpan sourceDuration,
        Action<int> onProgress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        FFMpegArgumentProcessor processor = BuildCommand(item, sourcePath, outputPath);
        string rendered = processor.Arguments;

        if (onProgress != null)
        {
            int lastReported = -1;

            // NotifyOnProgress's percentage overload needs the total duration and it is not optional; the
            // duration comes from the probe of the source that the caller has already done.
            processor.NotifyOnProgress(
                percent =>
                {
                    int whole = (int)percent;
                    if (whole <= lastReported) return;
                    lastReported = whole;
                    onProgress(whole);
                },
                sourceDuration);
        }

        processor.ProcessSynchronously();
        return rendered;
    }
}
