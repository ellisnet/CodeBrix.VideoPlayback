using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Core.Output;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// Exports CodeBrix video files - <c>.cbv</c> (Mode2), <c>.cbvmaster</c> (Mode3) and WebM-profile <c>.webm</c> /
/// <c>.mkv</c> (Mode1) - to standard MP4 files: H.264 by default, HEVC on request, AAC sound.
/// </summary>
/// <remarks>
/// <para>
/// THE DECODERS are the application's: the source is read with the core's container readers and decoded with the
/// decoders registered in the core's registries - AV1 through <c>VideoDecoders</c> (reference
/// CodeBrix.VideoPlayback.Dav1d and call <c>CodeBrixVideoPlaybackDav1d.Register()</c>), the sound through the shared
/// audio output (Vorbis and FLAC built into CodeBrix.Audio.Core, Opus from CodeBrix.Audio.Opus's
/// <c>CodeBrixAudioOpus.Register()</c>). A missing decoder is refused with a message naming what to register.
/// </para>
/// <para>
/// THE WRITER is the video-processing engine the application registered, or - when it registered none - the
/// desktop ffmpeg engine, exactly as the master flavour of <see cref="CbvAuthor" /> chooses it.
/// </para>
/// <para>
/// A MASTER FILE WITH ALPHA is composited over its <see cref="VideoExportRequest.Backdrop" /> - 50% grey by default -
/// because an MP4 has no transparency. Every other source is written as it is decoded and any backdrop is ignored
/// with a note. Pictures are always written at 8 bits (deeper sources are reduced, with a note) and at the source's
/// nominal frame rate (frames on another timing are dropped or repeated to fit it). Captions and chapters are not
/// carried into the MP4.
/// </para>
/// </remarks>
public static class VideoExporter
{
    /// <summary>The label of the export's one pass, as progress reports carry it.</summary>
    public const string PassLabel = "export";

    /// <summary>Exports one file.</summary>
    /// <param name="request">What to export and how.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="VideoAuthoringException">
    /// The request cannot be honoured: a missing file, an output that is not <c>.mp4</c>, HEVC on an engine without
    /// it, a codec with no registered decoder, an engine that cannot run.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <c>request.CancellationToken</c> was cancelled; the partly written output has been deleted.
    /// </exception>
    public static VideoExportResult Run(VideoExportRequest request) => Run(request, InProcessAuthoring.ResolveEngine());

    /// <summary>
    /// Describes what <see cref="Run(VideoExportRequest)" /> would do - one line per step - reading only the source's
    /// header and writing nothing.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>
    /// A deterministic plan, lines separated by <c>\n</c>: <c>source</c>, <c>decode</c>, <c>composite</c> (or
    /// <c>no composite</c>), <c>audio</c>, <c>write</c>, then any <c>note</c> lines.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="VideoAuthoringException">The request cannot be honoured as it stands.</exception>
    public static string DryRun(VideoExportRequest request) => DryRun(request, InProcessAuthoring.ResolveEngine());

    /// <summary>Exports with a given engine - the seam the tests use.</summary>
    /// <param name="request">The request.</param>
    /// <param name="engine">The engine to write with.</param>
    /// <returns>What was written.</returns>
    internal static VideoExportResult Run(VideoExportRequest request, IVideoProcessor engine)
    {
        Validate(request);
        request.CancellationToken.ThrowIfCancellationRequested();
        RequireEngine(request, engine, true);

        using ExportPipeline pipeline = new ExportPipeline(request, engine);
        return pipeline.Run();
    }

    /// <summary>Plans with a given engine - the seam the tests use.</summary>
    /// <param name="request">The request.</param>
    /// <param name="engine">The engine that would write.</param>
    /// <returns>The plan.</returns>
    internal static string DryRun(VideoExportRequest request, IVideoProcessor engine)
    {
        Validate(request);
        request.CancellationToken.ThrowIfCancellationRequested();
        RequireEngine(request, engine, false);

        ExportBackdrop backdrop = request.Backdrop ?? new ExportBackdrop();
        List<string> lines = new List<string>();
        List<string> notes = new List<string>();

        using ExportSource source = ExportSource.Open(request.SourcePath);
        MediaTrackInfoView video = new MediaTrackInfoView(source);

        lines.Add("source " + source.Mode + " " + request.SourcePath);

        int depth = source.StatedBitDepth;
        FrameRate stated = source.StatedFrameRate;
        lines.Add("decode " + source.VideoTrack.CodecId + " " + video.Size
            + " " + (stated.IsSpecified ? ExportSource.Describe(stated) + " fps" : "fps-from-timestamps")
            + " " + (depth > 0 ? Number(depth) + "-bit" : "bit-depth-from-decoder")
            + (depth > 8 ? " reduced-to-8-bit" : string.Empty)
            + (source.HasAlpha ? " alpha" : " no-alpha"));

        if (source.HasAlpha)
        {
            lines.Add("composite " + DescribeBackdrop(backdrop, video.Width, video.Height));
        }
        else
        {
            lines.Add("no composite - the source has no alpha plane");
            if (!IsDefault(backdrop)) notes.Add(BackdropIgnoredNote);
        }

        lines.Add(DescribeAudioPlan(request, source, backdrop));

        lines.Add("write mp4 " + (request.Codec == VideoExportCodec.Hevc ? "hevc" : "h264") + " " + engine.Name
            + " " + DescribeQuality(request.Quality)
            + " keyint " + (request.KeyframeIntervalFrames > 0 ? Number(request.KeyframeIntervalFrames) : "auto")
            + (request.FastStart ? " faststart" : " no-faststart")
            + " " + (source.HasAlpha ? "rgba" : "i420") + "-input"
            + " title " + (ResolveTitle(request, source) == null ? "none" : "'" + ResolveTitle(request, source) + "'")
            + " -> " + request.OutputPath);

        if (source.HasCaptionsOrChapters) notes.Add(CaptionsNote);
        foreach (string note in notes) lines.Add("note " + note);

        return string.Join("\n", lines);
    }

