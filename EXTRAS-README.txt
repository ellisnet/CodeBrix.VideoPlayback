================================================================================
EXTRAS-README: CodeBrix.VideoPlayback
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================


tools/CodeBrix.VideoPlayback.Tools
==================================
One console executable carrying four verbs. It is not packable and never
ships; it exists so that a build can be checked on a machine with no display and
no sound device, so that the bespoke container can be authored and inspected from
a shell, and so that a colour grade can be baked for the authoring pipeline.

    dotnet run --project tools/CodeBrix.VideoPlayback.Tools -c Release -- <verb> ...

It links the uncompressed video decoder in from the test project
(tests/CodeBrix.VideoPlayback.Tests/RawVideoDecoder*.cs) rather than duplicating
it, so cbvdecode can decode an uncompressed file with no codec package
installed. There is one copy of that code and it never reaches the NuGet package.


cbvinfo
-------
    cbvinfo <file> [--cues] [--packets] [--verify-checksums]

Reads either container flavour - a bespoke .cbv or a WebM/Matroska file - and
prints everything it can find without decoding anything: the header, every
track with its codec data, the caption tracks with their first few cues, the
chapters, the index or the cues, a per-track packet summary, and anything the
reader stepped over.

For an AV1 track it also parses the av1C record and prints the sequence header's
own view of the stream, which is the quickest way to see whether a file's
container metadata and its bitstream agree.

For an AUDIO track it adds one line saying whether anything in this process could
actually decode it:

    decoder available: yes (via the shared audio output)

That is asked with AudioDecoders.IsCodecSupported, which reads CodeBrix.Audio's
packet-codec registry WITHOUT starting the shared output, so cbvinfo still opens
no audio device and still runs on a machine with no sound card. An Opus track
reads "no" unless the tool's host has called CodeBrixAudioOpus.Register().

It finishes with the streamable profile report - the rules a file must meet to
open instantly over a network:

    video codec is AV1
    audio codec is Opus or Vorbis
    caption tracks are WebVTT
    cues sit before the first cluster        (or, for .cbv, the index is present
                                              and sits before the chunks)
    every element declares a known size
    the file states a duration
    timestamps ascend within every track
    video is 8-bit 4:2:0                     (a warning, not a failure)

Exit code 0 when the file passes, 1 when it does not or cannot be read, 2 for a
bad command line - so it can be used as a gate in a script.

    --cues                lists every index or cue entry rather than summarising
    --packets             lists every packet as it is read
    --verify-checksums    also checks the CRC-32 on each Matroska cluster, which
                          means reading the file twice


cbvdecode
---------
    cbvdecode --headless <file> [--y4m <out.y4m>] [--frames <n>] [--quiet]

Decodes every video frame and prints, per frame, its timestamp, size, layout,
key-frame flag, a truncated SHA-256 of its samples and how long it took; then
the totals, the mean and slowest frame times, the throughput, and a SHA-256 over
the whole decoded stream.

The stream hash is the point: two machines decoding the same file must produce
the same hash, which makes a cross-architecture build verifiable with no display
involved.

    --y4m <file>   also writes the decoded planes as a Y4M file, which FFmpeg and
                   every other tool can read
    --frames <n>   stop after n frames
    --quiet        totals only, no per-frame lines

Uncompressed video decodes with nothing installed. A coded stream needs a decoder
package registered; with none, the verb says so and exits 1.


cbvmux
------
    cbvmux --output <out.cbv> [--video <in.ivf>] [--audio <in.ogg>]
           [--chapters <chapters.ffmeta>] [--audio-language <bcp47>]
           [--audio-name <name>] [--video-name <name>]
           [--captions <path>:<bcp47>[:<name>[:default+forced+sdh]]] ...
           [--synthetic-video <frames>x<width>x<height>@<fps>]

Builds a bespoke .cbv file from an encoder's output: an IVF file holding an AV1
elementary stream, an Ogg Opus or Ogg Vorbis file, any number of WebVTT or SubRip
caption files, and a chapter file in FFmpeg's metadata format. The codec
configuration record is synthesised from the video's own sequence header, so
nothing has to be told what the video is.

--synthetic-video writes an uncompressed test clip instead, needing no encoder at
all. That is how tests/assets/raw-synthetic.cbv is made.


lutbake
-------
    lutbake --lut <file.cube>[@<percent>] [--lut ...] [--size <n>]
            [--interp tetrahedral|trilinear] [--domain <min> <max>]
            [--title <text>] -o <effective.cube>

