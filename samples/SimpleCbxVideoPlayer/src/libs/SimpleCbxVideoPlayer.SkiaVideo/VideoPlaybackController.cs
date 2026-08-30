using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Effects;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Rendering;
using CodeBrix.VideoPlayback.Skia;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using SkiaSharp;

namespace SimpleCbxVideoPlayer.SkiaVideo;

/// <summary>
/// The application-facing player: opens a file, drives the transport, draws into whatever canvas the host
/// hands it, and applies a chain of colour lookup tables.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the rest of the application sees. It owns the one playback session and the one Skia
/// presenter, and it exposes exactly three things a host framework has to supply: a canvas to draw into
/// (<see cref="Draw" />), a graphics context when the host has one (<see cref="SetGraphicsContext" />),
/// and a repaint when a frame arrives (<see cref="Invalidated" />). Nothing else in the application names
/// a type from the video packages.
/// </para>
/// <para>
/// THREADING. <see cref="Invalidated" /> is raised on the decoding thread - mark the view dirty there and
/// return. <see cref="Draw" />, <see cref="SaveComposedFrame" /> and <see cref="SetGraphicsContext" /> all
/// belong to the one thread that owns the canvas and the graphics context, which is the user-interface
/// thread in a CodeBrix.Platform application.
/// </para>
/// </remarks>
public sealed class VideoPlaybackController : IDisposable
{
    private readonly VideoPlaybackSession session;
    private readonly SkiaVideoPresenter presenter;
    private readonly LutChain lutChain = new();

    /// <summary>What a baked file's TITLE line starts with.</summary>
    public const string ChainTitlePrefix = "SimpleCbxVideoPlayer";

    private VideoRenderPathOption renderPath = VideoRenderPathOption.GpuAuto;
    private GRContext graphicsContext;
    private bool isDisposed;

    /// <summary>Creates the player. Nothing is opened and no device is touched until Open is called.</summary>
    /// <param name="playAudio">Whether to play the file's soundtrack. True in the application; false in a test.</param>
    public VideoPlaybackController(bool playAudio = true)
    {
        VideoPlaybackOptions options = new VideoPlaybackOptions
        {
            PlayAudio = playAudio,
            AudioSampleRate = SkiaVideoRuntime.AudioSampleRate,
            SeekMode = VideoSeekMode.Exact,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(100),
        };

        session = new VideoPlaybackSession(options);
        presenter = new SkiaVideoPresenter();
        presenter.Attach(session.Presenter);

        session.MediaOpened += (_, _) => MediaOpened?.Invoke(this, EventArgs.Empty);
        session.PlaybackEnded += (_, _) => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        session.MediaFailed += (_, args) => Report(args.Message);
        session.PositionChanged += (_, args) =>
            PositionChanged?.Invoke(this, new VideoPositionEventArgs(args.Position, args.Duration));

        presenter.Invalidated += (_, _) => Invalidated?.Invoke(this, EventArgs.Empty);
        presenter.RenderPathChanged += (_, args) => RaiseRenderPathStatus(args.Reason);
    }

    #region | Events |

    /// <summary>A new frame has arrived. Raised on the DECODING thread: mark the view dirty and return.</summary>
    public event EventHandler Invalidated;

    /// <summary>A file has been opened and its duration and tracks are known.</summary>
    public event EventHandler MediaOpened;

    /// <summary>Playback reached the end of the file - the later of the picture's end and the sound's.</summary>
    public event EventHandler PlaybackEnded;

    /// <summary>Playback has reached a new position.</summary>
    public event EventHandler<VideoPositionEventArgs> PositionChanged;

    /// <summary>Something failed, with the message to show a person word for word.</summary>
    public event EventHandler<VideoPlaybackMessageEventArgs> Failed;

    /// <summary>The render path has settled or changed, and the effect chain may have gone with it.</summary>
    public event EventHandler<VideoRenderPathStatusEventArgs> RenderPathChanged;

    #endregion

    #region | What is playing |

    /// <summary>The file that is open, or null when nothing is.</summary>
    public string CurrentFilePath { get; private set; }

