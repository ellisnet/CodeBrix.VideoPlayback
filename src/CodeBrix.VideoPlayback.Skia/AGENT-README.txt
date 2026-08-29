================================================================================
AGENT-README: CodeBrix.VideoPlayback.Skia
A Guide for AI Coding Agents - CONSUMING the CodeBrix.VideoPlayback.Skia.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========
CodeBrix.VideoPlayback.Skia draws the frames CodeBrix.VideoPlayback decodes.

The playback library has no drawing surface on purpose: it demultiplexes,
decodes, paces frames to a clock and hands the newest one to whoever is
painting. This package is the "whoever". It takes that frame, composes it on an
off-screen surface, and blits the result into whatever canvas your application
owns.

    VideoPlaybackSession ─► VideoFramePresenter ─► SkiaVideoPresenter ─► your SKCanvas
      (decodes, paces)         (newest frame)        (converts, composes)     (a XAML host
                                                                               element, a
                                                                               window, an
                                                                               image file)

There is exactly one class you have to learn: SkiaVideoPresenter. Attach it to
a session's presenter, call Draw from your paint handler, and you have video.

WHAT IT ACTUALLY DOES, AND WHY THE SHAPE IS WHAT IT IS

  * TWO RENDER PATHS, BOTH FIRST CLASS. Give it a GRContext and the three
    Y'CbCr planes are uploaded as single-channel textures and ONE shader does
    the colour conversion and the whole effect chain in a single pass, at full
    sample precision. Give it nothing and the core's vector converter turns the
    frame into BGRA pixels straight into the composition surface's own memory,
    with no copy at all. The second is not a degraded mode: it is the right
    answer on a machine with no usable graphics device, and it allocates
    nothing per frame.

  * AN OFF-SCREEN SURFACE, THEN A BLIT. The presenter never draws to your
    window directly. It draws the video into a surface of its own, runs your
    Layers and your Composing handler over the top, and blits the finished
    picture. That indirection is the point: an off-screen surface is a canvas
    anybody can draw on, which is what makes subtitles, heads-up overlays,
    annotation and camera-over-video possible without this class knowing about
    any of them. CaptureComposedFrame() hands back what was composed, which is
    a screenshot.

  * EFFECTS THAT COST ONE TEXTURE SAMPLE. A chain of colour effects is composed
    ONCE, when the chain changes, into a single three-dimensional lookup table.
    Ten effects cost what one costs. On the processor path they are ignored by
    default, because a per-pixel lookup on every frame is expensive and a
    graphics fallback should not become a slideshow; AllowEffectsOnCpu turns
    them on when the effect chain is the point of the picture.

Package reference: plain SkiaSharp, and nothing else. No view package, no
windowing toolkit, no native binary. Your application chooses the SkiaSharp
native asset package that suits the platforms it ships on - see INSTALLATION.

Target framework: .NET 10 or later. License: MIT.


INSTALLATION
============
    dotnet add package CodeBrix.VideoPlayback.Skia.MitLicenseForever

Or in a project file:

    <PackageReference Include="CodeBrix.VideoPlayback.Skia.MitLicenseForever" Version="*" />

That pulls in CodeBrix.VideoPlayback (and, through it, CodeBrix.Audio) and
SkiaSharp. Then add, as your application needs them:

  * a SkiaSharp NATIVE ASSET package for each platform you ship on. This
    library deliberately does not choose one: choosing for you would break
    every platform you did not have in mind. On Windows and macOS the SkiaSharp
    package attaches one for you; on LINUX you must name it yourself, and there
    are two to choose between:

        <PackageReference Include="SkiaSharp.NativeAssets.Linux" />
                 links the system fontconfig and freetype
        <PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" />
                 self-contained, for a machine with neither

  * an AV1 decoder package, to play AV1 video;
  * CodeBrix.Audio.Opus.BsdLicenseForever, to play Opus audio - and then call
    CodeBrixAudioOpus.Register() at start-up. An application that only plays
    Vorbis audio needs neither the package nor the call, and its published
    output contains no Opus binary at all.

If your host framework already gives you a Skia view - an SKElement, an
SKXamlCanvas, an SKCanvasView - keep using it. This package does not replace it
and does not want to know about it; it draws into the canvas that view hands
you.


