================================================================================
EXTRAS-README: CodeBrix.VideoPlayback
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================


tools/CodeBrix.VideoPlayback.Tools
==================================
One console executable carrying three verbs. It is not packable and never
ships; it exists so that a build can be checked on a machine with no display and
no sound device, and so that the bespoke container can be authored and inspected
from a shell.

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
================================================================================
