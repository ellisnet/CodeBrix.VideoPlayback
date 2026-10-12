using System;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoProcessing.Core.Output;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// One export: a CodeBrix video file - <c>.cbv</c> (Mode2), <c>.cbvmaster</c> (Mode3) or a WebM-profile
/// <c>.webm</c> / <c>.mkv</c> (Mode1) - written out as a standard MP4 file any player plays.
/// </summary>
/// <remarks>
/// The source is decoded with the decoders the APPLICATION registered with the core's registries
/// (<c>VideoDecoders</c> for AV1 - CodeBrix.VideoPlayback.Dav1d - and, for the sound, the packet codecs the shared
/// audio output holds, asked directly so that no audio device is opened); the
/// MP4 is written by the video-processing engine the application registered, or the desktop ffmpeg engine when it
/// registered none. Captions and chapters are not carried into the MP4.
/// </remarks>
public sealed class VideoExportRequest
{
    /// <summary>The file to export: a <c>.cbv</c>, a <c>.cbvmaster</c>, or a WebM-profile <c>.webm</c> / <c>.mkv</c>.</summary>
    public string SourcePath { get; set; }

    /// <summary>Where the MP4 goes. Must end in <c>.mp4</c>; an existing file is overwritten.</summary>
    public string OutputPath { get; set; }

    /// <summary>The picture codec: <see cref="VideoExportCodec.H264" /> (the default) or the opt-in HEVC.</summary>
    public VideoExportCodec Codec { get; set; } = VideoExportCodec.H264;

    /// <summary>The picture quality - the engine's default unless set.</summary>
    public VideoQuality Quality { get; set; } = VideoQuality.EngineDefault;

    /// <summary>The largest distance between key frames, in frames; 0 (the default) leaves it to the engine.</summary>
    public int KeyframeIntervalFrames { get; set; }

    /// <summary>True (the default) to put the MP4 index in front of the data, so the file starts playing at once.</summary>
    public bool FastStart { get; set; } = true;

    /// <summary>The title tag, or null (the default) for the source's own title when it has one.</summary>
    public string Title { get; set; }

    /// <summary>True (the default) to write sound; false for a file with no audio track.</summary>
    public bool IncludeAudio { get; set; } = true;

    /// <summary>The AAC bit rate, in kbit/s, all channels together - 128 by default.</summary>
    public int AudioBitrateKilobitsPerSecond { get; set; } = 128;

    /// <summary>
    /// What a master file's transparent pixels are composited over - 50% grey unless set. Ignored, with a note,
    /// for a source without an alpha plane.
    /// </summary>
    public ExportBackdrop Backdrop { get; set; } = new ExportBackdrop();

    /// <summary>Called as the export advances, or null. There is one pass, labelled <c>export</c>.</summary>
    public Action<AuthoringProgress> ProgressCallback { get; set; }

    /// <summary>
    /// The token that stops the export between frames. A cancelled export deletes its partly written output and
    /// throws <see cref="OperationCanceledException" />.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}