KEY NAMESPACES / USINGS
=======================
    using CodeBrix.VideoPlayback.Skia;              // SkiaVideoPresenter
    using CodeBrix.VideoPlayback.Skia.Rendering;    // VideoStretch, VideoRenderPath,
                                                    //   VideoRenderBackend, the fence,
                                                    //   the statistics
    using CodeBrix.VideoPlayback.Skia.Composition;  // IVideoLayer, VideoCompositionContext,
                                                    //   VideoComposingEventArgs
    using CodeBrix.VideoPlayback.Skia.Effects;      // IVideoFrameEffect, LutEffect,
                                                    //   EffectComposer
    using CodeBrix.VideoPlayback.Color.Luts;        // Lut3D, Lut1D, CubeLutFile, LutLayer,
                                                    //   LutComposer - all in the CORE package

And, from the playback library and Skia itself:

    using CodeBrix.VideoPlayback;                   // VideoPlaybackSession
    using CodeBrix.VideoPlayback.Presentation;      // VideoFramePresenter
    using SkiaSharp;                                // SKCanvas, SKRect, SKImage, GRContext


CORE API REFERENCE
==================

SkiaVideoPresenter - the whole package
--------------------------------------
Construction

    new SkiaVideoPresenter()                    starts on the processor path
    new SkiaVideoPresenter(GRContext context)   starts on the graphics path

  The presenter does NOT own the context. It never disposes it; the context
  must outlive the presenter and must be current on the thread that draws.

Getting frames in

    void Attach(VideoFramePresenter mailbox)    read a session's mailbox
    void Detach()                               go back to the presenter's own
    void Present(VideoFrame frame)              hand a frame over directly
    VideoFramePresenter Source { get; }          which mailbox is being read
    bool IsAttached { get; }

  Present takes its OWN reference to the frame; you keep and dispose yours.
  Whichever way frames arrive, the newest one always wins - a frame nobody
  collected in time is simply replaced.

Drawing

    void Draw(SKCanvas canvas, SKRect destination,
              VideoStretch stretch = VideoStretch.Uniform)
    bool Update()
    SKImage CurrentImage { get; }
    SKImage CaptureComposedFrame()

  Draw collects the newest frame, composes it, and blits it - so calling it
  from a paint handler is all most applications ever do. Update does the first
  half only, for an application that wants to compose at a moment of its own
  choosing. CurrentImage is the composed picture as an image the PRESENTER owns
  and replaces at the next frame; CaptureComposedFrame is a copy YOU own and
  must dispose, readable on the processor whichever path composed it.

Choosing a render path

    VideoRenderPath RenderPath { get; set; }        GpuAuto (default) | GpuNoFallback | Cpu
    VideoRenderBackend ActiveRenderPath { get; }    Gpu | Cpu - what is running
    void UseGpu(GRContext context)                  supply or withdraw a context
    GRContext GraphicsContext { get; }
    VideoRenderBackend ResolveRenderPath()          settle it now rather than at the first frame
    event RenderPathChanged                         announced when it settles or changes

  GpuAuto takes the graphics device when one is there and falls back to the
  processor when it is not - no exception, no error dialogue, the video simply
  plays. GpuNoFallback is for an application whose picture is WRONG without the
  effect chain: it fails with a clear message instead of degrading. Cpu forces
  the processor path.

Effects

    ObservableCollection<IVideoFrameEffect> Effects { get; }
    bool EffectsActive { get; }
    bool AllowEffectsOnCpu { get; set; }            false by default
    LutInterpolation EffectInterpolation { get; set; }   Tetrahedral by default
    int EffectLutSize { get; set; }                 33 by default
    Lut3D GetResultantLut()

  Editing Effects marks the chain for recomposition, which happens at the next
  frame - not per frame. EffectsActive tells you whether the chain is actually
  being applied; it is false, deliberately and silently, when the presenter is
  on the processor path and AllowEffectsOnCpu is not set.

Composition

    ObservableCollection<IVideoLayer> Layers { get; }
    event Composing                                 EventHandler<VideoComposingEventArgs>

  Layers draw in list order, on the composition surface, in VIDEO pixels -
  after the video and before the blit, so what they draw is scaled and
  letterboxed along with the picture. The Composing event is the same hook for
  an application that would rather write a handler than a class.

