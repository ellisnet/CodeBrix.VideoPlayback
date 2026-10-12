using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Core.Output;
using CodeBrix.VideoProcessing.Core.Tools;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// An engine that is NOT the ffmpeg one and DECODES: it serves a <see cref="SyntheticClip" /> from memory,
/// whatever path it is handed, and a sine tone as its sound - enough for the in-process passes to run end to end
/// without FFmpeg, the way they run on Android.
/// </summary>
/// <remarks>
/// <para>
/// It implements the frame and sample sources and the analysis only; everything else throws. It is never
/// registered process-wide (the registry cannot take an engine back out) - a test hands it to the library
/// through <c>InProcessAuthoring.EngineOverride</c> for its own flow only.
/// </para>
/// <para>
/// Given a <see cref="FakeExternalTools" />, it also reports <see cref="VideoProcessorCapabilities.ExternalTools" />
/// and hands those tools out as its external-tools facet - so the library's ffmpeg path (the decision, the
/// progress parse, the AV1 fallback rewrite, the tool-check messages, the recorded command line) can be tested
/// with no ffmpeg anywhere.
/// </para>
/// </remarks>
internal sealed class SyntheticEngine : IVideoProcessor, IExternalToolHost
{
    /// <summary>The sound's sample rate.</summary>
    public const int SoundSampleRate = 48000;

    /// <summary>Creates the engine.</summary>
    /// <param name="clip">The clip it serves.</param>
    /// <param name="name">What it calls itself.</param>
    /// <param name="capabilities">What it says it can do; alpha and 10-bit frames by default.</param>
    /// <param name="hasSound">Whether the clip has a sound stream.</param>
    /// <param name="tools">
    /// Fake external tools to report and hand out (adding <see cref="VideoProcessorCapabilities.ExternalTools" />),
    /// or null - the default - for an engine without them, like Android's.
    /// </param>
    public SyntheticEngine(
        SyntheticClip clip,
        string name = "synthetic",
        VideoProcessorCapabilities capabilities =
            VideoProcessorCapabilities.AlphaFrameSource | VideoProcessorCapabilities.HighBitDepthFrameSource,
        bool hasSound = true,
        FakeExternalTools tools = null)
    {
        Clip = clip;
        Name = name;
        Capabilities = tools != null ? capabilities | VideoProcessorCapabilities.ExternalTools : capabilities;
        HasSound = hasSound;
        FakeTools = tools;
    }

    /// <summary>The fake tools, or null.</summary>
    public FakeExternalTools FakeTools { get; }

    /// <inheritdoc />
    public IExternalTools Tools => FakeTools;

    /// <summary>The subtitle streams <see cref="Analyse" /> reports; none by default.</summary>
    public IReadOnlyList<SubtitleStreamInfo> SubtitleStreams { get; set; } = Array.Empty<SubtitleStreamInfo>();

    /// <summary>The chapters <see cref="Analyse" /> reports; none by default.</summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; set; } = Array.Empty<ChapterInfo>();

    /// <summary>How many times <see cref="Analyse" /> was called.</summary>
    public int Analyses { get; private set; }

    /// <summary>The clip served.</summary>
    public SyntheticClip Clip { get; }

    /// <summary>Whether the clip has a sound stream.</summary>
    public bool HasSound { get; }

    /// <summary>
    /// The matrix its frame sources REPORT for the I420 samples they deliver - BT.709 (what they are) by default;
    /// a test sets <see cref="VideoColorMatrix.Rgb" /> to stand in for an engine that misdescribes its delivery.
    /// </summary>
    public VideoColorMatrix DeliveredColorMatrix { get; set; } = VideoColorMatrix.Bt709;

