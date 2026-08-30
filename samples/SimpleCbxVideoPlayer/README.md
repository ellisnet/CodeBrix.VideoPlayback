# SimpleCbxVideoPlayer

An eight-head application that plays the sample videos in this repository - AV1 video with Opus sound, in
Matroska, WebM and the bespoke `.cbv` container - and grades the picture with a chain of `.cube` colour
lookup tables while it plays.

The eight heads come in two families that share no user-interface code at all: **six CodeBrix.Platform
heads** (Linux X11, Wayland and frame buffer; macOS; Windows Win32-Skia and WPF-Skia) and **two native
Windows heads** (WinUI 3 and WPF). Both families drive the same playback library through the same seam, and
that is the point the sample is making.

It exists to show three things working together in a real application, rather than in a console program:

1. **CodeBrix.VideoPlayback** demultiplexing a file and pacing decoded frames to a clock.
2. **CodeBrix.VideoPlayback.Skia** composing those frames - on the graphics device through one shader, or
   on the processor through the core's vector converter - and blitting the result into whatever canvas the
   application owns, whether that canvas belongs to a CodeBrix.Platform page, a WinUI page or a WPF window.
3. **CodeBrix.VideoPlayback.Dav1d** and **CodeBrix.Audio.Opus**, registered once at start-up, supplying the
   two decoders that nothing in this family discovers by reflection.

This is a DEVELOPMENT-REPOSITORY sample. It carries no media of its own: at start-up it walks up from its
own folder looking for a directory that holds `tests/assets/authoring`, and plays what it finds there. Run
it from inside a clone of this repository, or its drop-downs will be empty and it will say so.

---

## Running it

There are two solutions, and which one you want depends on the machine:

```
dotnet build SimpleCbxVideoPlayer.slnx -c Release            the six CodeBrix.Platform heads, anywhere
dotnet build SimpleCbxVideoPlayer.Windows.slnx -c Release    those PLUS the WinUI and WPF heads, on Windows
```

`SimpleCbxVideoPlayer.Windows.slnx` is the superset. It is separate because the WinUI head needs
Windows-host-only build tooling, and because that head declares no `Any CPU` platform - the Windows
solution restricts itself to `x86`, `x64` and `ARM64` so Visual Studio does not offer a platform it cannot
map. Build it with `-p:Platform=x64` on the command line.

Then run the head for the machine you are on:

| Head | Command |
|---|---|
| Linux X11 | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxX11 -c Release` |
| Linux Wayland | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxWayland -c Release` |
| Linux frame buffer | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxFrameBuffer -c Release` (from a text console, not from inside a desktop session) |
| macOS | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.MacOS -c Release` |
| Windows, Win32-Skia | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.Win32Skia -c Release` |
| Windows, WPF-Skia | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.WinWpfSkia -c Release` |
| Windows, native WPF | `dotnet run --project src/SimpleCbxVideoPlayer.Wpf -c Release` |
| Windows, native WinUI 3 | `dotnet run --project src/SimpleCbxVideoPlayer.WinUI -c Release -p:Platform=x64` |

Of the CodeBrix.Platform heads, only the X11 one has been run and verified on the machine this sample was
written on; the other five are built by the same solution build and share every line of their code with it.
Both native Windows heads have been run and verified, on the graphics path and the processor path, with a
graded chain and a bake round trip.

A note on the solution build: the two video libraries are consumed as published packages, so a `.slnx`
build needs nothing outside this folder.

---

## The three controls

**The video drop-down** lists every playable file found under `tests/assets/authoring`. The rule is a rule,
not a list: every sub-folder is read except `MP4/`, and every `.mkv`, `.webm` and `.cbv` file in it is
offered. So `MKV/`, `WebM/`, `CodeBrix-Mode1/` and `CodeBrix-Mode2/` appear today - twenty-four files -
`MP4/` never does, because nothing in this family reads an ISOBMFF file, and a folder added later will appear
without a line of code changing. The `CodeBrix-Mode2/` files are the bespoke `.cbv` container carrying Vorbis
audio, so they play with no Opus package anywhere in this sample.

**The render-path drop-down** sets `SkiaVideoPresenter.RenderPath`:

