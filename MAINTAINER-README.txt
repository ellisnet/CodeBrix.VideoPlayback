================================================================================
MAINTAINER-README: CodeBrix.VideoPlayback
Notes for people and agents MAINTAINING this repository - not for package
consumers
================================================================================

If you are CONSUMING the NuGet package, read AGENT-README.txt instead. Nothing
in this file is needed to use the package.


PURPOSE AND SCOPE
=================
This repository will produce one NuGet package:

  CodeBrix.VideoPlayback.MitLicenseForever
      License:       MIT
      Consumer doc:  AGENT-README.txt (repo root)

Video playback for .NET: WebM/MKV and .cbv containers carrying AV1 video with Opus or Vorbis audio, a codec-neutral decoder seam, and a Skia presenter companion package.

STATUS (2026-08-28): repository created; the library source has not been
scaffolded yet. The standard repository files (this file, EXTRAS-README.txt,
README-INDEX.txt, AGENT-README.txt, the eight AI-agent pointer stubs,
icon-codebrix-128.png, global.json, THIRD-PARTY-NOTICES.txt) are in place so
the scaffold lands into a repository that already follows the family
conventions.

PLANNED PACKAGES IN THIS REPOSITORY: the core package above, a Skia presenter
companion (CodeBrix.VideoPlayback.Skia.MitLicenseForever) and a .cbv authoring
package that drives FFmpeg through CodeBrix.VideoProcessing; the core never
references Skia, FFmpeg, or the Opus/AV1 codec packages.

BUILDING
========
Nothing to build yet. When the scaffold lands: dotnet build <solution>.slnx.

TESTING
=======
Nothing to test yet. global.json already selects the Microsoft.Testing.Platform
runner (xunit.v3), matching the rest of the family.

PACKAGING / PUBLISHING
======================
Jeremy packs and publishes; the package id and license are fixed above. The
csproj will pack AGENT-README.txt, README.md, icon-codebrix-128.png, LICENSE
and THIRD-PARTY-NOTICES.txt at the package root, as every family package does.

PROVENANCE / VENDORED SOURCES
=============================
See THIRD-PARTY-NOTICES.txt at the repository root; it is the authoritative
record of what came from where.

NOTES
=====
- The eight AI-agent pointer stubs point at README-INDEX.txt (the family's
  2026-08-24 convention). Do not let a scaffold overwrite them with the older
  "read AGENT-README.txt" wording.
- The .slnx, when created, must list AGENT-README.txt, EXTRAS-README.txt,
  MAINTAINER-README.txt, README-INDEX.txt, README.md, LICENSE,
  THIRD-PARTY-NOTICES.txt and icon-codebrix-128.png under Solution Items.
================================================================================