    /// <summary>How many frame sources were opened - the colour pass and the alpha pass each open one.</summary>
    public int VideoOpens { get; private set; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public VideoProcessorCapabilities Capabilities { get; }

    /// <inheritdoc />
    public MediaInfo Analyse(string path, CancellationToken cancellationToken = default)
    {
        Analyses++;
        VideoStreamInfo video = new VideoStreamInfo
        {
            Index = 0,
            CodecName = "synthetic",
            Width = Clip.Width,
            Height = Clip.Height,
            FrameRate = Clip.Rate,
            FrameCount = Clip.FrameCount,
            Duration = Clip.Duration,
            BitDepth = Clip.BitDepth,
            PixelFormatName = (Clip.Alpha ? "yuva420p" : "yuv420p") + (Clip.BitDepth > 8 ? "10le" : string.Empty),
            ChromaSubsampling = VideoChromaSubsampling.Yuv420,
            HasAlpha = Clip.Alpha,
            RotationDegrees = Clip.RotationDegrees,
            ColorRange = VideoColorRange.Limited,
            ColorMatrix = VideoColorMatrix.Bt709,
            ColorPrimaries = VideoColorPrimaries.Bt709,
            ColorTransfer = VideoColorTransfer.Bt709,
        };

        AudioStreamInfo[] audio = HasSound
            ? new[]
            {
                new AudioStreamInfo
                {
                    Index = 1,
                    CodecName = "sine",
                    SampleRate = SoundSampleRate,
                    Channels = 2,
                    Duration = Clip.Duration,
                },
            }
            : Array.Empty<AudioStreamInfo>();

        return new MediaInfo
        {
            ContainerFormat = "synthetic",
            Duration = Clip.Duration,
            VideoStreams = new[] { video },
            AudioStreams = audio,
            SubtitleStreams = SubtitleStreams,
            Chapters = Chapters,
        };
    }

    /// <inheritdoc />
    public Task<MediaInfo> AnalyseAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Analyse(path, cancellationToken));

    /// <inheritdoc />
    public VideoFrameData Snapshot(string path, TimeSpan at, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public IVideoFrameSource OpenVideo(string path, VideoSourceOptions options = null)
    {
        options ??= new VideoSourceOptions();
        options.Validate();
        if (options.PixelLayout != VideoPixelLayout.I420) throw new NotSupportedException("I420 only.");
        if (options.BitDepth > 8 && (Capabilities & VideoProcessorCapabilities.HighBitDepthFrameSource) == 0)
        {
            throw new NotSupportedException("No high-bit-depth frames.");
        }

        VideoOpens++;
        return new FrameSource(Clip, options.BitDepth, options.IncludeAlpha && Clip.Alpha, DeliveredColorMatrix);
    }

    /// <inheritdoc />
    public IAudioSampleSource OpenAudio(string path, AudioSourceOptions options = null)
    {
        if (!HasSound) throw new InvalidOperationException("The synthetic clip has no sound.");

        options ??= new AudioSourceOptions();
        int rate = options.SampleRate > 0 ? options.SampleRate : SoundSampleRate;
        int channels = options.Channels > 0 ? options.Channels : 2;
        return new SineSource(rate, channels, Clip.Duration);
    }

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

    private sealed class FrameSource : IVideoFrameSource
    {
        private readonly SyntheticClip clip;
        private readonly int depth;
        private readonly bool alpha;
        private int next;

        public FrameSource(SyntheticClip clip, int depth, bool alpha, VideoColorMatrix matrix)
        {
            this.clip = clip;
            this.depth = depth;
            this.alpha = alpha;
            Info = new VideoSourceInfo
            {
                Width = clip.Width,
                Height = clip.Height,
                FrameRate = clip.Rate,
                Duration = clip.Duration,
                EstimatedFrameCount = clip.FrameCount,
                PixelLayout = VideoPixelLayout.I420,
                BitDepth = depth,
                SourceBitDepth = clip.BitDepth,
                SourceHasAlpha = clip.Alpha,
                HasAlpha = alpha,
                RotationDegrees = clip.RotationDegrees,
                ColorRange = VideoColorRange.Limited,
                ColorMatrix = matrix,
            };
        }

        public VideoSourceInfo Info { get; }

        public bool CanSeek => false;

        public bool TryReadFrame(out VideoFrameData frame)
        {
            if (next >= clip.FrameCount)
            {
                frame = default;
                return false;
            }

            frame = clip.Frame(next++, depth, alpha);
            return true;
        }

        public void Seek(TimeSpan position) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class SineSource : IAudioSampleSource
    {
        private readonly long totalFrames;
        private long position;

        public SineSource(int rate, int channels, TimeSpan duration)
        {
            totalFrames = (long)(duration.TotalSeconds * rate);
            Info = new AudioSourceInfo { SampleRate = rate, Channels = channels, Duration = duration, SourceCodecName = "sine" };
        }

        public AudioSourceInfo Info { get; }

        public bool CanSeek => false;

        public int Read(Span<float> interleaved)
        {
            int channels = Info.Channels;
            int frames = (int)Math.Min(interleaved.Length / channels, totalFrames - position);
            for (int f = 0; f < frames; f++)
            {
                float sample = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (position + f) / Info.SampleRate));
                for (int c = 0; c < channels; c++) interleaved[(f * channels) + c] = sample;
            }

            position += frames;
            return frames;
        }

        public void Seek(TimeSpan position) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