    /// <summary>Whether a file is open.</summary>
    public bool IsOpen => !isDisposed && session.IsOpen;

    /// <summary>Whether the file is playing rather than paused or stopped.</summary>
    public bool IsPlaying => !isDisposed && session.IsPlaying;

    /// <summary>Where playback has reached - the audio clock, when the file carries sound.</summary>
    public TimeSpan Position => isDisposed ? TimeSpan.Zero : session.Position;

    /// <summary>How long the open file is.</summary>
    public TimeSpan Duration => isDisposed ? TimeSpan.Zero : session.Duration;

    /// <summary>The width the picture is meant to be displayed at, which is not always the coded width.</summary>
    public int DisplayWidth => isDisposed ? 0 : presenter.DisplayWidth;

    /// <summary>The height the picture is meant to be displayed at.</summary>
    public int DisplayHeight => isDisposed ? 0 : presenter.DisplayHeight;

    /// <summary>The frame number that is showing.</summary>
    public long CurrentFrameNumber => isDisposed ? 0 : presenter.CurrentFrameNumber;

    /// <summary>The timestamp of the frame that is showing.</summary>
    public TimeSpan CurrentTimestamp => isDisposed ? TimeSpan.Zero : presenter.CurrentTimestamp;

    /// <summary>Where the transport is, as far as a panel needs to know.</summary>
    /// <remarks>
    /// Opening, ended and failed all read as <see cref="VideoTransportState.Stopped" />: none of them is a
    /// picture running, and none of them is a picture parked part-way through.
    /// </remarks>
    public VideoTransportState TransportState
    {
        get
        {
            if (isDisposed) { return VideoTransportState.Stopped; }

            return session.State switch
            {
                VideoPlaybackState.Playing => VideoTransportState.Playing,
                VideoPlaybackState.Paused => VideoTransportState.Paused,
                _ => VideoTransportState.Stopped,
            };
        }
    }

    /// <summary>The last failure message, or an empty string when nothing has failed.</summary>
    public string LastError { get; private set; } = string.Empty;

    #endregion

    #region | The transport |

    /// <summary>Opens a file, closing whatever was open before it.</summary>
    /// <param name="filePath">The file to open.</param>
    /// <returns>True when the file opened; false when it did not, with the reason on <see cref="Failed" />.</returns>
    public bool Open(string filePath)
    {
        if (isDisposed) { return false; }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Report($"There is no file at '{filePath}'.");
            return false;
        }