Facts about what is showing

    bool HasComposedFrame { get; }
    int ComposedWidth { get; }        int ComposedHeight { get; }
    int DisplayWidth { get; }         int DisplayHeight { get; }
    TimeSpan CurrentTimestamp { get; }
    long CurrentFrameNumber { get; }
    event Invalidated                 a new frame has arrived; repaint

  Invalidated is raised on the DECODE thread. Mark the view dirty and return;
  draw on the drawing thread.

Diagnostics

    SkiaVideoPresenterStatistics GetStatistics()
    void ResetStatistics()

  FramesComposed, FramesDrawn, SurfaceAllocations, EffectCompositions.
  SurfaceAllocations should stop rising once playback is warm and rise again
  only when the frame size changes; EffectCompositions should rise only when
  you edit the chain.

Geometry, as a pure function

    static SKRect ComputeDestinationRect(SKRect destination,
                                         int contentWidth, int contentHeight,
                                         VideoStretch stretch)

  The same arithmetic Draw uses, exposed because it is what a host view needs
  in order to answer "where on screen is this video pixel" - for a click, a
  caption position, a hit test.

VideoStretch
------------
    None            the picture at its own display size, centred and clipped
    Fill            stretched to the destination, aspect ratio ignored
    Uniform         scaled to fit, aspect ratio kept, letterboxed (the default)
    UniformToFill   scaled to cover, aspect ratio kept, edges clipped

  The aspect ratio these keep is the frame's DISPLAY aspect ratio, so
  anamorphic content comes out the shape its author intended.

IVideoLayer and VideoCompositionContext
---------------------------------------
    void Draw(SKCanvas canvas, VideoCompositionContext context)

  The context carries VideoRect (where the video sits on the composition
  surface), FrameWidth / FrameHeight, DisplayWidth / DisplayHeight, Timestamp,
  FrameNumber, Backend and EffectsActive. The canvas is saved and restored
  around your call, so transform and clip it freely.

IVideoFrameEffect, LutEffect, EffectComposer
--------------------------------------------
    interface IVideoFrameEffect { string Name; void Compose(EffectComposer c); }

    new LutEffect(Lut3D table)         new LutEffect(Lut3D table, string name)
    new LutEffect(Lut3D table, string name, double applyAtPercent)
    new LutEffect(Lut1D curves)        new LutEffect(Lut1D curves, string name)
    new LutEffect(Lut1D curves, string name, double applyAtPercent)
    new LutEffect(LutLayer layer)

    LutEffect.FromCubeFile(string path)
    LutEffect.FromCubeFile(string path, double applyAtPercent)
    LutEffect.FromCube(CubeLut cube, double applyAtPercent = 100)

    double ApplyAtPercent { get; }     100 by default - the whole table.
                                       50 lands half way between the colour as
                                       it reached this effect and what the table
                                       makes of it; 0 leaves it alone and costs
                                       nothing. The blend happens ONCE, when the
                                       chain is composed, never per pixel.
    LutLayer Layer { get; }            Lut3D / Lut1D pass through to it

    EffectComposer: Size, NodeCount, Interpolation, Reset(), ApplyLut(lut),
        ApplyLut(lut, applyAtPercent), ApplyLayer(LutLayer), Apply(transform),
        GetNode(...), ToLut3D()

  THE TABLES THEMSELVES LIVE IN THE CORE PACKAGE, in
  CodeBrix.VideoPlayback.Color.Luts: Lut3D, Lut1D, CubeLutFile (read AND write),
  CubeLut, LutLayer, LutComposer, LutComposerOptions, LutInterpolation. The core
  has no drawing dependency, so the very same engine runs at authoring time in
  the `lutbake` tool - see the core's AGENT-README.txt. There is exactly ONE
  implementation of the composition arithmetic, and EffectComposer calls it.

  SkiaVideoPresenter.EffectInterpolation is the ONE knob for how a colour that
  falls BETWEEN a table's nodes is worked out. It is Tetrahedral by default and
  it governs three things at once, on purpose: how each effect's own table is
  sampled while the chain is folded, how the SHADER reads the resultant table on
  the graphics path, and how AllowEffectsOnCpu reads it on the processor path -
  so the two render paths always agree with each other, and the default agrees
  with what colour-grading tools and FFmpeg's lut3d filter mean by a lookup.

      presenter.EffectInterpolation = LutInterpolation.Trilinear;   // 2 fetches
      presenter.EffectInterpolation = LutInterpolation.Tetrahedral; // 4, default

  Tetrahedral holds the neutral axis exactly - a grey the table leaves grey stays
  grey - and costs four texture fetches a pixel. Trilinear is what a graphics
  card's texture filter does natively and costs two. On a smooth composed grade
  the two pictures differ by well under one level in 255; choose trilinear when
  the per-pixel cost matters more than agreeing with a grading tool. Changing it
  recomposes the chain. EffectComposer.Interpolation is the same setting seen
  from the composer's side.

  The composed grid always runs over 0 to 1, because that is the range a decoded
  frame's colour arrives in; a table declaring some other domain is still
  honoured, on the way in to its own lookup, where it belongs.

  A Lut3D's values are size*size*size triplets with RED changing fastest - the
  order ".cube" files use, so a parsed file's numbers go straight in.

  An effect can be anything expressible as "this colour becomes that colour".
  Anything that needs to see a pixel's NEIGHBOURS - a blur, a sharpen, a warp -
  is not an effect; it is a layer, and it gets a whole canvas.