Folds one or more ".cube" lookup tables into ONE effective table and writes it as
a ".cube" file. This is the AUTHORING HOOK: the file it writes is what the
authoring pipeline hands to FFmpeg.

    lutbake --lut film-stock.cube@70 --lut cool-shadows.cube -o effective.cube
    ffmpeg -i in.mov -vf lut3d=file=effective.cube ... out.mkv

Each --lut may carry "@<percent>" saying how much of that table to apply, 0 to
100; the DEFAULT IS 100, the whole table. The tables are applied IN ORDER, each
sampled at its own size and over its own declared domain, so swapping two of them
gives a different table. A table at 0 is skipped.

    --size    nodes a side of the result. The default is the largest size any of
              the tables has, never below 33 and never above 65.
    --interp  how each table is sampled between its own nodes. Tetrahedral is the
              default and is what FFmpeg's lut3d filter does; trilinear is what a
              graphics card's texture filter does.
    --domain  the input range of the RESULT. The default is 0 to 1, which is what
              a decoded picture and FFmpeg's raw video both carry. A table
              declaring some wider domain of its own is honoured either way, on
              the way in to ITS lookup.
    --title   the TITLE the written file states.

It uses the CORE package and nothing else - no drawing, no codec, no FFmpeg - and
it calls exactly the code the playback presenter calls to compose its own effect
chain, which is what makes the graded picture an application shows and the graded
video the pipeline encodes the same grade. Exit code 0 when the file was written,
1 when it could not be, 2 for a command line it did not understand.


tools/CodeBrix.VideoPlayback.AssetAuthoring
==========================================
    dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release

The second console executable, also not packable. It regenerates the SAMPLE-VIDEO
corpus under tests/assets/authoring - twenty-four files derived from two
Public-Domain phone recordings - and writes the manifest that describes them.

It is the counterpart to generate-assets.sh, and the difference is the point.
That script drives ffmpeg from a shell for the golden corpus. This tool builds
every command line through CodeBrix.VideoPlayback.Authoring, which renders the
arguments and hands them to CodeBrix.VideoProcessing to run; the tool never
invokes ffmpeg itself. It used to be the working PROTOTYPE of that authoring
library - CorpusEncoder.cs built its own ffmpeg arguments - and now it is the
library's reference CONSUMER: CorpusEncoder.cs turns a plan entry into a
VideoAuthoringRequest and nothing else. Every decision the prototype documented
survived as a setting.

    --dry-run              print every command line and produce nothing
    --only <folder>        re-encode only MKV, WebM, CodeBrix-Mode1 or
                           CodeBrix-Mode2. The manifest is still rewritten IN
                           FULL: every other folder is read back and re-verified
                           rather than re-encoded, so the manifest never
                           describes a corpus that is half stale.
    --skip-profile-check   do not judge each finished file against the profile
    --authoring-root <p>   use a folder other than the repository's own

Every finished file is read back and checked against the plan - codecs, exact
dimensions, frame rate, duration, and that no rotation side data survived - and
then judged against the streamable profile. The three FFmpeg-muxed folders are
read back with FFProbe.Analyse, which keeps them judged by an implementation
OTHER than the one that wrote them; CodeBrix-Mode2 cannot be, because a CBVF file
is not a container ffmpeg has ever heard of, so it is read back with this
repository's own container reader. Both results go into the manifest, and a
failure of either makes the tool exit non-zero.

The profile check no longer starts a process. The rules live in the playback
library as StreamableProfile, so the tool, the authoring library and the cbvinfo
verb all judge a file by the same code - and the corpus can be generated without
the tools project having been built at all.

It needs ffmpeg and ffprobe on the PATH, built with libsvtav1, libopus and
libvorbis. Nothing else: the CodeBrix-Mode2 container is written by managed code.
It downloads nothing and installs nothing.


tests/assets
============
The golden corpus: small synthetic media files, committed, that the whole test
suite is measured against. Nothing in it is third-party media - every source is
FFmpeg's own synthetic generators.

ASSETS.txt beside them is the manifest: for each file, the exact command that
produced it, its size, its SHA-256 and what it exercises. Read that before
changing anything here.