        try
        {
            session.Close();
            session.Open(filePath);
            CurrentFilePath = filePath;
            ClearError();
            return true;
        }
        catch (Exception exception)
        {
            //Whatever went wrong, the message names it - a missing decoder package, a container that is not
            //  one of the two this family reads, a file that will not open at all.
            CurrentFilePath = null;
            Report(exception.Message);
            return false;
        }
    }

    /// <summary>Starts, or resumes, playback.</summary>
    public void Play() => Guarded(session.Play);

    /// <summary>Pauses playback, leaving the last frame on screen.</summary>
    public void Pause() => Guarded(session.Pause);

    /// <summary>Stops playback and returns to the beginning.</summary>
    public void Stop() => Guarded(session.Stop);

    /// <summary>Moves playback to a position.</summary>
    /// <param name="position">Where to move to.</param>
    public void Seek(TimeSpan position) => Guarded(() => session.Seek(position));

    /// <summary>Closes the open file.</summary>
    public void Close()
    {
        Guarded(session.Close);
        CurrentFilePath = null;
    }

    #endregion

    #region | The render path |

    /// <summary>Which render path the application is asking for. Re-applied the moment it is set.</summary>
    public VideoRenderPathOption RenderPath
    {
        get => renderPath;
        set
        {
            if (isDisposed || renderPath == value) { return; }

            renderPath = value;
            presenter.RenderPath = ToPresenterPath(value);
            ResolveRenderPath();
        }
    }

    /// <summary>Which render path is actually running.</summary>
    public VideoRenderBackendOption ActiveRenderPath =>
        isDisposed || presenter.ActiveRenderPath == VideoRenderBackend.Cpu
            ? VideoRenderBackendOption.Cpu
            : VideoRenderBackendOption.Gpu;

    /// <summary>
    /// Whether the lookup-table chain is actually being applied to the picture.
    /// </summary>
    /// <remarks>
    /// False on the processor path, deliberately: this application leaves AllowEffectsOnCpu alone, so a
    /// grade is a graphics-path feature and the user interface says so rather than quietly costing every
    /// pixel a table lookup.
    /// </remarks>
    public bool EffectsActive => !isDisposed && presenter.EffectsActive;

    /// <summary>Whether a graphics context has been supplied by the host.</summary>
    public bool HasGraphicsContext => graphicsContext != null;

    /// <summary>Supplies, or withdraws, the host's graphics context.</summary>
    /// <param name="context">The host's context, or null to go back to the processor path.</param>
    /// <remarks>
    /// The presenter does not own the context and never disposes it; the host that created it must outlive
    /// this controller, or withdraw the context first. Calling this with the context it already has costs
    /// nothing, so a paint handler may call it on every frame.
    /// </remarks>
    public void SetGraphicsContext(GRContext context)
    {
        if (isDisposed || ReferenceEquals(graphicsContext, context)) { return; }

        graphicsContext = context;
        presenter.UseGpu(context);
        ResolveRenderPath();
    }

    /// <summary>Settles the render path now, rather than at the next frame.</summary>
    /// <returns>The path that is running.</returns>
    public VideoRenderBackendOption ResolveRenderPath()
    {
        if (isDisposed) { return VideoRenderBackendOption.Cpu; }

        try
        {
            presenter.ResolveRenderPath();
            ClearError();
        }
        catch (VideoPlaybackException exception)
        {
            //GpuNoFallback on a machine with no usable graphics device. This is the whole point of that
            //  setting: it says so instead of quietly degrading, and the application shows the message.
            Report(exception.Message);
        }

        RaiseRenderPathStatus(null);
        return ActiveRenderPath;
    }

    #endregion

    #region | Effects |

    /// <summary>The chain of lookup tables as it stands.</summary>
    public IReadOnlyList<LutChainEntry> LutEntries => lutChain.Entries;

    /// <summary>Applies a chain of lookup tables, if it differs from the one already applied.</summary>
    /// <param name="entries">The tables to apply, in order; null or empty clears the chain.</param>
    /// <returns>True when the chain changed and the effects were rebuilt.</returns>
    /// <remarks>
    /// Composing a chain walks tens of thousands of grid nodes, so this does nothing at all when the
    /// selection, the order and every percentage are unchanged.
    /// </remarks>
    public bool ApplyLutChain(IReadOnlyList<LutChainEntry> entries)
    {
        if (isDisposed || !lutChain.TrySet(entries)) { return false; }

        var effects = LutEffectFactory.Build(lutChain.Entries, out var failures);

        presenter.Effects.Clear();

        foreach (IVideoFrameEffect effect in effects) { presenter.Effects.Add(effect); }

        if (failures.Count > 0) { Report(string.Join("; ", failures)); }

        RaiseRenderPathStatus(null);
        return true;
    }

    /// <summary>Names the APPLIED chain the way a baked file's TITLE should read.</summary>
    /// <returns>
    /// Something like <c>SimpleCbxVideoPlayer: sepia_33@40 + cool_33@40</c>, or a bare product name when
    /// the chain is empty.
    /// </returns>
    public string GetChainTitle() => GetChainTitle(lutChain.Entries);

    /// <summary>Names any chain the way a baked file's TITLE should read.</summary>
    /// <param name="entries">The chain to name, in the order its tables are applied.</param>
    /// <returns>
    /// Something like <c>SimpleCbxVideoPlayer: sepia_33@40 + cool_33@40</c>, or a bare product name when
    /// the chain is empty.
    /// </returns>
    public static string GetChainTitle(IReadOnlyList<LutChainEntry> entries)
    {
        if (entries == null || entries.Count == 0) { return ChainTitlePrefix; }

        var tables = entries
            .Where(entry => entry != null)
            .Select(entry => $"{Path.GetFileNameWithoutExtension(entry.FilePath)}@{entry.ApplyAtPercent:0.#}");

        return ChainTitlePrefix + ": " + string.Join(" + ", tables);
    }

    /// <summary>Composes a chain of lookup tables and writes the result as one ".cube" file.</summary>
    /// <param name="entries">The chain to bake, in the order its tables are applied.</param>
    /// <param name="cubeFilePath">Where to write the file.</param>
    /// <returns>What was written, or null when there was nothing to write.</returns>
    /// <remarks>
    /// <para>
    /// A bake is INDEPENDENT of the picture. The chain handed in here is read and composed from scratch;
    /// the presenter showing the video is not read and not touched, and NEITHER IS ANY OTHER PRESENTER.
    /// Composing a chain is arithmetic on the tables - <see cref="LutComposer" /> in the core package does
    /// it with no video, no graphics context, no frame and no window anywhere in sight. So a chain can be
    /// baked that has never been played, and a chain that is playing can be baked while the panel above it
    /// holds something else entirely: the two are simply not connected.
    /// </para>
    /// <para>
    /// The output size is pinned to <see cref="SkiaVideoPresenter.DefaultEffectLutSize" /> - the size the
    /// presenter composes at - rather than left to <c>LutComposer.GetOutputSize</c>. That is what keeps a
    /// baked file and a played chain agreeing to the last bit even though nothing connects them: same
    /// size, same tetrahedral sampling, same arithmetic.
    /// </para>
    /// <para>
    /// An application already holding <see cref="IVideoFrameEffect" />s rather than file paths wants
    /// <c>EffectComposer.Compose</c> instead, which is the same arithmetic reached from the Skia side.
    /// </para>
    /// </remarks>
    public BakedLut BakeChain(IReadOnlyList<LutChainEntry> entries, string cubeFilePath)
    {
        if (isDisposed) { return null; }

        if (string.IsNullOrWhiteSpace(cubeFilePath))
        {
            Report("A bake needs a file to write to.");
            return null;
        }

        List<LutChainEntry> chain = (entries ?? []).Where(entry => entry != null).ToList();

        if (chain.Count == 0)
        {
            Report("There is no chain to bake: no lookup table is ticked.");
            return null;
        }

        List<LutLayer> layers = [];
        List<string> failures = [];

        foreach (LutChainEntry entry in chain)
        {
            try
            {
                layers.Add(LutLayer.FromCubeFile(entry.FilePath, entry.ApplyAtPercent));
            }
            catch (Exception exception) when (exception is IOException or FormatException or InvalidDataException
                                                  or UnauthorizedAccessException or ArgumentException)
            {
                //One unreadable table is that table's problem, not the bake's.
                failures.Add($"{entry.FileName}: {exception.Message}");
            }
        }

        if (failures.Count > 0) { Report(string.Join("; ", failures)); }

        if (layers.Count == 0)
        {
            Report("None of the ticked lookup tables could be read, so there is nothing to bake.");
            return null;
        }

        try
        {
            Lut3D table = LutComposer.Compose(
                layers,
                new LutComposerOptions { OutputSize = SkiaVideoPresenter.DefaultEffectLutSize });

            if (table == null)
            {
                Report("The ticked chain composed to nothing, so there is no table to bake.");
                return null;
            }

            var folder = Path.GetDirectoryName(cubeFilePath);

            if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }

            var title = GetChainTitle(chain);
            CubeLutFile.Write(table, cubeFilePath, title);

            return new BakedLut(cubeFilePath, title, table.Size, chain.Count);
        }
        catch (Exception exception)
        {
            //A bake that cannot be written is a message in the status line, never a crash mid-playback.
            Report(exception.Message);
            return null;
        }
    }

    #endregion

    #region | Drawing |

    /// <summary>Draws the newest frame into a canvas, letterboxed inside a rectangle.</summary>
    /// <param name="canvas">The canvas the host's view handed over.</param>
    /// <param name="destination">The rectangle the video should occupy.</param>
    /// <remarks>
    /// The picture keeps its DISPLAY aspect ratio, so a portrait recording is drawn portrait inside a
    /// landscape window with black bars either side of it.
    /// </remarks>
    public void Draw(SKCanvas canvas, SKRect destination)
    {
        if (isDisposed || canvas == null) { return; }

        try
        {
            presenter.Draw(canvas, destination, VideoStretch.Uniform);
            ClearError();
        }
        catch (VideoPlaybackException exception)
        {
            Report(exception.Message);
        }
    }

    /// <summary>Writes the composed picture to a PNG file and measures what was written.</summary>
    /// <param name="pngFilePath">Where to write the picture.</param>
    /// <returns>What was written, or null when nothing has been composed yet.</returns>
    /// <remarks>
    /// Call this on the thread that draws, with the graphics context current - inside the paint handler,
    /// in other words. What is captured is the COMPOSED picture, with the effect chain and any overlay
    /// applied, and it is readable on the processor whichever path composed it.
    /// </remarks>
    public ComposedFrameSnapshot SaveComposedFrame(string pngFilePath)
    {
        if (isDisposed || string.IsNullOrWhiteSpace(pngFilePath)) { return null; }

        using SKImage composed = presenter.CaptureComposedFrame();

        if (composed == null) { return null; }

        using SKData png = composed.Encode(SKEncodedImageFormat.Png, 100);

        if (png == null) { return null; }

        var bytes = png.ToArray();
        var folder = Path.GetDirectoryName(pngFilePath);

        if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }

        File.WriteAllBytes(pngFilePath, bytes);

        MeasurePicture(composed, out var nonBlackPercent, out var meanLuminance);

        return new ComposedFrameSnapshot(
            pngFilePath,
            composed.Width,
            composed.Height,
            nonBlackPercent,
            meanLuminance,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            presenter.CurrentFrameNumber,
            presenter.CurrentTimestamp);
    }

    #endregion

    #region | Implementation |

    private static VideoRenderPath ToPresenterPath(VideoRenderPathOption option) => option switch
    {
        VideoRenderPathOption.Cpu => VideoRenderPath.Cpu,
        VideoRenderPathOption.GpuNoFallback => VideoRenderPath.GpuNoFallback,
        _ => VideoRenderPath.GpuAuto,
    };

    private static void MeasurePicture(SKImage image, out double nonBlackPercent, out double meanLuminance)
    {
        nonBlackPercent = 0;
        meanLuminance = 0;

        using SKBitmap bitmap = SKBitmap.FromImage(image);

        if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0) { return; }

        //Sampling every fourth pixel in each direction is plenty to tell a picture from a black rectangle,
        //  and keeps a 4K capture to a few hundred thousand reads.
        const int step = 4;
        double total = 0;
        var counted = 0;
        var nonBlack = 0;

        for (var y = 0; y < bitmap.Height; y += step)
        {
            for (var x = 0; x < bitmap.Width; x += step)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                double luminance = (0.2126 * pixel.Red) + (0.7152 * pixel.Green) + (0.0722 * pixel.Blue);

                total += luminance;
                counted++;

                if (pixel.Red > 8 || pixel.Green > 8 || pixel.Blue > 8) { nonBlack++; }
            }
        }

        if (counted == 0) { return; }

        meanLuminance = total / counted;
        nonBlackPercent = nonBlack * 100.0 / counted;
    }

    private void Guarded(Action action)
    {
        if (isDisposed) { return; }

        try
        {
            action();
            ClearError();
        }
        catch (Exception exception)
        {
            Report(exception.Message);
        }
    }

    private void RaiseRenderPathStatus(string reason) =>
        RenderPathChanged?.Invoke(
            this,
            new VideoRenderPathStatusEventArgs(ActiveRenderPath, EffectsActive, reason));

    private void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return; }

        //A failed draw fails on every paint; showing the same sentence sixty times a second helps nobody.
        if (string.Equals(message, LastError, StringComparison.Ordinal)) { return; }

        LastError = message;
        Failed?.Invoke(this, new VideoPlaybackMessageEventArgs(message));
    }

    private void ClearError() => LastError = string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        if (isDisposed) { return; }

        isDisposed = true;

        //The presenter goes first: it is the one holding surfaces on a context this object does not own.
        presenter.Dispose();
        session.Dispose();
    }

    #endregion
}
