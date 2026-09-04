# CodeBrix.VideoPlayback

Royalty-free video playback for .NET, with no native binaries, no drawing dependency and exactly one NuGet
dependency. It reads **WebM and Matroska** files carrying AV1 video with Opus or Vorbis audio and any number of
text caption tracks, and reads and writes **`.cbv`** - a bespoke container laid out so that the whole index and
every caption cue sit in front of the media data, which is what makes a shipped-with-the-app clip start
instantly and a seek cost one read. CodeBrix.VideoPlayback is provided as three .NET 10 libraries and their
associated NuGet packages: `CodeBrix.VideoPlayback.MitLicenseForever` (playback),
`CodeBrix.VideoPlayback.Skia.MitLicenseForever` (a presenter that draws the frames) and
`CodeBrix.VideoPlayback.Authoring.MitLicenseForever` (a developer-machine library that writes the files the
first one reads).

CodeBrix.VideoPlayback supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.VideoPlayback.MitLicenseForever
dotnet add package CodeBrix.VideoPlayback.Skia.MitLicenseForever
dotnet add package CodeBrix.VideoPlayback.Authoring.MitLicenseForever
```

**Which one do I reference?** An application that PLAYS video takes `CodeBrix.VideoPlayback.MitLicenseForever`,
and adds `CodeBrix.VideoPlayback.Skia.MitLicenseForever` when it would rather have frames drawn for it than
draw them itself. A build tool or an asset pipeline that WRITES video takes
`CodeBrix.VideoPlayback.Authoring.MitLicenseForever` - and only there: that one drives the FFmpeg installed on
the authoring workstation, so it belongs in a build tool or an asset pipeline and never in a shipped
application. The three are published together at one version.

Note that the NuGet package IDs and the namespaces are different - there is no package named plain
`CodeBrix.VideoPlayback`:

* NuGet package ID `CodeBrix.VideoPlayback.MitLicenseForever` - assembly and primary namespace
  `CodeBrix.VideoPlayback` - i.e. `using CodeBrix.VideoPlayback;`
* NuGet package ID `CodeBrix.VideoPlayback.Skia.MitLicenseForever` - assembly and primary namespace
  `CodeBrix.VideoPlayback.Skia`
* NuGet package ID `CodeBrix.VideoPlayback.Authoring.MitLicenseForever` - assembly and primary namespace
  `CodeBrix.VideoPlayback.Authoring`

XML documentation (IntelliSense) ships alongside every assembly.

Each package pulls in what it needs automatically; no version pinning is needed in the consuming project:

* `CodeBrix.VideoPlayback.MitLicenseForever` pulls in `CodeBrix.Audio.MitLicenseForever`, which plays the
  sound and has Vorbis built in. That is the whole list - no native binary, no drawing dependency.
* `CodeBrix.VideoPlayback.Skia.MitLicenseForever` pulls in the playback package and `SkiaSharp`. It
  deliberately does NOT bring a SkiaSharp native-asset package: a consuming application adds the
  `SkiaSharp.NativeAssets` package for each platform it ships on, and so chooses its own native binary.
* `CodeBrix.VideoPlayback.Authoring.MitLicenseForever` pulls in the playback package and
  `CodeBrix.VideoProcessing.MitLicenseForever`, which is how it launches the FFmpeg installed on the machine
  that runs it.

Add, as your application needs them:

* `CodeBrix.VideoPlayback.Dav1d.BsdLicenseForever` - to play AV1 video. It binds the dav1d AV1 decoder and
  carries native binaries, which is why it is a separate package.
* `CodeBrix.Audio.Opus.BsdLicenseForever` - to play Opus audio.

## CodeBrix.VideoPlayback supports:

* A playback session with a transport, a clock, A/V sync, seeking, looping, captions and chapters
* Reading WebM and Matroska files carrying AV1 video with Opus or Vorbis audio, plus any number of text
  caption tracks
* Reading and writing `.cbv`, the bespoke container, with its index and every caption cue in front of the
  media data
* Playing from files, streams, memory, memory-mapped files and HTTP
* A frame-buffer pool whose layout lets a decoder write straight into the memory a presenter uploads from -
  no copy in between, and nothing allocated per frame once playback is warm
* A frame presenter: a one-slot mailbox that always holds the newest frame, so the drawing side never waits
  and never shows a stale picture
* A managed SIMD converter from planar YUV to BGRA, for when there is no graphics device to do it
* Colour lookup tables - 1D and 3D, `.cube` reading and writing, and layered chains composed into one
  resultant table
* Container readers for EBML, Matroska/WebM and `.cbv`, and writers for `.cbv`, IVF and Ogg
* A codec-neutral decoder seam that decoder packages register themselves into
* A streamable-profile checker that says whether a file really is laid out the way a `.cbv` promises

## CodeBrix.VideoPlayback.Skia supports:

* One class, `SkiaVideoPresenter`, that composes the newest frame on an off-screen surface and blits the
  result into whatever canvas your application owns
* A graphics-device path: one SkSL shader doing the colour conversion and a whole chain of colour effects in
  a single pass
* A processor path through the core's converter, and a choice of which you get - `GpuAuto`,
  `GpuNoFallback` or `Cpu`
* Letterboxing: `Uniform`, `UniformToFill`, `Fill` or `None`
* Drawing over the video - a `Composing` event that hands you the canvas, and `IVideoLayer` overlay layers
* Colour effects, including lookup-table chains, and the resultant table they compose to
* Capturing the composed frame as an `SKImage`
* Composition statistics and frame counters
* Plain SkiaSharp only: no view package, no native asset, no windowing toolkit

## CodeBrix.VideoPlayback.Authoring supports:

* One call that turns a source video, plus its caption files and a chapter file, into a `.cbv` - in either
  flavour: one FFmpeg pass for the WebM-profile one, two passes and this repository's own muxer for the
  bespoke one
* Frame sizes by source, exact size, long side or short side, with rate control and encoder settings
* Device-class presets, applied to a request in one call
* A colour grade baked from a chain of `.cube` tables with the same composer the presenter uses
* A dry run that renders every command line without running anything
* A streamable-profile report over the finished file
* Refusing, before any process starts, what would not play or would not encode: Opus in a bespoke file,
  SubRip in a WebM-profile file, a Vorbis bit rate outside the encoder's band, a malformed language tag

## Decoders

One video decoder ships in the box: the uncompressed one, for Matroska's `V_UNCOMPRESSED` tracks and the
`.cbv` files built from them. It is registered for you, so that container, session, clock, seek, pool and
presenter all work with no codec package installed at all - it is a diagnostics and test codec, not a
distribution format.

A decoder for a CODED format arrives as a separate package and registers itself; audio plays through
[CodeBrix.Audio](https://github.com/ellisnet/CodeBrix.Audio), which has Vorbis built in. A file whose codec has
no decoder fails with a message naming the package to add.

## The two `.cbv` flavours

A WebM-profile file is an ordinary WebM document - or a Matroska one, if you ask for that - with its cues
moved to the front, so anything that reads WebM can read it too, and Opus is its default sound.

A bespoke `.cbv` is this repository's own container, and it exists so that a file plays with the playback
package and a video decoder and NOTHING else - so its sound is Vorbis, which the one audio dependency has
built in. A bespoke file never carries Opus, and a request that asks for one is refused rather than written.

## Sample Code

### Play a file

```csharp
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Frames;