| Choice | What it does |
|---|---|
| GPU (auto) | Take the graphics device when there is one, and the processor when there is not. The default. |
| GPU only (no fallback) | Insist on the graphics device. When there is none, the presenter's own message is shown in the status line at the bottom of the window rather than the picture quietly degrading. That is the entire point of offering the choice. |
| CPU | Compose on the processor, whether or not a graphics device is available. |

Changing it re-applies immediately, and the line beside the drop-down says what is actually running -
`ActiveRenderPath`, not what was asked for.

**The transport** is Play, Pause, Stop, a scrub bar and a clock. Position comes from the audio clock while a
soundtrack is playing, which is what a listener is actually hearing, so it advances in the mixer's steps.
Play does one more thing besides starting the picture: it applies whatever the lookup-table panel currently
holds - see below.

---

## The lookup-table panel

Every `.cube` file under `tests/assets/LUTs/generated/` and `tests/assets/LUTs/found/` gets a row: a tick
box, the table's own `TITLE` (its file name when it has none), where it came from, and a percentage box that
starts at 40. `found/` is searched recursively, because it keeps one folder per upstream project.

`tests/assets/LUTs/invalid/` is NEVER read. Those two files are malformed on purpose, as negative test
fixtures, and offering one would offer a parse failure.

Rules the panel follows:

- **The chain is applied when you press Play, and only then.** Ticking a table or typing a percentage
  changes the panel, not the picture; the heading says how many tables are waiting. Pressing Play hands the
  panel's current state to the presenter. An unchanged panel re-applies the same chain, which the presenter
  recognises and skips, so pressing Play twice costs nothing.
- **The panel is read-only while the video is playing.** Pause or stop it to change the tables. Nothing in
  this application changes a grade under a running picture at a moment nobody chose.
- **Reaching the end of the file counts as stopped**, and the panel becomes editable again the moment it
  happens. Nobody pressed Pause or Stop, but the picture is no longer running, and the only thing that can
  happen next is Play - which is exactly when a chain is applied. So that is the moment the tables have to
  be reachable again.
- **The panel greys out whenever the picture is being composed on the processor**, whatever the transport is
  doing, with a note saying why. This application deliberately leaves `AllowEffectsOnCpu` alone, so a grade
  is a graphics-path feature here; `EffectsActive` is the property that tells the truth, and the status line
  shows it.
- Ticked tables are applied **in list order** - the order of the list, not the order you ticked them - and
  order matters: the second table sees what the first produced.
- A percentage is clamped to 0-100 and committed when the box loses focus. A table at 0 costs nothing.
- The whole chain is composed into ONE lookup table when it is applied - never per frame. Ten tables cost
  what one costs.
- **The Bake button is part of the panel** and is enabled and disabled with it, plus it needs at least one
  table ticked. It does NOT need the chain to have been played - see below.

So the enablement is a small matrix - covering the tick boxes, the percentages AND the Bake button - in
which "stopped" means stopped, never opened, OR played to the end:

| | Stopped | Paused | Playing |
|---|---|---|---|
| **GPU path** | editable | editable | read-only |
| **CPU path** | read-only | read-only | read-only |

### Bake chain to .cube

Under the list is a **Bake chain to .cube** button. It writes THE CHAIN THE PANEL HOLDS - the ticked tables,
at their percentages, in list order - out as a single `.cube` file, so a grade dialled in by eye can be
handed to FFmpeg, to a colour-grading tool, or back to this application.

**Play and Bake are two independent triggers on the same panel.** Play composes the chain and hands it to
the picture; Bake composes the chain and hands it to a file. Neither reads the other's result, and neither
one has to have happened first:

- A chain that has NEVER been played bakes perfectly well. Tick two tables, press Bake, and you get their
  resultant table - there is no need to play a video to get at it, and the video does not have to be the
  one you would eventually grade.
- A chain baked while something else is playing is still the panel's chain, not the picture's.
- What is on screen is never consulted. Composing a chain is arithmetic on the tables; it owes nothing to
  any frame.

They agree anyway, and deliberately so: both compose at the same size with the same tetrahedral sampling,
so the file the button writes and the table the shader samples are the same table down to the last bit.
That is what makes the smoke round trip below - bake a chain, feed the file back at 100 per cent, compare
the frames - a real test rather than a tautology.