SkiaGpuUploadFence
------------------
    bool IsSignaled { get; }    void Signal()

  You will not normally touch this. The presenter puts one in a frame buffer's
  Tag before a texture upload and signals it once the graphics commands have
  been submitted, which is what stops the pool handing that memory back to a
  decoder while a driver is still reading it. It is public so that a presenter
  of your own can use the same mechanism.


COMPLETE EXAMPLES
=================

1. Play a file into a Skia view.

    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Skia;
    using CodeBrix.VideoPlayback.Skia.Rendering;
    using SkiaSharp;

    public sealed class Player : IDisposable
    {
        private readonly VideoPlaybackSession session = new VideoPlaybackSession();
        private readonly SkiaVideoPresenter presenter = new SkiaVideoPresenter();

        public Player()
        {
            presenter.Attach(session.Presenter);
            presenter.Invalidated += (sender, args) => RequestRepaint();
        }

        public void Open(string path)
        {
            session.Open(path);
            session.Play();
        }

        // Call this from whatever your host framework calls when it wants a repaint.
        public void Paint(SKCanvas canvas, SKRect bounds)
        {
            canvas.Clear(SKColors.Black);
            presenter.Draw(canvas, bounds, VideoStretch.Uniform);
        }

        private void RequestRepaint()
        {
            // Mark the host element dirty here. Do NOT draw: this runs on the decode thread.
        }

        public void Dispose()
        {
            presenter.Dispose();
            session.Dispose();
        }
    }

2. A XAML host element (WPF's SKElement, WinUI's SKXamlCanvas, and the
   equivalents in MAUI and Avalonia all have this shape).

    // The host element owns the view and the paint event; the presenter owns the video.
    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear(SKColors.Black);
        presenter.Draw(
            e.Surface.Canvas,
            new SKRect(0, 0, e.Info.Width, e.Info.Height),
            VideoStretch.Uniform);
    }

    private void OnInvalidated(object sender, EventArgs args)
    {
        // Marshal to the user-interface thread and ask the element to repaint.
        Dispatcher.Invoke(() => hostElement.InvalidateVisual());
    }

3. Hand the presenter a graphics context.

    // Wherever your host gives you one - a Skia GPU view, an off-screen GL or
    // Vulkan context of your own - hand it over and the presenter moves to the
    // graphics path at the next frame.
    presenter.UseGpu(graphicsContext);

    // Or say at start-up that the graphics path is the only acceptable one.
    presenter.RenderPath = VideoRenderPath.GpuNoFallback;
    presenter.ResolveRenderPath();   // throws now, with a clear message, rather than later

    // The context must be current on the thread that calls Draw, and it must
    // outlive the presenter. The presenter never disposes it.