The container files, in outline:

    av1-opus.webm                    AV1 + Opus, cues BEFORE the first cluster
    av1-vorbis.webm                  AV1 + Vorbis, the large Xiph-laced setup headers
    av1-opus-cues-at-end.webm        the same content with its cues at the END,
                                     which forces the tail-range read over HTTP
    av1-opus.mkv                     the same streams remuxed by mkvmerge: EBML
                                     lacing, different clustering, voids, tags
    raw-opus.mkv                     V_UNCOMPRESSED video + Opus
    av1-opus-captions-chapters.mkv   a WebVTT caption track and a chapter edition,
                                     in FFmpeg's D_WEBVTT/SUBTITLES dialect
    webvtt-blockadditions.mkv        the OTHER WebVTT dialect: S_TEXT/WEBVTT with
                                     the settings in a BlockAddition
    lacing-xiph.mkv                  Xiph lacing   (block flags 0x82)
    lacing-ebml.mkv                  EBML lacing   (block flags 0x86)
    lacing-fixed.mkv                 fixed lacing  (block flags 0x84)
    lacing-vorbis.mkv                laced audio beside unlaced video
    av1-video-only.ivf               an AV1 elementary stream - muxer input, and
                                     the reference for av1C synthesis
    opus-audio.ogg, vorbis-audio.ogg the audio, as an encoder wrote it
    captions-en.vtt, srt-captions.srt, chapters.ffmeta    authoring inputs
    av1-opus.cbv, av1-vorbis.cbv     bespoke samples built by this repository's
                                     own muxer from the files above
    raw-synthetic.cbv                an uncompressed bespoke sample, decodable
                                     with no codec package at all

Beside each media file is <name>.probe.json - the output of

    ffprobe -v quiet -print_format json -show_format -show_streams \
            -show_chapters -show_frames <file>

recorded when the file was made. The tests compare the library's own reading of
each file against that recording, so the reader is measured against an
independent implementation rather than against itself.

Three honest caveats are recorded in ASSETS.txt and repeated here:
  * FFmpeg 7.1.5 cannot READ S_TEXT/WEBVTT, so webvtt-blockadditions.mkv's
    oracle records "unknown" for that stream. The track is valid; FFmpeg's
    Matroska table simply has no entry for it.
  * The FFmpeg-produced files are not byte-reproducible - FFmpeg writes random
    track UIDs and Ogg serial numbers. The mkvmerge ones are.
  * mkvmerge writes TimestampScale 20832 ns (1/48000 s) for the audio-only files
    rather than the usual 1000000. A reader that assumes 1000000 gets every
    timestamp in those three files wrong by a factor of 48; there is a test that
    would catch it.


generate-assets.sh
------------------
    tests/assets/generate-assets.sh

Rebuilds every FFmpeg- and mkvmerge-produced asset from scratch, re-records the
ffprobe oracles, and rewrites ASSETS.txt. It downloads nothing and needs only
ffmpeg, ffprobe and mkvmerge. It asserts the codecs it produced and exits
non-zero if any is wrong.


generate-cbv-assets.sh
----------------------
    tests/assets/generate-cbv-assets.sh

Rebuilds the .cbv samples through this repository's own muxer, then verifies them
with cbvinfo and cbvdecode. It needs only the .NET SDK - no FFmpeg, no mkvmerge -
which is the whole point of the bespoke format: authoring it requires nothing
that is not either an encoder or CodeBrix code.


tests/assets/authoring
======================
The SAMPLE-VIDEO corpus, and the one place in this repository that holds real
recordings rather than synthetic ones. It is not part of the golden corpus and no
byte-level assertion is made against it.

    MP4/     the two originals: a 4K landscape clip and a 4K portrait clip,
             created by Jeremy Ellis on his phone and placed by him in the Public
             Domain on 2026-08-29. Everything derived from them is Public Domain
             too. The portrait clip is stored LANDSCAPE with a -90 degree rotation
             in its metadata, which is what phones do and what players get wrong.
    MKV/     six off-the-shelf Matroska derivatives - AV1 + Opus, three
             resolutions by two orientations, muxed the way anything on the
             internet is muxed, cues at the END
    WebM/    the same six as WebM
    CodeBrix-Mode1/   the same six again as "CodeBrix Video Mode1" .cbv files: plain WebM
             with the cues moved to the FRONT, which is what the streamable
             profile is built around
    CodeBrix-Mode2/   the same six again as "CodeBrix Video Mode2" .cbv files: the
             BESPOKE CBVF container, AV1 + VORBIS, written by two ffmpeg passes
             and the playback library's own muxer. Vorbis rather than Opus
             because Mode2 is the flavour an application ships inside itself, and
             a Vorbis file plays with the core package alone