**You choose where it goes.** Pressing Bake opens your platform's own save dialog - `SaveFileDialog` on WPF,
the file picker on WinUI and on the CodeBrix.Platform heads - with a stamped name suggested
(`chain-20260829-141530.cube`) and no folder proposed. Cancel and nothing is written and nothing is said;
deciding not to save is not a failure. Nothing is ever written to a location the application picked on its
own. Once written, the full path appears under the button. The `TITLE` line names the chain, for example
`SimpleCbxVideoPlayer: cool_33@40 + sepia_33@40`.

The button is part of the lookup-table panel and follows the panel exactly: it needs something ticked, and
it is disabled while the picture is running and on the processor path, like every other control in there.

#### Baking needs no video at all

Worth saying plainly, because it shapes what you can build on these packages: composing a chain and writing
a `.cube` file is **`CodeBrix.VideoPlayback` on its own**. No `.Skia`, no presenter, no decoder, no window.
An application that only lets a person tick some tables and save the result is this much:

```csharp
using CodeBrix.VideoPlayback.Color.Luts;

List<LutLayer> chain =
[
    LutLayer.FromCubeFile(warmPath, 40d),
    LutLayer.FromCubeFile(coolPath, 65d),
];

Lut3D resultant = LutComposer.Compose(chain);
CubeLutFile.Write(resultant, chosenPath, "warm@40 + cool@65");
```

`LutLayer`, `LutComposer`, `Lut3D`, `CubeLut` and `CubeLutFile` all live in
`CodeBrix.VideoPlayback.Color.Luts`, in the core package, whose only dependency is `CodeBrix.Audio`. This
sample's own bake is those four lines - it pins the output size to the presenter's so the file and the
picture keep agreeing, and that constant is the only reason its bake mentions Skia at all. An application
already holding `IVideoFrameEffect`s rather than file paths can reach the same arithmetic from the Skia side
with `EffectComposer.Compose(effects)`, which also needs no presenter.

---

## What supplies the graphics context

Every head does the same thing and reaches for a different pair of controls to do it. It builds a GPU canvas
and a processor canvas, waits until the GPU one has had its chance to start, then paints whichever one it
settled on and collapses the other. When the GPU canvas started, its `GRContext` is handed to the presenter
and the lookup tables become real; when it did not, the head tells the player it has no context, and the
status line and the lookup-table panel say so. Both render paths draw correctly into whichever canvas is
showing.

| Head | GPU canvas | Processor canvas |
|---|---|---|
| CodeBrix.Platform | `SkiaGLCanvasElement` (`CodeBrix.Platform.Graphics3DGL`) | `SKXamlCanvas` (`CodeBrix.Platform.SkiaSharp.Views`) |
| WinUI 3 | `SKSwapChainPanel` (`SkiaSharp.Views.WinUI`) | `SKXamlCanvas` (same package) |
| WPF | `SKGLElement` (`SkiaSharp.Views.WPF`) | `SKElement` (same package) |

The first row is the awkward one, and it is why `Graphics3DGL` appears at all: on the CodeBrix.Platform
heads `SKXamlCanvas` paints on the processor and cannot hand out a `GRContext`, and the `SKSwapChainPanel`
beside it is a non-functional placeholder. So the page builds a `SkiaGLCanvasElement` instead, which creates
an offscreen OpenGL context and gives each paint a GPU-backed `SKSurface` **and** the `GRContext` behind it.

The two native heads need no such arrangement. SkiaSharp's own WinUI and WPF views ship a working GPU
element - ANGLE behind `SKSwapChainPanel`, OpenGL behind `SKGLElement` - and each exposes its `GRContext` as
a plain property. Both are built in code rather than in XAML, in a `try`, because each starts its graphics
API in its own constructor: a machine with no usable context throws, and a throw inside
`InitializeComponent` would take the whole window with it. Built in code, a failure just settles on the
processor canvas, which is exactly what the "GPU only (no fallback)" choice exists to make visible.

---

## How the projects are arranged

```
src/libs/SimpleCbxVideoPlayer.SkiaVideo/   the ONLY project that names the video packages
src/CodeBrixPlatform/…Core/                view models; sees SkiaVideo's types and nothing else
src/CodeBrixPlatform/…UI/                  App.xaml and the page, shared by the six CodeBrix.Platform heads
src/CodeBrixPlatform/…<Head>/              one project per platform
src/SimpleCbxVideoPlayer.WinUI/            the native WinUI 3 head: its own view models, page and App
src/SimpleCbxVideoPlayer.Wpf/              the native WPF head: its own view models, window and App
tests/libs/…SkiaVideo.Tests/               device-less tests for everything above the presenter
```