VideoPlaybackSession session = new VideoPlaybackSession();
session.FrameReady += (s, e) => myView.Invalidate();
session.Open("clip.cbv");
session.Play();

// ...on the thread that draws:
if (session.Presenter.TryTakeLatest(out VideoFrame frame))
{
    using (frame)
    {
        // frame.Y / frame.U / frame.V are the planes, with their colour metadata.
    }
}
```

### Have the frames drawn for you

```csharp
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Rendering;
using CodeBrix.VideoPlayback.Skia;
using SkiaSharp;

VideoPlaybackSession session = new VideoPlaybackSession();
SkiaVideoPresenter presenter = new SkiaVideoPresenter();

presenter.Attach(session.Presenter);
presenter.Invalidated += (s, e) => myView.Invalidate();   // mark dirty; do NOT draw here

session.Open("clip.cbv");
session.Play();

// ...in whatever your host framework calls to paint:
void Paint(SKCanvas canvas, SKRect bounds)
{
    canvas.Clear(SKColors.Black);
    presenter.Draw(canvas, bounds, VideoStretch.Uniform);
}
```

### Author a `.cbv`

```csharp
using CodeBrix.VideoPlayback.Authoring;
using CodeBrix.VideoPlayback.Authoring.Presets;

VideoAuthoringRequest request = new VideoAuthoringRequest
{
    Flavour = VideoAuthoringFlavour.Bespoke,
    SourcePath = "master.mov",
    OutputPath = "intro.cbv",
};