The portrait derivatives have TRUE portrait pixels - taller than they are wide,
with no rotation left for a player to apply. The generator asserts that and so do
the tests.

READ README.txt AND MANIFEST.txt in that folder before changing anything there.
README.txt is the Public-Domain declaration and the description of the five
folders; MANIFEST.txt is the per-file record, rewritten on every run.

The twelve off-the-shelf files are the NEGATIVE CONTROL for the profile: they
fail on "cues sit before the first cluster" and pass on everything else, which is
exactly the difference the Mode1 layout makes.

CodeBrix-Mode2 WAS GENERATED BY THE AUTHORING LIBRARY AND THE OTHER THREE FOLDERS
WERE NOT REGENERATED WHEN IT LANDED. That is deliberate, and it is checked: the
authoring library renders the same command text the committed manifest records,
byte for byte, and CorpusCommandEquivalenceTests says so. See MAINTAINER-README.txt.

tests/CodeBrix.VideoPlayback.Tests/AuthoringCorpusTests.cs reads all twenty-four
with this repository's own readers - track codecs, frame sizes, declared duration,
seek index, cues-before-first-cluster for Mode1, index-first and Vorbis for Mode2
- and decodes nothing, because there is no AV1 decoder in that project's tests.
tests/CodeBrix.VideoPlayback.Authoring.Tests DOES decode the six Mode2 files, with
the published Dav1d package. Both sets skip themselves, naming the folder and the
command that fills it, on a checkout that has not generated the corpus.


samples/CodeBrix.VideoPlayback.ConsumerShape
===========================================
    dotnet run --project samples/CodeBrix.VideoPlayback.ConsumerShape -c Release -- <out.png> [<in.cbv>]

The smallest honest application, and the one that proves the family's central
claim by being LOOKED AT rather than argued about.

It opens tests/assets/raw-vorbis.cbv - uncompressed video with Vorbis audio -
plays it right through with sound and no display, draws every frame into an
off-screen surface through SkiaVideoPresenter on the processor render path,
writes one PNG of what a view would have shown, prints the pixel values at a few
coordinates, and exits.

WHAT IT IS FOR is its publish output:

    dotnet publish samples/CodeBrix.VideoPlayback.ConsumerShape -c Release -o <folder>
    ls <folder>            # there is no CodeBrix.Audio.Opus.dll in it, and no libopus

An application that plays Vorbis-audio files carries the playback library, the
presenter, CodeBrix.Audio, SkiaSharp and their natives - and no Opus binary at
all. That is the check, and it is a directory listing rather than a promise.

WHAT IT REFERENCES, and why:

  * the two projects. A real application would reference the two PACKAGES; they
    are project references here because this sample lives in the repository that
    builds them.
  * SkiaSharp.NativeAssets.Linux, because it is an APPLICATION and applications
    choose their native assets. The presenter library deliberately does not -
    see hard rule 1a in MAINTAINER-README.txt.
  * the uncompressed decoder's two source files, LINKED from the test project
    exactly as the tools project links them, so the sample needs no codec
    package installed. A real application would reference a real decoder.


tests/CodeBrix.VideoPlayback.Tests
==================================
Not shipped, but worth reading as documentation: the uncompressed decoder
(RawVideoDecoder.cs and RawVideoDecoderFactory.cs) is the smallest complete
example of implementing IVideoDecoderFactory and IVideoDecoder, including
renting from the host's pool and publishing a reference-counted frame.

Two opt-in gates:

    CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1      the tests that open the audio device
    CODEBRIX_VIDEOPLAYBACK_RUN_BENCHMARKS=1  the colour-conversion benchmark


tests/CodeBrix.VideoPlayback.Skia.Tests
=======================================
Also worth reading: HeadlessGraphicsContext.cs gets a REAL graphics context on a
machine with no display, through Mesa's surfaceless EGL platform and four
P/Invokes to a library any machine with a graphics driver already has. Nothing is
installed. Where it cannot be arranged, the graphics tests skip themselves and
name the reason.

There is no opt-in gate here: the whole suite is device-free, because Skia's
runtime effects compile and run on its raster backend, so the colour shader is
measured against the core's own converter with no graphics device in the picture
at all.


