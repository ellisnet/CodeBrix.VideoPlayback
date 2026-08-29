using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Presentation;
using CodeBrix.VideoPlayback.Skia.Composition;
using CodeBrix.VideoPlayback.Skia.Effects;
using CodeBrix.VideoPlayback.Skia.Internal;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.Skia;

/// <summary>
/// Draws decoded video through SkiaSharp: it takes the newest frame, composes it on an off-screen surface -
/// on the graphics device or on the processor - lets the application draw over it, and blits the result into
/// whatever canvas the application owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of it.</b> The presenter never draws to a window. It owns an off-screen
/// <see cref="SKSurface" />, draws the video into it as a base layer, runs <see cref="Layers" /> and the
/// <see cref="Composing" /> event over the top, and then <see cref="Draw" /> blits that surface into the
/// application's canvas with the letterboxing the <see cref="VideoStretch" /> asks for. That indirection is
/// the whole point: an off-screen surface is a canvas anybody can draw on, which is what makes subtitles,
/// heads-up overlays, annotation and picture-over-picture possible without this class knowing about any of
/// them. <see cref="CaptureComposedFrame" /> hands back what was composed, which is a screenshot - and, one
/// day, a recording.
/// </para>
/// <para>
/// <b>Two render paths, both first class.</b> On the graphics path the three planes are uploaded as
/// single-channel textures and ONE shader does the colour conversion and the whole effect chain in a single
/// pass, at full sample precision. On the processor path the core's vector converter turns the frame into
/// BGRA pixels straight into the composition surface's own memory, with no copy at all. Neither is a
/// degraded version of the other: the processor path is the right answer on a machine with no usable
/// graphics device, and it is a tested, benchmarked configuration.
/// <see cref="RenderPath" /> says which one you want and what to do if it cannot be had;
/// <see cref="ActiveRenderPath" /> says which one is running.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Present" /> and <see cref="Attach" /> may be called from any thread.
/// Everything that touches the surface - <see cref="Update" />, <see cref="Draw" />,
/// <see cref="CurrentImage" />, <see cref="CaptureComposedFrame" /> - must be called from ONE thread, the
/// thread that owns the graphics context. <see cref="Invalidated" /> is raised on whatever thread posted the
/// frame, which is the decode thread: mark the view dirty there and draw later.
/// </para>
/// <para>
/// <b>What it allocates.</b> Nothing per frame on the processor path once it is warm, and there is a test
/// that fails if that stops being true. The graphics path allocates a handful of small wrapper objects per
/// frame, because SkiaSharp offers no way to refill an existing texture from host memory; the pixels
/// themselves are never copied on either path.
/// </para>
/// </remarks>
public sealed class SkiaVideoPresenter : IDisposable
{
    /// <summary>The default number of nodes along each axis of the composed effect lookup table.</summary>
    /// <remarks>
    /// Thirty-three is the size the ".cube" convention settled on, and is enough for any smooth colour
    /// change: 35,937 nodes, a 1089-by-33 atlas, 143 kilobytes of texture.
    /// </remarks>
    public const int DefaultEffectLutSize = 33;

    private static readonly SKSamplingOptions BlitSampling =
        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

    private static int cpuEffectWarningIssued;

    private readonly VideoFramePresenter ownMailbox = new VideoFramePresenter();
    private readonly ObservableCollection<IVideoFrameEffect> effects = new ObservableCollection<IVideoFrameEffect>();
    private readonly ObservableCollection<IVideoLayer> layers = new ObservableCollection<IVideoLayer>();
    private readonly BgraFrameBufferPool bgraPool = new BgraFrameBufferPool();
    private readonly YuvSurfaceRenderer renderer = new YuvSurfaceRenderer();

    private VideoFramePresenter source;
    private GRContext grContext;
    private VideoRenderPath renderPath = VideoRenderPath.GpuAuto;
    private VideoRenderBackend activeBackend = VideoRenderBackend.Cpu;
    private bool backendResolved;
    private bool fallbackReported;
    private bool allowEffectsOnCpu;
    private bool effectsChainDirty = true;
    private bool effectsActive;
    private int effectLutSize = DefaultEffectLutSize;

    private LutInterpolation effectInterpolation = LutInterpolation.Tetrahedral;

