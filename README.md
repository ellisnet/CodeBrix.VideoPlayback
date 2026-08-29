# CodeBrix.VideoPlayback

Royalty-free video playback for .NET 10, with no native binaries, no drawing
dependency, and exactly one NuGet dependency.

It reads **WebM and Matroska** files carrying AV1 video with Opus or Vorbis audio
and any number of text caption tracks, and **`.cbv`** — a bespoke container this
library writes and reads, laid out so that the whole index and every caption cue
sit in front of the media data, which is what makes a shipped-with-the-app clip
start instantly and a seek cost one read.

What it gives you is the machinery around a codec, and no codec:

- a **playback session** with a transport, a clock, A/V sync, seeking, looping,
  captions and chapters;
- a **frame-buffer pool** whose layout lets a decoder write straight into the
  memory a presenter uploads from — no copy in between, and nothing allocated
  per frame once playback is warm;
- a **frame presenter**: a one-slot mailbox that always holds the newest frame,
  so the drawing side never waits and never shows a stale picture;
- a **managed SIMD converter** from planar YUV to BGRA, for when there is no
  graphics device to do it;
- **container readers** for EBML, Matroska/WebM and `.cbv`, written clean-room
  from the published specifications;
- an **authoring muxer** that turns an encoder's IVF and Ogg output, plus caption
  and chapter files, into a `.cbv`.

The video decoder itself arrives as a separate package and registers itself;
audio plays through [CodeBrix.Audio](https://github.com/ellisnet/CodeBrix.Audio).
A file whose codec has no decoder fails with a message naming the package to add.

## The presenter

This repository also produces **`CodeBrix.VideoPlayback.Skia`**, which draws the
frames the library above decodes. One class, `SkiaVideoPresenter`: it composes the
newest frame on an off-screen surface — on the graphics device through a single
SkSL shader that does the colour conversion and a whole chain of colour effects in
one pass, or on the processor through the converter above — lets your application
draw over it, and blits the result into whatever canvas your application owns,
letterboxed however you asked. It depends on plain SkiaSharp and nothing else: no
view package, no native asset, no windowing toolkit.

The two packages are separate so that the choice stays yours. An application that
draws frames itself, or runs where Skia does not, takes the first alone.

## Installing

```
dotnet add package CodeBrix.VideoPlayback.MitLicenseForever
dotnet add package CodeBrix.VideoPlayback.Skia.MitLicenseForever   # optional: the presenter
```

## Playing something

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

## Looking inside a file

```
dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvinfo clip.webm
dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvdecode --headless clip.cbv
```

`cbvinfo` prints the tracks, the index, the captions and the chapters, and checks
the file against the streamable profile rules. `cbvdecode` decodes every frame to
a hash and a timing summary with no display and no sound device — which is how a
build is verified on a small board or a build agent.

## Documentation

- **[AGENT-README.txt](AGENT-README.txt)** — the playback library's consumer
  guide: the API, ten worked examples, performance tips and the pitfalls.
- **[src/CodeBrix.VideoPlayback.Skia/AGENT-README.txt](src/CodeBrix.VideoPlayback.Skia/AGENT-README.txt)**
  — the presenter's consumer guide: the two render paths, the composition
  surface, the effect chain, and ten more worked examples.
- **[CBV-FORMAT.txt](CBV-FORMAT.txt)** — the bespoke container, byte by byte.
- **[MAINTAINER-README.txt](MAINTAINER-README.txt)** — design notes and how to
  build, test and package this repository.
- **[EXTRAS-README.txt](EXTRAS-README.txt)** — the tools, the consumer-shape
  sample and the test corpus.
- **[README-INDEX.txt](README-INDEX.txt)** — the map of all of the above.

## Licence

MIT, both packages. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) — nothing is vendored, and
every container reader and every line of the colour shader here was written from
its published specification.