4. Find out what is actually running.

    presenter.RenderPathChanged += (sender, args) =>
        Console.WriteLine($"video is rendering on the {args.Backend}: {args.Reason}");

    presenter.RenderPath = VideoRenderPath.GpuAuto;   // the default

    if (presenter.ActiveRenderPath == VideoRenderBackend.Cpu && presenter.Effects.Count > 0)
    {
        // The chain is configured but not applied. Either accept that, or:
        presenter.AllowEffectsOnCpu = true;           // applied, and slower
    }

5. Grade the picture with a lookup table.

    using CodeBrix.VideoPlayback.Skia.Effects;

    presenter.Effects.Add(LutEffect.FromCubeFile("teal-and-orange.cube"));
    presenter.Effects.Add(LutEffect.FromCubeFile("film-stock.cube", 60));

    // The two are composed into ONE table at the next frame, and cost one
    // texture sample per pixel between them. Order matters: the second sees
    // what the first produced. The second is applied at 60 percent - the grade
    // dialled back, without re-exporting the file.

6. Write an effect that is not a file.

    using CodeBrix.VideoPlayback.Skia.Effects;

    public sealed class Desaturate : IVideoFrameEffect
    {
        private readonly float amount;

        public Desaturate(float amount) => this.amount = amount;

        public string Name => "desaturate";

        public void Compose(EffectComposer composer) =>
            composer.Apply((ref float red, ref float green, ref float blue) =>
            {
                float grey = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
                red += (grey - red) * amount;
                green += (grey - green) * amount;
                blue += (grey - blue) * amount;
            });
    }

    presenter.Effects.Add(new Desaturate(0.6f));

7. Draw on the video.

    using CodeBrix.VideoPlayback.Skia.Composition;

    public sealed class TimecodeBar : IVideoLayer
    {
        public void Draw(SKCanvas canvas, VideoCompositionContext context)
        {
            float width = context.VideoRect.Width * (float)context.FrameNumber / 250f;
            using SKPaint paint = new SKPaint { Color = new SKColor(255, 255, 255, 160) };
            canvas.DrawRect(
                SKRect.Create(0f, context.VideoRect.Bottom - 4f, width, 4f),
                paint);
        }
    }

    presenter.Layers.Add(new TimecodeBar());

8. Composite another picture over the video - a camera, a logo, anything.

    presenter.Composing += (sender, args) =>
    {
        SKImage latest = camera.LatestFrame;      // whatever your source gives you
        if (latest == null) return;

        float side = args.Context.VideoRect.Width / 4f;
        args.Canvas.DrawImage(
            latest,
            SKRect.Create(args.Context.VideoRect.Right - side - 8f, 8f, side, side * 9f / 16f),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    };

9. Take a picture of what is showing.

    using SKImage composed = presenter.CaptureComposedFrame();
    if (composed != null)
    {
        using SKData png = composed.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream file = File.Create("frame.png");
        png.SaveTo(file);
    }

    // The image includes the layers and anything the Composing handler drew,
    // because it is the composed surface and not the raw video.

10. Play with no display at all - a test, a thumbnail service, a render farm.

    using SkiaSharp;

    using SkiaVideoPresenter presenter = new SkiaVideoPresenter
    {
        RenderPath = VideoRenderPath.Cpu,
    };

    presenter.Attach(session.Presenter);

    using SKSurface surface = SKSurface.Create(
        new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));

    session.Open(path);
    session.Play();

    while (session.State == VideoPlaybackState.Playing)
    {
        surface.Canvas.Clear(SKColors.Black);
        presenter.Draw(surface.Canvas, SKRect.Create(0, 0, 1280, 720));
        Thread.Sleep(5);
    }