    private EffectComposer composer;
    private Lut3D resultantLut;
    private SKBitmap lookupAtlas;
    private SKImage lookupAtlasImage;

    private SKSurface surface;
    private BgraFrameBuffer cpuSurfaceBuffer;
    private VideoRenderBackend surfaceBackend;
    private int surfaceWidth;
    private int surfaceHeight;
    private SKImage cachedImage;

    private bool hasComposition;
    private int displayWidth;
    private int displayHeight;
    private TimeSpan lastTimestamp;
    private long lastFrameNumber = -1;

    private long framesComposed;
    private long framesDrawn;
    private long surfaceAllocations;
    private long effectCompositions;

    private bool disposed;

    /// <summary>Creates a presenter with no graphics context, which starts on the processor path.</summary>
    /// <remarks>
    /// Hand it a context later with <see cref="UseGpu" /> and it moves to the graphics path at the next
    /// frame.
    /// </remarks>
    public SkiaVideoPresenter()
    {
        source = ownMailbox;
        source.Invalidated += OnSourceInvalidated;
        effects.CollectionChanged += OnEffectsChanged;
    }

    /// <summary>Creates a presenter that draws on a graphics context the application owns.</summary>
    /// <param name="graphicsContext">
    /// The context to render on. The presenter does NOT take ownership of it and never disposes it; it must
    /// outlive the presenter and must be current on the thread that draws.
    /// </param>
    public SkiaVideoPresenter(GRContext graphicsContext)
        : this()
    {
        grContext = graphicsContext;
    }

    /// <summary>
    /// Raised when a new frame has arrived and the view should repaint.
    /// </summary>
    /// <remarks>
    /// It is raised on the thread that posted the frame - the decode thread - so a handler must do the least
    /// possible: mark the view dirty, ask for a repaint, and return. Drawing belongs on the drawing thread,
    /// which then calls <see cref="Draw" />.
    /// </remarks>
    public event EventHandler Invalidated;

    /// <summary>Raised when the presenter settles on a render path, and again whenever it changes.</summary>
    public event EventHandler<VideoRenderPathChangedEventArgs> RenderPathChanged;

    /// <summary>
    /// Raised after the video and every registered layer have been drawn on the composition surface, and
    /// before it is blitted - the ad-hoc alternative to writing a <see cref="IVideoLayer" />.
    /// </summary>
    /// <remarks>
    /// Raised on the drawing thread, inside the composition. The canvas is valid only for the duration of
    /// the call.
    /// </remarks>
    public event EventHandler<VideoComposingEventArgs> Composing;

    /// <summary>
    /// The mailbox this presenter takes frames from - either one it was <see cref="Attach">attached</see> to
    /// or its own.
    /// </summary>
    public VideoFramePresenter Source => source;

    /// <summary>True when the presenter is reading a mailbox somebody else owns.</summary>
    public bool IsAttached => !ReferenceEquals(source, ownMailbox);

    /// <summary>The graphics context the presenter renders on, or null when it has none.</summary>
    public GRContext GraphicsContext => grContext;

    /// <summary>Which render path to use, and what to do when the graphics one is unavailable.</summary>
    /// <remarks>
    /// Changing this takes effect at the next composition. The change is announced through
    /// <see cref="RenderPathChanged" /> once it has actually happened.
    /// </remarks>
    public VideoRenderPath RenderPath
    {
        get => renderPath;
        set
        {
            if (renderPath == value) return;
            renderPath = value;
            fallbackReported = false;
            effectsChainDirty = true;
        }
    }

    /// <summary>Which render path is actually running.</summary>
    /// <remarks>
    /// Until the first composition this reports what the presenter WOULD choose, without committing to it and
    /// without raising <see cref="RenderPathChanged" />.
    /// </remarks>
    public VideoRenderBackend ActiveRenderPath => backendResolved ? activeBackend : PeekBackend();

    /// <summary>True when the configured <see cref="Effects" /> are actually being applied.</summary>
    /// <remarks>
    /// False with a non-empty chain means the presenter is on the processor path and
    /// <see cref="AllowEffectsOnCpu" /> is not set, so the effects are being silently ignored - which is the
    /// documented behaviour, not a fault.
    /// </remarks>
    public bool EffectsActive =>
        !disposed && effects.Count > 0 && (ActiveRenderPath == VideoRenderBackend.Gpu || allowEffectsOnCpu);

