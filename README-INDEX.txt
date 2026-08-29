================================================================================
README-INDEX: CodeBrix.VideoPlayback
Map of the README files in this repository
================================================================================

If you are an AI coding agent: find the NuGet package you are consuming below
and read its AGENT-README file in full. Read MAINTAINER-README.txt only if you
are changing this repository itself.

AGENT-README FILES (consumer documentation, one per NuGet package)
------------------------------------------------------------------
  AGENT-README.txt
      CodeBrix.VideoPlayback.MitLicenseForever - Royalty-free video playback for
      .NET: reads WebM, Matroska and .cbv containers, demultiplexes AV1 video
      with Opus or Vorbis audio plus captions and chapters, and drives a
      codec-neutral decoder seam, a zero-copy frame pool, a frame presenter and
      a managed SIMD colour converter. No native binaries, no drawing
      dependency, one NuGet dependency.

  src/CodeBrix.VideoPlayback.Skia/AGENT-README.txt
      CodeBrix.VideoPlayback.Skia.MitLicenseForever - Draws the frames the
      playback library decodes, through SkiaSharp. One class, SkiaVideoPresenter:
      it composes the newest frame on an off-screen surface - on the graphics
      device through one shader that does colour conversion and a resultant
      colour lookup table in a single pass, or on the processor through the
      core's vector converter - lets the application draw over it, and blits the
      result into whatever canvas the application owns. Plain SkiaSharp only: no
      view package, no native asset, no windowing dependency.

MAINTAINER AND EXTRAS
---------------------
  MAINTAINER-README.txt
      Building, testing, packaging, versioning and provenance notes for
      maintainers of BOTH packages, plus the design notes: the buffer-pool
      contract, frame reference counting and fences, session threading, the
      clock, seeking, audio trimming and pre-roll, HTTP behaviour, the
      presenter's two render paths and its shader, and the benchmark numbers.
  EXTRAS-README.txt
      The headless tools (cbvinfo, cbvdecode, cbvmux), the consumer-shape
      sample, the golden test-asset corpus and the scripts that regenerate it.

FORMAT SPECIFICATIONS
---------------------
  CBV-FORMAT.txt
      The bespoke ".cbv" container, version 0, byte by byte - and every
      deviation from the draft layout it was finalised from.

GENERAL
-------
  README.md
      Human-facing overview shown on GitHub and nuget.org.
  README-INDEX.txt
      This file.
  THIRD-PARTY-NOTICES.txt
      What came from where. Nothing is vendored; the containers are written from
      published specifications.
================================================================================