MINIMUM VIABLE PROJECT TEMPLATE
===============================
A console application that plays a clip through to its end and writes one
picture of it. Everything else is the same three lines with a window round
them.

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.VideoPlayback.Skia.MitLicenseForever" Version="*" />
        <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="*" />
      </ItemGroup>
    </Project>

    using System;
    using System.IO;
    using System.Threading;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Playback;
    using CodeBrix.VideoPlayback.Skia;
    using CodeBrix.VideoPlayback.Skia.Rendering;
    using SkiaSharp;

    using VideoPlaybackSession session = new VideoPlaybackSession();
    using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
    presenter.Attach(session.Presenter);

    using SKSurface view = SKSurface.Create(
        new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));

    session.Open(args[0]);
    session.Play();

    while (session.State == VideoPlaybackState.Playing)
    {
        view.Canvas.Clear(SKColors.Black);
        presenter.Draw(view.Canvas, SKRect.Create(0, 0, 640, 360));
        Thread.Sleep(5);
    }

    using SKImage picture = view.Snapshot();
    using SKData png = picture.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream file = File.Create("frame.png");
    png.SaveTo(file);

    // A video codec still has to be registered for anything but uncompressed
    // content - see the playback library's own AGENT-README.txt - and
    // CodeBrixAudioOpus.Register() has to be called for Opus audio.


PERFORMANCE TIPS
================
  * CALL Draw, NOT CurrentImage, IN A PAINT LOOP. Draw blits the composition
    surface with no allocation at all. CurrentImage snapshots it, which
    allocates an image per frame.

  * ONE PRESENTER PER VIDEO, KEPT ALIVE. It holds the composition surface, the
    pooled pixel buffer and the compiled shaders. Creating one per frame throws
    all three away every frame.

  * WATCH SurfaceAllocations. It should reach one and stay there. If it keeps
    climbing, the frame size is changing - which is legitimate for adaptive
    content and is a defect for anything else.

  * THE PROCESSOR PATH IS NOT SLOW. 1080p 8-bit 4:2:0 converts in about three
    milliseconds on a current x64 laptop core, which clears a 60-per-second
    budget with room. What is expensive is AllowEffectsOnCpu, which adds a
    per-pixel table lookup of roughly the same cost again.

  * EDIT THE EFFECT CHAIN RARELY. Composing it walks tens of thousands of grid
    nodes. That is nothing once, and noticeable every frame. Change a
    parameter by replacing the effect, not by editing the list twice a second.

  * A BIGGER EffectLutSize BUYS FIDELITY FOR HARD STEPS ONLY. 33 nodes is
    exact enough for any smooth grade. A chain with a posterise or a key in it
    wants more; nothing else does.

  * DRAW THE OVERLAYS YOU NEED, WHEN YOU NEED THEM. Layers cost ordinary Skia
    drawing on both paths, and an empty Layers list with no Composing handler
    costs nothing at all.


COMMON PITFALLS TO AVOID
========================
  * DO NOT DRAW FROM THE Invalidated HANDLER. It is raised on the decode
    thread. Mark the view dirty and return; draw when your host asks you to.

  * DO NOT DISPOSE CurrentImage. The presenter owns it and replaces it at the
    next frame. Dispose what CaptureComposedFrame gives you instead - that one
    is yours.

  * DO NOT SHARE ONE PRESENTER BETWEEN TWO THREADS. Present and Attach are safe
    from anywhere; everything that touches the surface - Update, Draw,
    CurrentImage, CaptureComposedFrame - belongs to one thread, the one that
    owns the graphics context.

  * DO NOT ASSUME THE GRAPHICS PATH IS RUNNING. With the default GpuAuto it
    might not be, and nothing will tell you unless you ask. Read
    ActiveRenderPath, or say GpuNoFallback and mean it.

  * DO NOT EXPECT EFFECTS TO APPLY ON THE PROCESSOR PATH. They are ignored,
    silently and on purpose, unless AllowEffectsOnCpu is set. EffectsActive is
    the property that tells you the truth.

  * DO NOT DISPOSE A GRContext YOU GAVE THE PRESENTER WHILE IT IS STILL
    DRAWING. The presenter does not own it and cannot know. Call
    UseGpu(null) first, or dispose the presenter first.

  * DO NOT ADD A SkiaSharp.Views PACKAGE BECAUSE OF THIS ONE. It depends on
    plain SkiaSharp and draws into a canvas. If your host framework needs a
    view package, that is between your host framework and you.

  * DO NOT FORGET THE NATIVE ASSET PACKAGE ON LINUX. SkiaSharp attaches one
    automatically on Windows and macOS and cannot on Linux, because there are
    two and only your application knows which. Without it you get a
    DllNotFoundException for libSkiaSharp the first time anything draws.

  * DO NOT FORGET CodeBrixAudioOpus.Register() FOR OPUS AUDIO. Referencing the
    package is not enough; nothing is discovered by reflection anywhere in this
    family. A file with Opus audio and no registration fails with a message
    that says exactly this.


