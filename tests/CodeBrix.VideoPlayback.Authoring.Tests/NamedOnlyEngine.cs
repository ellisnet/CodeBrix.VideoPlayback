using System;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Core.Output;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// An engine that is NOT the ffmpeg one and can do nothing but say its name - enough to test what the master
/// flavour decides from which engine it has, without registering anything process-wide (the registry has no
/// way to take an engine back out, so a registered stand-in would leak into every other test).
/// </summary>
internal sealed class NamedOnlyEngine : IVideoProcessor
{
    /// <summary>Creates the engine.</summary>
    /// <param name="name">What it calls itself.</param>
    public NamedOnlyEngine(string name)
    {
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public VideoProcessorCapabilities Capabilities => VideoProcessorCapabilities.AlphaFrameSource;

    /// <inheritdoc />
    public MediaInfo Analyse(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<MediaInfo> AnalyseAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public VideoFrameData Snapshot(string path, TimeSpan at, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public IVideoFrameSource OpenVideo(string path, VideoSourceOptions options = null) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public IAudioSampleSource OpenAudio(string path, AudioSourceOptions options = null) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public IStandardVideoWriter CreateWriter(StandardVideoRequest request, IProgress<VideoProgress> progress = null) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public void TranscodeFile(
        TranscodeRequest request,
        IProgress<VideoProgress> progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public void TrimFile(
        TrimRequest request,
        IProgress<VideoProgress> progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public void ConcatenateFiles(
        ConcatenateRequest request,
        IProgress<VideoProgress> progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