    /// <summary>The note for a backdrop given to a source without alpha.</summary>
    internal const string BackdropIgnoredNote = "the source has no alpha plane; backdrop ignored";

    /// <summary>The note for a source that carries captions or chapters.</summary>
    internal const string CaptionsNote =
        "the source carries captions and/or chapters; an MP4 export does not carry them, so they were left out";

    /// <summary>Checks a request on its own, before anything is opened.</summary>
    /// <param name="request">The request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="VideoAuthoringException">A setting is wrong or a file is missing.</exception>
    internal static void Validate(VideoExportRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new VideoAuthoringException("VideoExportRequest.SourcePath is required: the .cbv, .cbvmaster or .webm file to export.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new VideoAuthoringException("VideoExportRequest.OutputPath is required: where the .mp4 file goes.");
        }

        if (!string.Equals(Path.GetExtension(request.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new VideoAuthoringException(
                "VideoExportRequest.OutputPath '" + request.OutputPath + "' does not end in .mp4; an export writes MP4 "
                + "files only.");
        }

        if (string.Equals(Path.GetFullPath(request.SourcePath), Path.GetFullPath(request.OutputPath), StringComparison.Ordinal))
        {
            throw new VideoAuthoringException("VideoExportRequest.OutputPath is the source file itself.");
        }

        if (!File.Exists(request.SourcePath))
        {
            throw new VideoAuthoringException("The export source '" + request.SourcePath + "' does not exist.");
        }

        if (!Enum.IsDefined(request.Codec))
        {
            throw new VideoAuthoringException("VideoExportRequest.Codec " + (int)request.Codec + " is not a VideoExportCodec.");
        }

        if (request.KeyframeIntervalFrames < 0)
        {
            throw new VideoAuthoringException(
                "VideoExportRequest.KeyframeIntervalFrames is " + Number(request.KeyframeIntervalFrames)
                + "; it is 0 for the engine's default or a positive number of frames.");
        }

        if (request.IncludeAudio && request.AudioBitrateKilobitsPerSecond <= 0)
        {
            throw new VideoAuthoringException(
                "VideoExportRequest.AudioBitrateKilobitsPerSecond is " + Number(request.AudioBitrateKilobitsPerSecond)
                + "; it must be greater than zero.");
        }

        ExportBackdrop backdrop = request.Backdrop;
        if (backdrop == null) return;

        if (!Enum.IsDefined(backdrop.Kind))
        {
            throw new VideoAuthoringException("ExportBackdrop.Kind " + (int)backdrop.Kind + " is not an ExportBackdropKind.");
        }

        if (backdrop.Kind == ExportBackdropKind.Image)
        {
            if (backdrop.ImageRgba != null)
            {
                if (backdrop.ImageWidth <= 0 || backdrop.ImageHeight <= 0
                    || (long)backdrop.ImageWidth * backdrop.ImageHeight * 4 != backdrop.ImageRgba.Length)
                {
                    throw new VideoAuthoringException(
                        "ExportBackdrop.ImageRgba holds " + Number(backdrop.ImageRgba.Length) + " bytes and ImageWidth x "
                        + "ImageHeight is " + Number(backdrop.ImageWidth) + "x" + Number(backdrop.ImageHeight)
                        + "; an RGBA image needs width x height x 4 bytes.");
                }
            }
            else if (string.IsNullOrWhiteSpace(backdrop.ImagePath))
            {
                throw new VideoAuthoringException(
                    "An image backdrop needs ExportBackdrop.ImagePath (or ImageRgba with its width and height).");
            }
        }
        else if (backdrop.Kind == ExportBackdropKind.Video && string.IsNullOrWhiteSpace(backdrop.VideoPath))
        {
            throw new VideoAuthoringException("A video backdrop needs ExportBackdrop.VideoPath.");
        }
    }

    /// <summary>Checks the engine can do what the request asks.</summary>
    /// <param name="request">The request.</param>
    /// <param name="engine">The engine.</param>
    /// <param name="mustRun">True for a real run, which also checks that the ffmpeg engine's binaries can be run.</param>
    /// <exception cref="VideoAuthoringException">The engine cannot.</exception>
    internal static void RequireEngine(VideoExportRequest request, IVideoProcessor engine, bool mustRun)
    {
        if (request.Codec == VideoExportCodec.Hevc && !engine.Supports(VideoProcessorCapabilities.HevcOutput))
        {
            throw new VideoAuthoringException(
                "The request asks for HEVC, and the '" + engine.Name + "' video-processing engine does not report "
                + "HevcOutput, so it cannot write HEVC MP4 files. Leave Codec at VideoExportCodec.H264, which every "
                + "engine writes.");
        }

        if (mustRun && !InProcessAuthoring.TryVerifyEngine(engine, out string problem))
        {
            throw new VideoAuthoringException("The export has no engine to write the MP4 with. " + problem);
        }
    }

    /// <summary>True when a backdrop is the untouched default - 50% grey.</summary>
    /// <param name="backdrop">The backdrop.</param>
    /// <returns>True for the default.</returns>
    internal static bool IsDefault(ExportBackdrop backdrop) =>
        backdrop.Kind == ExportBackdropKind.Colour && (backdrop.Colour & 0xFFFFFF) == ExportBackdrop.DefaultColour;

    /// <summary>The title the MP4 gets: the request's, or the source's own.</summary>
    /// <param name="request">The request.</param>
    /// <param name="source">The source.</param>
    /// <returns>The title, or null.</returns>
    internal static string ResolveTitle(VideoExportRequest request, ExportSource source) =>
        !string.IsNullOrEmpty(request.Title) ? request.Title : source.Title;

    /// <summary>The dry run's words for a backdrop.</summary>
    /// <param name="backdrop">The backdrop.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <returns>For example <c>over colour #808080</c>.</returns>
    internal static string DescribeBackdrop(ExportBackdrop backdrop, int width, int height)
    {
        string size = Number(width) + "x" + Number(height);
        switch (backdrop.Kind)
        {
            case ExportBackdropKind.Image:
                if (backdrop.ImageRgba != null)
                {
                    string imageSize = Number(backdrop.ImageWidth) + "x" + Number(backdrop.ImageHeight);
                    return "over image rgba " + imageSize + (imageSize == size ? " same-size" : " scaled-to " + size);
                }

                return "over image " + backdrop.ImagePath + " scaled-to " + size;
            case ExportBackdropKind.Video:
                return "over video " + backdrop.VideoPath + " scaled-to " + size
                    + " nearest-earlier-frame length longer-of-the-two grey-after-backdrop-ends";
            default:
                return "over colour #" + (backdrop.Colour & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The dry run's words for a quality setting.</summary>
    /// <param name="quality">The quality.</param>
    /// <returns>For example <c>engine-default</c>, <c>cq 23</c> or <c>2500 kbit/s</c>.</returns>
    internal static string DescribeQuality(VideoQuality quality) => quality.Mode switch
    {
        VideoQualityMode.ConstantQuality => "cq " + Number(quality.QualityLevel),
        VideoQualityMode.TargetBitrate => Number(quality.BitrateKilobitsPerSecond) + " kbit/s",
        _ => "engine-default",
    };

    private static string DescribeAudioPlan(VideoExportRequest request, ExportSource source, ExportBackdrop backdrop)
    {
        string aac = " -> aac " + Number(request.AudioBitrateKilobitsPerSecond) + " kbit/s";
        if (!request.IncludeAudio) return "audio none (IncludeAudio is false)";

        bool silentMaster = source.HasAlpha && (source.IsSilent || source.AudioTrack == null);
        if (!silentMaster)
        {
            if (source.AudioTrack == null) return "audio none (the source has no audio track)";

            return "audio ours " + source.AudioTrack.CodecId + " " + Number(source.AudioTrack.SampleRate) + " Hz "
                + Number(source.AudioTrack.Channels) + " channel(s)" + aac;
        }

        string why = source.IsSilent ? "the source is flagged silent" : "the source has no audio track";
        if (backdrop.Kind == ExportBackdropKind.Video)
        {
            return "audio backdrop " + backdrop.VideoPath + aac + " (" + why + "; none if the backdrop has no sound)";
        }

        return "audio none (" + why + " and " + (backdrop.Kind == ExportBackdropKind.Image ? "an image" : "a colour")
            + " backdrop has no sound)";
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    // The dry run's view of the picture track's size, cropped to even as the export crops it.
    private readonly struct MediaTrackInfoView
    {
        public MediaTrackInfoView(ExportSource source)
        {
            Width = source.VideoTrack.Width & ~1;
            Height = source.VideoTrack.Height & ~1;
        }

        public int Width { get; }

        public int Height { get; }

        public string Size => Number(Width) + "x" + Number(Height);
    }
}
