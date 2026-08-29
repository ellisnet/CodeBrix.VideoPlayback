# SimpleCbxVideoPlayer

A six-head CodeBrix.Platform application that plays the sample videos in this repository - AV1 video with
Opus sound, in Matroska, WebM and the bespoke `.cbv` container - and grades the picture with a chain of
`.cube` colour lookup tables while it plays.

It exists to show three things working together in a real application, rather than in a console program:

1. **CodeBrix.VideoPlayback** demultiplexing a file and pacing decoded frames to a clock.
2. **CodeBrix.VideoPlayback.Skia** composing those frames - on the graphics device through one shader, or
   on the processor through the core's vector converter - and blitting the result into the canvas a
   CodeBrix.Platform page owns.
3. **CodeBrix.VideoPlayback.Dav1d** and **CodeBrix.Audio.Opus**, registered once at start-up, supplying the
   two decoders that nothing in this family discovers by reflection.

This is a DEVELOPMENT-REPOSITORY sample. It carries no media of its own: at start-up it walks up from its
own folder looking for a directory that holds `tests/assets/authoring`, and plays what it finds there. Run
it from inside a clone of this repository, or its drop-downs will be empty and it will say so.

---

## Running it

Every head builds from the solution in this folder:

```
dotnet build SimpleCbxVideoPlayer.slnx -c Release
```

Then run the head for the machine you are on:

| Head | Command |
|---|---|
| Linux X11 | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxX11 -c Release` |
| Linux Wayland | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxWayland -c Release` |
| Linux frame buffer | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxFrameBuffer -c Release` (from a text console, not from inside a desktop session) |
| macOS | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.MacOS -c Release` |
| Windows, Win32-Skia | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.Win32Skia -c Release` |
| Windows, WPF-Skia | `dotnet run --project src/CodeBrixPlatform/SimpleCbxVideoPlayer.WinWpfSkia -c Release` |

Only the X11 head has been run and verified on the machine this sample was written on. The other five are
built by the same solution build and share every line of their code with it.

A note on the solution build: the two video projects this sample references live OUTSIDE its solution, so a
`.slnx` build compiles them in their own default configuration. Build a head project directly
(`dotnet build src/CodeBrixPlatform/SimpleCbxVideoPlayer.LinuxX11 -c Release`) when you want the video
libraries built in Release as well.

---

## The three controls

**The video drop-down** lists every playable file found under `tests/assets/authoring`. The rule is a rule,
not a list: every sub-folder is read except `MP4/`, and every `.mkv`, `.webm` and `.cbv` file in it is
offered. So `MKV/`, `WebM/` and `CodeBrix-Mode1/` appear today - eighteen files - `MP4/` never does, because
nothing in this family reads an ISOBMFF file, and a `CodeBrix-Mode2/` folder added later will appear without
a line of code changing.

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
- **The panel greys out whenever the picture is being composed on the processor**, whatever the transport is
  doing, with a note saying why. This application deliberately leaves `AllowEffectsOnCpu` alone, so a grade
  is a graphics-path feature here; `EffectsActive` is the property that tells the truth, and the status line
  shows it.
- Ticked tables are applied **in list order** - the order of the list, not the order you ticked them - and
  order matters: the second table sees what the first produced.
- A percentage is clamped to 0-100 and committed when the box loses focus. A table at 0 costs nothing.
- The whole chain is composed into ONE lookup table when it is applied - never per frame. Ten tables cost
  what one costs.

So the enablement is a small matrix:

| | Stopped | Paused | Playing |
|---|---|---|---|
| **GPU path** | editable | editable | read-only |
| **CPU path** | read-only | read-only | read-only |

### Bake chain to .cube

Under the list is a **Bake chain to .cube** button. It writes the chain that is ON SCREEN - the one applied
at the last Play - out as a single `.cube` file, so a grade dialled in by eye can be handed to FFmpeg, to a
colour-grading tool, or back to this application. The table it writes is the presenter's own resultant
lookup table: the very table the shader is sampling, at the same size and with the same interpolation, so
the file and the picture cannot disagree.

The button is enabled only when there is a chain, it is actually being applied (so: the GPU path), and there
is a picture for it to be applied to (playing or paused). The file goes into a `baked-luts` folder beside
the application, under a name stamped with the moment it was made, and the full path appears under the
button - V1 has no file picker here on purpose. The `TITLE` line names the chain, for example
`SimpleCbxVideoPlayer: cool_33@40 + sepia_33@40`.

---

## What supplies the graphics context

`SKXamlCanvas`, from `CodeBrix.Platform.SkiaSharp.Views`, paints Skia on the processor and cannot hand out a
`GRContext` (its `SKSwapChainPanel` is a non-functional placeholder on the CodeBrix.Platform heads). So the
page also builds a `SkiaGLCanvasElement` from `CodeBrix.Platform.Graphics3DGL`, which creates an offscreen
OpenGL context and gives each paint a GPU-backed `SKSurface` **and** the `GRContext` behind it. That context
is what the presenter is handed, and it is what makes the GPU render path - and therefore the lookup tables -
real on these heads.

The page decides between the two canvases shortly after it loads: when GPU Skia starts, the graphics canvas
is the one painted and the processor canvas is collapsed; when it does not, the page falls back to
`SKXamlCanvas`, tells the player it has no context, and the status line and the lookup-table panel say so.
Both render paths draw correctly into whichever canvas is showing.

---

## How the projects are arranged

```
src/libs/SimpleCbxVideoPlayer.SkiaVideo/   the ONLY project that names the video packages
src/CodeBrixPlatform/…Core/                view models; sees SkiaVideo's types and nothing else
src/CodeBrixPlatform/…UI/                  App.xaml and the page, shared by every head
src/CodeBrixPlatform/…<Head>/              one project per platform
tests/libs/…SkiaVideo.Tests/               device-less tests for everything above the presenter
```

`SimpleCbxVideoPlayer.SkiaVideo` is the seam, on purpose: the application talks to a
`VideoPlaybackController` (open, play, pause, stop, seek, "draw into this canvas", "here is a graphics
context", "apply this chain of tables") and never names a type from CodeBrix.VideoPlayback,
CodeBrix.VideoPlayback.Skia, CodeBrix.VideoPlayback.Dav1d or CodeBrix.Audio.Opus. A WinUI or WPF version of
this sample can reuse that library unchanged.

---

## Temporary: the local package folder

The AV1 decoder package this sample needs, `CodeBrix.VideoPlayback.Dav1d.BsdLicenseForever`, is not published
yet. Until it is, it is packed from its own repository into a folder that `Directory.Build.props` adds as an
extra restore source:

```
dotnet pack ~/GitHome/CodeBrix.VideoPlayback.Dav1d/src/CodeBrix.VideoPlayback.Dav1d/CodeBrix.VideoPlayback.Dav1d.csproj \
    -c Release -o ~/ClaudeHome/localfeed_codebrix_videoplayback_2026-08-29/
```

No `nuget.config` is committed and the machine's own NuGet configuration is untouched. **Delete
`Directory.Build.props` and pin the published version in `SimpleCbxVideoPlayer.SkiaVideo.csproj` as soon as
the package is on nuget.org.**

The playback library and its Skia presenter are project references for the same reason the
`CodeBrix.VideoPlayback.ConsumerShape` sample uses them: this sample lives in the repository that builds
them. A standalone application would reference the two packages instead.

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
| `--bake <path.cube>` | Write the applied chain out as one `.cube` file. Fails the run if there is no chain to write. |
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