`SimpleCbxVideoPlayer.SkiaVideo` is the seam, on purpose: the application talks to a
`VideoPlaybackController` (open, play, pause, stop, seek, "draw into this canvas", "here is a graphics
context", "apply this chain of tables") and never names a type from CodeBrix.VideoPlayback,
CodeBrix.VideoPlayback.Skia, CodeBrix.VideoPlayback.Dav1d or CodeBrix.Audio.Opus. The two native heads are
the proof: each references that library UNCHANGED - the same project, not a copy of it - and neither names
a video type anywhere.

### Why the native heads carry their own view models

Each of the three families holds its own copy of `MainViewModel`, `LutListItem`, `RenderPathChoice` and
`VideoListItem`, rather than sharing one file three ways. They differ in two lines and no more:

- **Which `Visibility` they mean.** The CodeBrix.Platform and WinUI copies use
  `Microsoft.UI.Xaml.Visibility`; the WPF copy uses `System.Windows.Visibility`.
- **`[Microsoft.UI.Xaml.Data.Bindable]`.** WinUI wants it, WPF has no such attribute, so the WPF copy
  drops it.

Everything else - all eight hundred lines of transport, corpus, chain and smoke-run logic - is the same
text in all three. Copies were chosen over a shared file with `#if` guards because this is a sample, and a
sample that has to be mentally un-preprocessed before it can be read is a worse sample. Each head reads
straight through.

The three copies also differ in one string apiece, and it is not decoration: the smoke run prints which
canvas it settled on, so each head names its own (`SkiaGLCanvasElement`, `SKSwapChainPanel`, `SKGLElement`).

---

## Package references

Every CodeBrix package this sample consumes is the published nuget.org version, including
`CodeBrix.VideoPlayback.MitLicenseForever`, `CodeBrix.VideoPlayback.Skia.MitLicenseForever` and
`CodeBrix.VideoPlayback.Dav1d.BsdLicenseForever` (published 2026-08-29). No extra restore source and no
`nuget.config` are needed, and nothing outside this folder is built.

Each family adds exactly one more package for its own MVVM toolkit, and the native heads add SkiaSharp's
views for their platform:

| Family | Toolkit | Canvases |
|---|---|---|
| CodeBrix.Platform | `CodeBrix.Platform.ApacheLicenseForever` | `CodeBrix.Platform.SkiaSharp.Views` + `.Graphics3DGL` |
| WinUI 3 | `CodeBrix.Platform.WinUI.ApacheLicenseForever` | `SkiaSharp.Views.WinUI` |
| WPF | `CodeBrix.Platform.WPF.ApacheLicenseForever` | `SkiaSharp.Views.WPF` |

The two `CodeBrix.Platform.WinUI` and `.WPF` packages are the *native* members of that family - the same
"Simple" MVVM toolkit (`SimpleViewModel`, `SimpleCommand`, `SimpleServiceResolver`) compiled against real
WinUI and real WPF, sharing no code with the Uno-derived CodeBrix.Platform packages. That is what lets the
view models be copied between the families almost verbatim.

The SkiaSharp versions have to agree, and they do: `CodeBrix.VideoPlayback.Skia` wants SkiaSharp 4.151.0,
which is exactly what `SkiaSharp.Views.WinUI` and `SkiaSharp.Views.WPF` 4.151.0 bring with them.

---

## The hidden smoke mode

The application can verify itself without a person watching. With `--smoke` it still opens its window and
still paints through the real head and the real canvas, but it chooses a file, plays it, writes one composed
frame to a PNG, prints what it saw and leaves with an exit code:

```
SimpleCbxVideoPlayer.LinuxX11 --smoke MKV/landscape_hd.mkv --snapshot /tmp/frame.png --exit
SimpleCbxVideoPlayer.LinuxX11 --smoke MKV/landscape_hd.mkv --lut sepia_33.cube@40 --lut cool_33.cube@40 \
    --snapshot /tmp/graded.png --exit
```

The two together verify a bake without anyone looking at it: the first run grades a frame and writes the
chain out, the second applies THAT file at 100 per cent and measures the result against the first frame.

```
SimpleCbxVideoPlayer.LinuxX11 --smoke MKV/landscape_hd.mkv --lut sepia_33.cube@40 --lut cool_33.cube@40 \
    --bake /tmp/chain.cube --snapshot /tmp/chain.png --no-audio --exit
SimpleCbxVideoPlayer.LinuxX11 --smoke MKV/landscape_hd.mkv --lut /tmp/chain.cube@100 \
    --snapshot /tmp/baked.png --compare /tmp/chain.png --no-audio --exit
```

| Switch | Meaning |
|---|---|
| `--smoke <name>` | The video to play: `MKV/landscape_hd.mkv`, a bare file name, or a full path. |
| `--snapshot <path>` | Write the composed frame there. The run pauses and seeks to a fixed position first, so two runs of the same file capture the SAME picture and their snapshots can be compared byte for byte. |
| `--render-path <gpuauto\|gpunofallback\|cpu>` | Which path to compose on. |
| `--lut <name>[@<percent>]` | Tick a table, at a percentage that defaults to 40. Repeatable; the chain is applied in LIST order, exactly as pressing Play does. An `=` is accepted in place of the `@`. The text after the last separator MUST be a number - a value whose tail is not one is refused rather than read as part of the name - and a name that matches no table FAILS the run rather than being skipped. A full path to a `.cube` file outside the corpus is accepted too, which is how a baked chain is fed back in. |
| `--bake <path.cube>` | Write the chain the panel holds out as one `.cube` file, exactly as the button does - to this path, with no dialog, because a smoke run has nobody to ask. Fails the run if there is no chain to write. |
| `--compare <path.png>` | Measure the captured frame against another picture and FAIL when any colour channel differs by more than the tolerance. Needs `--snapshot`. |
| `--compare-tolerance <levels>` | How far a channel may differ before `--compare` fails. Two levels by default. |
| `--seconds <n>` | How long to play before capturing. Two seconds by default. |
| `--snapshot-at <n>` | The position the captured frame is seeked to. One second by default. |
| `--until-ended` | Play the whole file instead of a fixed stretch of it. |
| `--no-audio` | Open the file with its soundtrack switched off. |
| `--exit` | Leave when finished. A smoke run always does; the switch is there to say so. |

Each line of output starts with `SMOKE`, and the exit code is 0 when the run did what it was asked to do
and 2 when it did not. A smoke run is a verification, so anything it was asked for and could not deliver -
a command line it could not read, a video or a lookup table that matches nothing, a chain that did not end
up holding what was asked for - is a failure with a message, never a warning it carries on past.

### Smoke runs on the two native heads

Every switch above works on the WinUI and WPF heads too, and the same pair of runs verifies a bake on them:

```
src\SimpleCbxVideoPlayer.Wpf\bin\Debug\net10.0-windows10.0.19041.0\SimpleCbxVideoPlayer.Wpf.exe ^
    --smoke MKV/landscape_hd.mkv --lut sepia_33.cube@40 --lut cool_33.cube@40 ^
    --bake chain.cube --snapshot chain.png --no-audio --exit
src\SimpleCbxVideoPlayer.Wpf\bin\Debug\net10.0-windows10.0.19041.0\SimpleCbxVideoPlayer.Wpf.exe ^
    --smoke MKV/landscape_hd.mkv --lut chain.cube@100 ^
    --snapshot baked.png --compare chain.png --no-audio --exit
```

Two details are particular to these heads, and both are deliberate:

**They go and find a console.** The CodeBrix.Platform heads are console-subsystem executables, so their
`SMOKE` lines land in the terminal that started them without anyone arranging it. A WinUI or WPF head is a
*windows*-subsystem executable and has no console at all, so each one calls `ConsoleHelper.AttachForSmokeRun`
as the first line of its `App` constructor: it attaches to the terminal it was launched from, or allocates
one, and rebinds `Console.Out`. It does this only when `--smoke` is on the command line - nobody wants a
console behind their video player.

**The WinUI head is unpackaged by default.** `SimpleCbxVideoPlayer.WinUI.csproj` sets
`WindowsPackageType` to `None`, which is what lets it be launched from a terminal with a command line at
all. Without it the Windows App SDK's auto-initializer never bootstraps the runtime for a launch that did
not come from an MSIX identity, and the process dies with `REGDB_E_CLASSNOTREG` before `Main` runs. The
property is conditional on being unset, so the "(Package)" launch profile and the single-project MSIX
packaging pass - which set it themselves - still win.