WHAT THIS PACKAGE DOES NOT DO
=============================
  * IT IS NOT A VIEW. There is no control, no element, no XAML type here. Your
    host framework owns the view and gives you a canvas; this fills it.

  * IT DOES NOT DECODE ANYTHING. The playback library demultiplexes and a codec
    package decodes. This package draws what comes out.

  * IT DOES NOT CREATE A GRAPHICS CONTEXT. Contexts belong to windows, and this
    package has no window. Hand it one that your host already has.

  * IT DOES NOT TONE-MAP HIGH-DYNAMIC-RANGE CONTENT. A PQ or HLG frame is
    converted with its own colour matrix and its transfer curve ignored, on
    both paths. An effect of your own can do better on the graphics path.

  * IT DOES NOT RENDER CAPTIONS. The playback session gives you the active
    cues; drawing them is a layer, and a short one. Text rendering is a
    decision about typefaces and positioning that belongs to the application.

  * IT DOES NOT DO NEIGHBOURHOOD EFFECTS. Blurs, sharpens, warps and scalers
    are not colour lookups and cannot be composed into one. Draw them as a
    layer, with Skia's own filters.

  * IT DOES NOT RECORD. CaptureComposedFrame gives you a picture at a time; a
    recorder is an authoring tool, and authoring lives elsewhere in this
    family.


WORKING EXAMPLES ON GITHUB
==========================
  https://github.com/ellisnet/CodeBrix.VideoPlayback

  samples/CodeBrix.VideoPlayback.ConsumerShape
      The smallest honest application: it opens a bespoke .cbv file with Vorbis
      audio, plays it through with no display, draws every frame, writes one
      picture, and exits. It exists to be published and then looked at - its
      publish output is the proof that an application playing Vorbis-audio
      files carries no Opus binary.

  tests/CodeBrix.VideoPlayback.Skia.Tests
      Every part of this package, exercised: the letterbox arithmetic, the
      render-path rules, the composition surface, the layers, the effect chain,
      the upload fence, and the colour shader measured pixel for pixel against
      the playback library's own converter.


QUICK REFERENCE CARD
====================
TASK                                  CALL
------------------------------------  ------------------------------------------
Show a session's video                presenter.Attach(session.Presenter)
Paint it                              presenter.Draw(canvas, rect, stretch)
Paint it letterboxed                  presenter.Draw(canvas, rect)
Hand over a frame directly            presenter.Present(frame)
Use the graphics device               new SkiaVideoPresenter(context) / UseGpu(context)
Insist on the graphics device         RenderPath = VideoRenderPath.GpuNoFallback
Force the processor                   RenderPath = VideoRenderPath.Cpu
Ask what is running                   presenter.ActiveRenderPath
Be told when it changes               presenter.RenderPathChanged
Add a colour grade                    Effects.Add(LutEffect.FromCubeFile(path))
Dial a grade back                     Effects.Add(LutEffect.FromCubeFile(path, 60))
Add a grade of your own               Effects.Add(new LutEffect(new Lut3D(size, values)))
Apply grades without a graphics card  AllowEffectsOnCpu = true
Trade grade accuracy for speed        EffectInterpolation = LutInterpolation.Trilinear
Ask whether grades are applied        presenter.EffectsActive
See the composed grade                presenter.GetResultantLut()
Draw over the video                   Layers.Add(myLayer) / Composing += handler
Take a screenshot                     using SKImage p = presenter.CaptureComposedFrame()
Composite it yourself                 presenter.CurrentImage
Repaint when a frame arrives          presenter.Invalidated
Know what is showing                  CurrentFrameNumber, CurrentTimestamp
Check nothing is leaking              presenter.GetStatistics().SurfaceAllocations
Where is this video pixel on screen   SkiaVideoPresenter.ComputeDestinationRect(...)
================================================================================