DeviceClassPresets.Pi1080p.ApplyTo(request);

VideoAuthoringResult result = CbvAuthor.Write(request);
Console.WriteLine(result);          // path, size, flavour, profile verdict
```

The same request renders its command lines without running anything, which is what a build script records:

```csharp
using CodeBrix.VideoPlayback.Authoring.Commands;

foreach (AuthoringCommand command in CbvAuthor.RenderCommands(request))
{
    Console.WriteLine(command);
}
```

### Look inside a file

```
dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvinfo clip.webm
dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvdecode --headless clip.cbv
```

`cbvinfo` prints the tracks, the index, the captions and the chapters, and checks the file against the
streamable profile rules. `cbvdecode` decodes every frame to a hash and a timing summary with no display and
no sound device - which is how a build is verified on a small board or a build agent.

## Documentation

Every one of these NuGet packages includes `AGENT-README.txt`, a complete API reference and usage guide
written for AI coding agents - point your agent at the file inside the package it is writing code against:

* `CodeBrix.VideoPlayback.MitLicenseForever` - the playback library's guide: the session, the pool, the
  presenter mailbox, the converter, the containers, ten worked examples, performance notes and the pitfalls.
* `CodeBrix.VideoPlayback.Skia.MitLicenseForever` - the presenter's guide: the two render paths, the
  composition surface, the effect chain, and ten more worked examples.
* `CodeBrix.VideoPlayback.Authoring.MitLicenseForever` - the authoring library's guide: the two flavours,
  every encoder setting, the device-class table, the colour-grade hook, and the two things a WebM-profile
  file cannot carry.

Sound is played by the sibling `CodeBrix.Audio.MitLicenseForever` package; read its own `AGENT-README.txt`
for the audio side.

In the repository there is also
[CBV-FORMAT.txt](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/CBV-FORMAT.txt), the bespoke
container byte by byte;
[EXTRAS-README.txt](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/EXTRAS-README.txt), the
headless tools, the samples and the test corpora;
[MAINTAINER-README.txt](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/MAINTAINER-README.txt),
the design notes and how to build, test and package it; and
[README-INDEX.txt](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/README-INDEX.txt), the map of
all of them.

Additional sample code and usage examples are available in the `CodeBrix.VideoPlayback.Tests` project:
https://github.com/ellisnet/CodeBrix.VideoPlayback/tree/main/tests/CodeBrix.VideoPlayback.Tests

The presenter and the authoring library have suites of their own beside it,
`CodeBrix.VideoPlayback.Skia.Tests` and `CodeBrix.VideoPlayback.Authoring.Tests`.

## License

CodeBrix.VideoPlayback is licensed under the MIT License, all three packages - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.VideoPlayback/blob/main/THIRD-PARTY-NOTICES.txt).