tests/CodeBrix.VideoPlayback.Authoring.Tests
============================================
The third suite, and the only project in this repository that references a
DECODER package. It is in three layers, and the layering is the point:

  * ARGUMENT TESTS assert the exact ffmpeg command text a request renders, and
    the refusals a bad request earns. They touch no file, run no process and need
    no ffmpeg - which is what the authoring library's dry run exists for.
  * END-TO-END TESTS author real files from clips generated on the spot out of
    ffmpeg's own synthetic sources, then read them back with this repository's
    readers. Nothing here is third-party media and nothing depends on the
    sample-video corpus having been built. They SKIP, naming what was looked for,
    when ffmpeg is absent.
  * PLAYBACK TESTS open what was authored with CodeBrix.VideoPlayback.Dav1d
    registered and decode through to a frame count and a set of frame hashes -
    the gate that turns "the structure reads back" into "it is a video". The six
    CodeBrix-Mode2 corpus files are played here too, and skip when the corpus has
    not been generated.

It also references tools/CodeBrix.VideoPlayback.AssetAuthoring, for one test: that
the library still renders exactly the command lines the committed sample-video
manifest records. That is what lets the eighteen FFmpeg-muxed corpus files stand
unregenerated.

There is no opt-in gate here either. Nothing opens an audio device: what is
asserted about the sound is that a Vorbis track is decodable with the Opus package
never registered, and asking that question costs no device.


tests/assets/LUTs
=================
The .cube colour lookup tables the LUT effect is measured against, and the looks
the samples draw with. Twenty-three files, about 17.3 MB, none of which reaches any
NuGet package: they are data, read as text at run time.

    found/       nine tables from six open-source projects, every one CC0, a
                 public-domain dedication, or MIT, with the licence read at its
                 source and each file's own origin traced to the author of the
                 repository holding it.
    generated/   twelve tables written for this repository and placed in the
                 PUBLIC DOMAIN - identities at 17, 33 and 65, warm, cool, sepia,
                 an S curve, a deliberately unsubtle teal-and-orange for
                 screenshots, an exact inversion, a 1024-point 1D gamma curve,
                 a non-default-domain file and a CRLF-with-comments file.
    invalid/     two files that are broken ON PURPOSE and must be REFUSED. Do
                 not "fix" them; read invalid/DO-NOT-FIX.txt first.

LutCorpusTests walks every file in the folder - the valid ones to prove they
parse and survive a write-and-read round trip, the invalid ones to prove they are
refused by name - and skips itself when the folder is not in the checkout.

    python3 tests/assets/LUTs/generate-luts.py [output-folder]

That script writes everything in generated/. Python 3 standard library only -
nothing to install - and its output is deterministic and locale-independent, so
re-running it rewrites byte-identical files.

READ FIRST if you are adding a file here: tests/assets/LUTs/README.txt sets out
the licence bar, names every candidate that was REJECTED and why, and explains
why this program reads .cube and nothing else. MANIFEST.txt carries the per-file
provenance, hashes and validation results; LICENSES/ carries the verbatim licence
text of all six upstream projects.



samples/SimpleCbxVideoPlayer
============================
The application sample: an eight-head player that plays the sample-video corpus
(AV1 video with Opus or Vorbis sound, in Matroska, WebM and both .cbv flavours)
and grades the picture with a chain of .cube colour lookup tables while it
plays. Six CodeBrix.Platform heads (Linux X11, Wayland and frame buffer; macOS;
Windows Win32-Skia and WPF-Skia) and two native Windows heads (WinUI 3 and WPF)
share no user-interface code; all eight drive the same playback library through
one library, src/libs/SimpleCbxVideoPlayer.SkiaVideo, which owns every
VideoPlayback, .Skia, Dav1d and Opus reference plus the graphics-context seam.
It is the reference consumer of CodeBrix.VideoPlayback.Skia - the presenter for
hosts that are not CodeBrix.Platform applications - and it consumes the
PUBLISHED packages, not project references, so it builds the way an application
outside this repository would.

It carries no media of its own: at start-up it walks up from its folder looking
for tests/assets/authoring and plays what it finds, so run it from inside a
clone. Its own README.md (samples/SimpleCbxVideoPlayer/README.md) covers the two
solutions (the six-head one builds anywhere; the Windows one adds the WinUI and
WPF heads and needs -p:Platform=x64), running each head, the render-path
drop-down (GpuAuto / GpuNoFallback / Cpu), the LUT panel and the "bake the
current chain to .cube" button, and the scripted smoke verification.

================================================================================