    /// <summary>
    /// Whether the effect chain should be applied on the processor when the graphics path is unavailable.
    /// False by default.
    /// </summary>
    /// <remarks>
    /// A per-pixel lookup on every frame is roughly as expensive as the colour conversion itself, so turning
    /// this on can halve the frame rate the processor path sustains. It is off by default so that a graphics
    /// fallback degrades in speed by nothing at all; turn it on when the effect chain is the point of the
    /// picture rather than an enhancement of it. The first frame that takes this road is written once per
    /// process to <see cref="Trace" /> as a warning.
    /// </remarks>
    public bool AllowEffectsOnCpu
    {
        get => allowEffectsOnCpu;
        set
        {
            if (allowEffectsOnCpu == value) return;
            allowEffectsOnCpu = value;
            effectsChainDirty = true;
        }
    }

    /// <summary>
    /// The colour effect chain, applied in list order and composed into ONE resultant lookup table.
    /// </summary>
    /// <remarks>
    /// Editing the list marks the chain for recomposition, which happens at the next composition and not per
    /// frame. Order matters: effect two sees the colours effect one produced.
    /// </remarks>
    public ObservableCollection<IVideoFrameEffect> Effects => effects;

    /// <summary>
    /// The overlay layers, drawn in list order on top of the video and beneath the
    /// <see cref="Composing" /> event.
    /// </summary>
    public ObservableCollection<IVideoLayer> Layers => layers;

