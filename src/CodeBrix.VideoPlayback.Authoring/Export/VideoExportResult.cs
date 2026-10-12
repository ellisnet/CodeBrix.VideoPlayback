using System;
using System.Collections.Generic;
using System.Globalization;
using CodeBrix.VideoProcessing.Core.Media;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>What one export produced, and anything it wants the caller to know.</summary>
public sealed class VideoExportResult
{
    private readonly List<string> notes;

    /// <summary>Creates a result.</summary>
    /// <param name="outputPath">Where the MP4 was written.</param>
    /// <param name="engineName">The name of the engine that wrote it.</param>
    /// <param name="framesWritten">How many pictures the file holds.</param>
    /// <param name="duration">How long the picture lasts.</param>
    /// <param name="audioSource">Where the sound came from.</param>
    /// <param name="runNotes">Anything the export wants the caller to know.</param>
    /// <param name="codecUsed">The picture codec written.</param>
    /// <param name="width">The picture width.</param>
    /// <param name="height">The picture height.</param>
    /// <param name="frameRate">The constant frame rate written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runNotes" /> is null.</exception>
    public VideoExportResult(
        string outputPath,
        string engineName,
        long framesWritten,
        TimeSpan duration,
        VideoExportAudioSource audioSource,
        IReadOnlyList<string> runNotes,
        VideoExportCodec codecUsed,
        int width,
        int height,
        FrameRate frameRate)
    {
        if (runNotes == null) throw new ArgumentNullException(nameof(runNotes));

        OutputPath = outputPath;
        EngineName = engineName;
        FramesWritten = framesWritten;
        Duration = duration;
        AudioSource = audioSource;
        notes = new List<string>(runNotes);
        CodecUsed = codecUsed;
        Width = width;
        Height = height;
        FrameRate = frameRate;
    }

    /// <summary>Where the MP4 was written.</summary>
    public string OutputPath { get; }

    /// <summary>The name of the video-processing engine that wrote it - <c>ffmpeg</c> on the desktop.</summary>
    public string EngineName { get; }

    /// <summary>How many pictures the file holds, a backdrop video's tail included.</summary>
    public long FramesWritten { get; }

    /// <summary>How long the picture lasts: <see cref="FramesWritten" /> frames at <see cref="FrameRate" />.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Where the sound came from - the source's, the backdrop video's, or none at all.</summary>
    public VideoExportAudioSource AudioSource { get; }

    /// <summary>Anything the export wants the caller to know that is not a failure; empty when there was nothing to say.</summary>
    public IReadOnlyList<string> Notes => notes;

    /// <summary>The picture codec written.</summary>
    public VideoExportCodec CodecUsed { get; }

    /// <summary>The picture width, in pixels - the source's (cropped to even when it was odd).</summary>
    public int Width { get; }

    /// <summary>The picture height, in pixels - the source's (cropped to even when it was odd).</summary>
    public int Height { get; }

    /// <summary>The constant frame rate the file was written at - the source's nominal rate.</summary>
    public FrameRate FrameRate { get; }

    /// <inheritdoc />
    public override string ToString() =>
        OutputPath + ": " + CodecUsed + " " + Width.ToString(CultureInfo.InvariantCulture) + "x"
        + Height.ToString(CultureInfo.InvariantCulture) + " " + FrameRate + " fps, "
        + FramesWritten.ToString(CultureInfo.InvariantCulture) + " frame(s), audio " + AudioSource
        + (notes.Count == 0 ? string.Empty : ", " + notes.Count.ToString(CultureInfo.InvariantCulture) + " note(s)");
}