    /// <summary>The number of nodes along each axis of the composed effect lookup table.</summary>
    /// <remarks>
    /// Larger grids follow a chain containing a hard step more faithfully and cost more texture memory -
    /// the atlas is <c>size * size</c> by <c>size</c> pixels. Changing this recomposes the chain.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is below <see cref="Lut3D.MinimumSize" /> or above <see cref="Lut3D.MaximumSize" />.
    /// </exception>
    public int EffectLutSize
    {
        get => effectLutSize;
        set
        {
            if (value < Lut3D.MinimumSize || value > Lut3D.MaximumSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"The effect lookup grid has between {Lut3D.MinimumSize} and {Lut3D.MaximumSize} nodes a side.");
            }

            if (effectLutSize == value) return;
            effectLutSize = value;
            effectsChainDirty = true;
        }
    }

    /// <summary>
    /// How a colour that falls BETWEEN the nodes of a lookup table is worked out, everywhere in this
    /// presenter's effect chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It governs three things at once, on purpose, so that one setting means one thing: how each effect's
    /// own table is sampled while the chain is folded, how the shader reads the resultant table on the
    /// graphics path, and how <see cref="AllowEffectsOnCpu" /> reads it on the processor path. Both render
    /// paths therefore always agree with each other.
    /// </para>
    /// <para>
    /// <see cref="LutInterpolation.Tetrahedral" /> is the default. It is what colour-grading tools and
    /// FFmpeg's <c>lut3d</c> filter do, so a grade shown here and the same grade baked to a ".cube" file for
    /// an encoding pipeline agree to about one level in 255. It holds the neutral axis exactly - a grey the
    /// table leaves grey stays grey - and costs four texture fetches a pixel.
    /// </para>
    /// <para>
    /// <see cref="LutInterpolation.Trilinear" /> is what a graphics card's own texture filter does and costs
    /// two fetches a pixel instead of four. On a smooth grade the two are within a level of each other;
    /// choose it when the per-pixel cost matters more than agreeing with a grading tool.
    /// </para>
    /// <para>Changing this recomposes the chain and repaints.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the two.</exception>
    public LutInterpolation EffectInterpolation
    {
        get => effectInterpolation;
        set
        {
            if (value != LutInterpolation.Tetrahedral && value != LutInterpolation.Trilinear)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A lookup table is read tetrahedrally or trilinearly.");
            }

            if (effectInterpolation == value) return;
            effectInterpolation = value;
            effectsChainDirty = true;
        }
    }

    /// <summary>True once a frame has been composed and there is something to draw.</summary>
    public bool HasComposedFrame => hasComposition;

    /// <summary>The coded width of the frame most recently composed, or zero before the first one.</summary>
    public int ComposedWidth => surfaceWidth;

    /// <summary>The coded height of the frame most recently composed, or zero before the first one.</summary>
    public int ComposedHeight => surfaceHeight;

    /// <summary>The width the composed frame should be SHOWN at, once its pixel aspect ratio is applied.</summary>
    public int DisplayWidth => displayWidth;

    /// <summary>The height the composed frame should be SHOWN at, once its pixel aspect ratio is applied.</summary>
    public int DisplayHeight => displayHeight;

    /// <summary>The timestamp of the frame most recently composed.</summary>
    public TimeSpan CurrentTimestamp => lastTimestamp;

    /// <summary>The number of the frame most recently composed, or -1 before the first one.</summary>
    public long CurrentFrameNumber => lastFrameNumber;

    /// <summary>
    /// The composed picture as an image, for an application that would rather composite it itself than let
    /// <see cref="Draw" /> blit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The presenter OWNS the returned image and replaces it at the next composition, so use it and let it
    /// go - never hold it across a frame and never dispose it. An image you may keep comes from
    /// <see cref="CaptureComposedFrame" />.
    /// </para>
    /// <para>
    /// Reading this allocates an image the first time after each composition, so a caller that reads it every
    /// frame gives up the "nothing per frame" guarantee that <see cref="Draw" /> keeps.
    /// </para>
    /// <para>Null before the first frame has been composed.</para>
    /// </remarks>
    public SKImage CurrentImage
    {
        get
        {
            if (disposed || !hasComposition || surface == null) return null;
            return cachedImage ??= surface.Snapshot();
        }
    }

    /// <summary>Works out where a picture goes inside a rectangle.</summary>
    /// <param name="destination">The rectangle to fit the picture into.</param>
    /// <param name="contentWidth">The picture's display width.</param>
    /// <param name="contentHeight">The picture's display height.</param>
    /// <param name="stretch">How to fit it.</param>
    /// <returns>
    /// The rectangle the picture should be drawn into. For
    /// <see cref="VideoStretch.UniformToFill" /> and <see cref="VideoStretch.None" /> it can be larger than
    /// <paramref name="destination" />, and the caller is expected to clip.
    /// </returns>
    /// <remarks>
    /// A pure function, exposed because it is the geometry a host view needs in order to answer "where on
    /// screen is this video pixel" - for a click, a caption position, or a hit test.
    /// </remarks>
    public static SKRect ComputeDestinationRect(
        SKRect destination,
        int contentWidth,
        int contentHeight,
        VideoStretch stretch)
    {
        if (contentWidth <= 0 || contentHeight <= 0) return destination;
        if (stretch == VideoStretch.Fill) return destination;

        float scale;
        switch (stretch)
        {
            case VideoStretch.None:
                scale = 1f;
                break;

            case VideoStretch.UniformToFill:
                scale = Math.Max(destination.Width / contentWidth, destination.Height / contentHeight);
                break;

            default:
                scale = Math.Min(destination.Width / contentWidth, destination.Height / contentHeight);
                break;
        }

        float width = contentWidth * scale;
        float height = contentHeight * scale;
        float left = destination.Left + ((destination.Width - width) / 2f);
        float top = destination.Top + ((destination.Height - height) / 2f);

        return SKRect.Create(left, top, width, height);
    }

    /// <summary>Reads frames from somebody else's mailbox - a playback session's, normally.</summary>
    /// <param name="presenter">
    /// The mailbox to read. The presenter does NOT take ownership of it; the session that made it disposes
    /// it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="presenter" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <example>
    /// <code>
    /// VideoPlaybackSession session = new VideoPlaybackSession();
    /// SkiaVideoPresenter view = new SkiaVideoPresenter();
    /// view.Attach(session.Presenter);
    /// </code>
    /// </example>
    public void Attach(VideoFramePresenter presenter)
    {
        if (presenter == null) throw new ArgumentNullException(nameof(presenter));
        ThrowIfDisposed();

        if (ReferenceEquals(presenter, source)) return;

        source.Invalidated -= OnSourceInvalidated;
        if (ReferenceEquals(source, ownMailbox)) ownMailbox.Clear();

        source = presenter;
        source.Invalidated += OnSourceInvalidated;
    }

    /// <summary>Stops reading somebody else's mailbox and goes back to this presenter's own.</summary>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    public void Detach()
    {
        ThrowIfDisposed();
        if (!IsAttached) return;

        source.Invalidated -= OnSourceInvalidated;
        source = ownMailbox;
        source.Invalidated += OnSourceInvalidated;
    }

    /// <summary>Hands a frame straight to the presenter, without a playback session in between.</summary>
    /// <param name="frame">
    /// The frame to show. The presenter takes its own reference; the caller keeps and disposes its own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// The frame goes into whichever mailbox the presenter is reading, so it replaces anything waiting there
    /// - the newest frame always wins, exactly as it does during playback.
    /// </remarks>
    public void Present(VideoFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        ThrowIfDisposed();
        source.Post(frame);
    }

    /// <summary>Moves the presenter onto a graphics context.</summary>
    /// <param name="graphicsContext">
    /// The context to render on, or null to go back to the processor path. The presenter does NOT take
    /// ownership: it never disposes the context, which must outlive it and must be current on the drawing
    /// thread.
    /// </param>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// The change takes effect at the next composition, which allocates a new composition surface on the new
    /// backend.
    /// </remarks>
    public void UseGpu(GRContext graphicsContext)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(grContext, graphicsContext)) return;

        grContext = graphicsContext;
        fallbackReported = false;
        effectsChainDirty = true;
    }

    /// <summary>
    /// Settles which render path will be used, raising <see cref="RenderPathChanged" /> if that is news.
    /// </summary>
    /// <returns>The path that will run.</returns>
    /// <exception cref="VideoPlaybackException">
    /// <see cref="RenderPath" /> is <see cref="VideoRenderPath.GpuNoFallback" /> and there is no usable
    /// graphics context.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// <see cref="Update" /> and <see cref="Draw" /> call this for you. Call it yourself at start-up to find
    /// out - or to be told - what you are going to get, before the first frame arrives.
    /// </remarks>
    public VideoRenderBackend ResolveRenderPath()
    {
        ThrowIfDisposed();

        VideoRenderBackend wanted;
        string reason;

        if (renderPath == VideoRenderPath.Cpu)
        {
            wanted = VideoRenderBackend.Cpu;
            reason = "the render path is set to Cpu.";
        }
        else if (HasUsableContext())
        {
            wanted = VideoRenderBackend.Gpu;
            reason = "a graphics context is available.";
        }
        else if (renderPath == VideoRenderPath.GpuNoFallback)
        {
            throw new VideoPlaybackException(
                grContext == null
                    ? "This presenter's RenderPath is GpuNoFallback, but it has no graphics context: pass a "
                      + "GRContext to the SkiaVideoPresenter constructor or call UseGpu(GRContext) before "
                      + "drawing, or set RenderPath to GpuAuto to let it fall back to the processor."
                    : "This presenter's RenderPath is GpuNoFallback, and the GRContext it was given has been "
                      + "abandoned: supply a live one with UseGpu(GRContext), or set RenderPath to GpuAuto to "
                      + "let it fall back to the processor.");
        }
        else
        {
            wanted = VideoRenderBackend.Cpu;
            reason = grContext == null
                ? "no graphics context was supplied, so the processor path is running instead."
                : "the graphics context has been abandoned, so the processor path is running instead.";

            if (!fallbackReported)
            {
                fallbackReported = true;
                Trace.TraceInformation(
                    "CodeBrix.VideoPlayback.Skia: {0} Set RenderPath to GpuNoFallback if that should be an "
                    + "error instead. Configured effects are {1}.",
                    reason,
                    allowEffectsOnCpu ? "being applied on the processor" : "not being applied");
            }
        }

        if (backendResolved && wanted == activeBackend) return activeBackend;

        activeBackend = wanted;
        backendResolved = true;
        effectsChainDirty = true;
        RenderPathChanged?.Invoke(this, new VideoRenderPathChangedEventArgs(wanted, reason));
        return activeBackend;
    }

    /// <summary>
    /// Takes the newest frame, if there is one, and composes it onto the off-screen surface.
    /// </summary>
    /// <returns>True when a new frame was composed; false when the mailbox was empty.</returns>
    /// <exception cref="VideoPlaybackException">
    /// The graphics path was demanded and cannot be had, or a graphics resource could not be created.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// <see cref="Draw" /> calls this first, so most applications never call it. Call it directly when you
    /// want the composition to happen at a moment of your choosing - to capture a frame without drawing one,
    /// say.
    /// </remarks>
    public bool Update()
    {
        ThrowIfDisposed();
        VideoRenderBackend backend = ResolveRenderPath();

        if (!source.TryTakeLatest(out VideoFrame frame)) return false;

        try
        {
            Compose(frame, backend);
        }
        finally
        {
            frame.Dispose();
        }

        return true;
    }

    /// <summary>Draws the composed video into a canvas.</summary>
    /// <param name="canvas">The canvas to draw into - the host view's.</param>
    /// <param name="destination">The rectangle in that canvas the video should occupy.</param>
    /// <param name="stretch">How to fit the picture into the rectangle. Letterboxed by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">
    /// The graphics path was demanded and cannot be had, or a graphics resource could not be created.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// <para>
    /// This collects the newest frame first, so calling it from a paint handler is all an application has to
    /// do. Nothing is drawn until a frame has arrived.
    /// </para>
    /// <para>
    /// The canvas is clipped to <paramref name="destination" /> and its state is restored before the call
    /// returns.
    /// </para>
    /// </remarks>
    public void Draw(SKCanvas canvas, SKRect destination, VideoStretch stretch = VideoStretch.Uniform)
    {
        if (canvas == null) throw new ArgumentNullException(nameof(canvas));
        ThrowIfDisposed();

        Update();

        if (!hasComposition || surface == null || surfaceWidth <= 0 || surfaceHeight <= 0) return;

        SKRect target = ComputeDestinationRect(destination, displayWidth, displayHeight, stretch);

        canvas.Save();
        canvas.ClipRect(destination, SKClipOperation.Intersect, false);
        canvas.Translate(target.Left, target.Top);
        canvas.Scale(target.Width / surfaceWidth, target.Height / surfaceHeight);
        surface.Draw(canvas, 0f, 0f, BlitSampling, null);
        canvas.Restore();

        framesDrawn++;
    }

    /// <summary>Takes a copy of the composed picture that the caller owns.</summary>
    /// <returns>
    /// A readable image of the composition surface, which the CALLER must dispose, or null when nothing has
    /// been composed yet.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// The image is always readable on the processor, whichever path composed it, so its pixels can be
    /// encoded, hashed or written to a file. This is the hook for screenshots, and for recording the composed
    /// output rather than the source.
    /// </remarks>
    public SKImage CaptureComposedFrame()
    {
        ThrowIfDisposed();
        if (!hasComposition || surface == null) return null;

        SKImageInfo info = new SKImageInfo(surfaceWidth, surfaceHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

        using SKImage snapshot = surface.Snapshot();
        using SKBitmap scratch = new SKBitmap(info);

        if (!snapshot.ReadPixels(info, scratch.GetPixels(), info.RowBytes, 0, 0)) return null;

        return SKImage.FromPixelCopy(info, scratch.GetPixels(), info.RowBytes);
    }

    /// <summary>
    /// Composes the effect chain, if it needs it, and hands back the resultant table.
    /// </summary>
    /// <returns>
    /// The single lookup table the whole chain reduces to, or null when the chain is empty. The table is a
    /// copy; changing it changes nothing.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    /// <remarks>
    /// Useful for showing a user what a chain does, for saving a chain as one file, and for testing. It
    /// counts as a composition in <see cref="GetStatistics" /> when it actually recomposes.
    /// </remarks>
    public Lut3D GetResultantLut()
    {
        ThrowIfDisposed();
        if (effects.Count == 0) return null;

        if (effectsChainDirty || composer == null || composer.Size != effectLutSize) ComposeEffectChain();
        return resultantLut;
    }

    /// <summary>Takes a snapshot of the presenter's counters.</summary>
    /// <returns>The counters as they stood at the moment of the call.</returns>
    public SkiaVideoPresenterStatistics GetStatistics() =>
        new SkiaVideoPresenterStatistics(framesComposed, framesDrawn, surfaceAllocations, effectCompositions);

    /// <summary>Sets every counter back to zero. Nothing else changes.</summary>
    public void ResetStatistics()
    {
        framesComposed = 0;
        framesDrawn = 0;
        surfaceAllocations = 0;
        effectCompositions = 0;
    }

    /// <summary>
    /// Releases the composition surface, the pooled pixels, the compiled shaders and the presenter's own
    /// mailbox.
    /// </summary>
    /// <remarks>
    /// The graphics context and any attached mailbox belong to whoever made them and are left alone.
    /// </remarks>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        source.Invalidated -= OnSourceInvalidated;
        effects.CollectionChanged -= OnEffectsChanged;

        ReleaseSurface();

        cachedImage?.Dispose();
        cachedImage = null;

        lookupAtlasImage?.Dispose();
        lookupAtlasImage = null;
        lookupAtlas?.Dispose();
        lookupAtlas = null;

        renderer.Dispose();
        bgraPool.Dispose();
        ownMailbox.Dispose();

        Invalidated = null;
        RenderPathChanged = null;
        Composing = null;
    }

    private void OnSourceInvalidated(object sender, EventArgs args) => Invalidated?.Invoke(this, EventArgs.Empty);

    private void OnEffectsChanged(object sender, NotifyCollectionChangedEventArgs args) => effectsChainDirty = true;

    private bool HasUsableContext() => grContext != null && !grContext.IsAbandoned;

    private VideoRenderBackend PeekBackend() =>
        renderPath != VideoRenderPath.Cpu && HasUsableContext() ? VideoRenderBackend.Gpu : VideoRenderBackend.Cpu;

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(SkiaVideoPresenter));
    }

    private void Compose(VideoFrame frame, VideoRenderBackend backend)
    {
        effectsActive = effects.Count > 0 && (backend == VideoRenderBackend.Gpu || allowEffectsOnCpu);
        if (effectsActive && (effectsChainDirty || composer == null || composer.Size != effectLutSize))
        {
            ComposeEffectChain();
        }

        EnsureSurface(frame, backend);

        cachedImage?.Dispose();
        cachedImage = null;

        if (backend == VideoRenderBackend.Gpu) ComposeOnGpu(frame);
        else ComposeOnCpu(frame);

        displayWidth = frame.DisplayWidth > 0 ? frame.DisplayWidth : frame.Width;
        displayHeight = frame.DisplayHeight > 0 ? frame.DisplayHeight : frame.Height;
        lastTimestamp = frame.Timestamp;
        lastFrameNumber = frame.FrameNumber;
        hasComposition = true;
        framesComposed++;

        RunOverlays(backend);
    }

    private void RunOverlays(VideoRenderBackend backend)
    {
        EventHandler<VideoComposingEventArgs> composing = Composing;
        if (layers.Count == 0 && composing == null) return;

        VideoCompositionContext context = new VideoCompositionContext(
            SKRect.Create(0f, 0f, surfaceWidth, surfaceHeight),
            surfaceWidth,
            surfaceHeight,
            displayWidth,
            displayHeight,
            lastTimestamp,
            lastFrameNumber,
            backend,
            effectsActive);

        SKCanvas canvas = surface.Canvas;

        foreach (IVideoLayer layer in layers)
        {
            if (layer == null) continue;
            canvas.Save();
            try
            {
                layer.Draw(canvas, context);
            }
            finally
            {
                canvas.Restore();
            }
        }

        if (composing != null)
        {
            canvas.Save();
            try
            {
                composing(this, new VideoComposingEventArgs(canvas, context));
            }
            finally
            {
                canvas.Restore();
            }
        }
    }

    private unsafe void ComposeOnCpu(VideoFrame frame)
    {
        VideoFrameConverter.ToBgra32(frame, cpuSurfaceBuffer.AsSpan(), cpuSurfaceBuffer.Stride);

        if (!effectsActive || resultantLut == null) return;

        WarnOnceAboutEffectsOnCpu();
        CpuLutApplier.Apply(resultantLut, cpuSurfaceBuffer, effectInterpolation);
    }

    private void ComposeOnGpu(VideoFrame frame)
    {
        // The pool must not recycle this frame's memory until the upload has actually been handed to the
        // driver, so a fence goes in the buffer's tag before the upload and is signalled after the submit.
        SkiaGpuUploadFence fence = new SkiaGpuUploadFence();
        frame.Buffer.Tag = fence;

        try
        {
            bool useLookup = effectsActive && lookupAtlasImage != null;
            renderer.Render(
                frame,
                surface,
                grContext,
                useLookup ? lookupAtlasImage : null,
                useLookup ? composer.Size : 0,
                effectInterpolation);
        }
        finally
        {
            fence.Signal();
        }
    }

    private unsafe void ComposeEffectChain()
    {
        if (composer == null || composer.Size != effectLutSize) composer = new EffectComposer(effectLutSize);
        else composer.Reset();

        composer.Interpolation = effectInterpolation;

        foreach (IVideoFrameEffect effect in effects)
        {
            effect?.Compose(composer);
        }

        resultantLut = composer.ToLut3D();

        int width = LutAtlas.GetWidth(effectLutSize);
        int height = LutAtlas.GetHeight(effectLutSize);

        if (lookupAtlas == null || lookupAtlas.Width != width || lookupAtlas.Height != height)
        {
            lookupAtlas?.Dispose();
            lookupAtlas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        }

        LutAtlas.Write(
            composer,
            new Span<byte>((void*)lookupAtlas.GetPixels(), lookupAtlas.ByteCount),
            lookupAtlas.RowBytes);

        lookupAtlas.NotifyPixelsChanged();

        lookupAtlasImage?.Dispose();
        lookupAtlasImage = SKImage.FromBitmap(lookupAtlas);

        effectsChainDirty = false;
        effectCompositions++;
    }

    private void EnsureSurface(VideoFrame frame, VideoRenderBackend backend)
    {
        int width = frame.Width;
        int height = frame.Height;

        if (surface != null && surfaceWidth == width && surfaceHeight == height && surfaceBackend == backend)
        {
            return;
        }

        ReleaseSurface();

        SKImageInfo info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        if (backend == VideoRenderBackend.Cpu)
        {
            cpuSurfaceBuffer = bgraPool.Rent(width, height);
            surface = SKSurface.Create(info, cpuSurfaceBuffer.Data, cpuSurfaceBuffer.Stride);

            if (surface == null)
            {
                bgraPool.Return(cpuSurfaceBuffer);
                cpuSurfaceBuffer = null;
                throw new VideoPlaybackException(
                    $"SkiaSharp would not make a {width}x{height} BGRA raster surface over the pooled pixel "
                    + "buffer, so there is nothing to compose the video on.");
            }
        }
        else
        {
            surface = SKSurface.Create(grContext, true, info);

            if (surface == null)
            {
                throw new VideoPlaybackException(
                    $"The graphics context would not make a {width}x{height} BGRA render target, so there is "
                    + "nothing to compose the video on. Set RenderPath to GpuAuto to fall back to the "
                    + "processor path, or Cpu to use it outright.");
            }
        }

        surfaceWidth = width;
        surfaceHeight = height;
        surfaceBackend = backend;
        surfaceAllocations++;
        hasComposition = false;
        bgraPool.Trim(width, height);
    }

    private void ReleaseSurface()
    {
        surface?.Dispose();
        surface = null;

        if (cpuSurfaceBuffer != null)
        {
            bgraPool.Return(cpuSurfaceBuffer);
            cpuSurfaceBuffer = null;
        }

        cachedImage?.Dispose();
        cachedImage = null;

        surfaceWidth = 0;
        surfaceHeight = 0;
        hasComposition = false;
    }

    private static void WarnOnceAboutEffectsOnCpu()
    {
        if (Interlocked.Exchange(ref cpuEffectWarningIssued, 1) != 0) return;

        Trace.TraceWarning(
            "CodeBrix.VideoPlayback.Skia: AllowEffectsOnCpu is set, so the composed colour lookup table is "
            + "being applied to every pixel of every frame on the processor. That costs roughly as much again "
            + "as the colour conversion itself and will lower the frame rate this machine can sustain. Supply "
            + "a GRContext to move the effect chain onto the graphics device. This warning is issued once per "
            + "process.");
    }
}
