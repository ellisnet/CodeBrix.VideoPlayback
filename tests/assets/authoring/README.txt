================================================================================
README: tests/assets/authoring
The sample-video corpus - real recordings, in the containers this program reads
================================================================================

This folder is NOT the golden corpus. The golden corpus lives one level up in
tests/assets: tiny synthetic files, made by FFmpeg's own generators, that the
container reader is measured against byte by byte. Read ASSETS.txt there for it.

What is here instead is REAL VIDEO: two phone recordings and the twenty-four files
derived from them. They exist so that a player, a sample, a demonstration or a
performance measurement has something to open that looks and sounds like the
video people actually have - 4K, portrait and landscape, sound included - rather
than four seconds of a test pattern.

Everything here is Public Domain. See the next section.


MP4/  -  THE ORIGINALS
======================
    landscape_video_test_4k.mp4      3840x2160, H.264 + AAC, 30 fps, 4.03 s
    portrait_video_test_4k.mp4       stored 3840x2160 WITH ROTATION METADATA,
                                     displayed 2160x3840, H.264 + AAC, 30 fps,
                                     5.47 s

These two files were created by Jeremy Ellis on his phone, and placed by him in
the PUBLIC DOMAIN on 2026-08-29.

That declaration covers everything derived from them, so every file in MKV/,
WebM/, CodeBrix-Mode1/ and CodeBrix-Mode2/ is Public Domain as well. Nothing in
this folder is third-party media and nothing here carries a licence obligation.

A NOTE ON THE PORTRAIT FILE, because it is the reason it is here. It is stored
landscape - 3840 wide by 2160 tall - with a -90 degree rotation in its metadata,
which is what every phone does and what a great many players get wrong. Re-encoding
it BAKES the rotation in: the derived files have TRUE portrait pixels, taller than
they are wide, with no rotation left for anything downstream to have to apply. The
generator asserts that, and so do the tests.


MKV/ and WebM/  -  THE OFF-THE-SHELF DERIVATIVES
================================================
    landscape_4k     3840x2160        portrait_4k      2160x3840
    landscape_hd     1920x1080        portrait_hd      1080x1920
    landscape_720p   1280x720         portrait_720p    720x1280

Six files each: AV1 video (libsvtav1, 8-bit 4:2:0) with Opus audio (libopus,
48 kHz stereo), 30 frames per second, the full duration of the source.

These are deliberately ORDINARY. They are muxed the way anything on the internet
is muxed - FFmpeg's plain -f matroska and -f webm, with the muxer left to put the
cues wherever it puts them, which is at the END of the file. That is the point of
them: they are what a reader will actually be handed, and a reader that only
copes with a well-laid-out file is not finished.


CodeBrix-Mode1/  -  CODEBRIX VIDEO MODE1
===============================
    the same six files again, same resolutions, same codecs, extension .cbv

"CodeBrix Video Mode1" is a PROFILE, not a new container: a Mode1 file is a plain
WebM file that has been laid out so it can be opened and scrubbed immediately,
including over a network, and it is given the .cbv extension to say so. Any tool
that reads WebM reads these files unchanged.

What the profile guarantees, and what each guarantee buys: the video is AV1 and
the audio is Opus or Vorbis, so a player needs one royalty-free decoder pair and
no others; the seek index sits BEFORE the first cluster, so a reader that has the
first few kilobytes of the file already has the whole index and the first scrub
costs no second round trip; every element declares a known size, so nothing has to
be scanned to be skipped; the Info element states a duration, so a scrub bar can
be drawn before a single frame is decoded; timestamps ascend within every track,
so nothing has to be re-ordered; and the picture is 8-bit 4:2:0, which every
decoder and every display path handles without a conversion nobody asked for. The
cbvinfo verb checks all of that and exits non-zero when a file misses any of it:

    dotnet run --project tools/CodeBrix.VideoPlayback.Tools -c Release -- \
        cbvinfo tests/assets/authoring/CodeBrix-Mode1/landscape_hd.cbv

Run the same command against any file in MKV/ or WebM/ and it fails the "cues sit
before the first cluster" rule and passes the rest. That contrast is the whole
reason both sets are here.

Mode1 is the FIRST of the .cbv profiles. It carries plain audio and video only -
no captions, no chapters - which is what this corpus needs.


CodeBrix-Mode2/  -  CODEBRIX VIDEO MODE2
========================================
    the same six files again, same resolutions, same picture, extension .cbv

"CodeBrix Video Mode2" is the OTHER .cbv flavour, and unlike Mode1 it is not a
profile of somebody else's container: it is the BESPOKE format this repository
defines, byte for byte, in CBV-FORMAT.txt at the root. A Mode2 file begins with
the four bytes "CBVF" rather than with EBML's signature, and a reader tells the
two apart by looking at them. No other tool reads these files; that is the trade.

What the format buys, and why it is worth a format of its own: the index is in
front of the media data BY CONSTRUCTION rather than by asking a muxer nicely, so
there is no layout to get wrong; whole caption tracks and the whole chapter table
live in the header region, so every cue and every chapter title is available the
instant the header is read, including right after a seek; chapter titles are
MULTILINGUAL, which a WebM cannot manage; and a track's codec is named by an
identifier in its own header, so a new codec rides in without a format version
bump.

The video is the same AV1 as the other three folders. THE AUDIO IS VORBIS, NOT
OPUS, and that is the point of the folder rather than a detail of it: a Vorbis
track plays with the core playback package alone, while an Opus track needs the
application to reference CodeBrix.Audio.Opus and call its Register(). Mode2 is the
flavour an application ships INSIDE itself, so its convention is the one that
needs nothing extra. The bit rates are the same numbers as the Opus rungs, so the
two ladders stay comparable.

These six files are written by the CodeBrix authoring library rather than by a
re-mux of somebody else's encoder output: two ffmpeg passes - AV1 into an IVF
wrapper and Vorbis into an Ogg stream - and then this repository's own muxer,
which writes the container. Run cbvinfo over one of them and it passes the
profile on its own three layout rules rather than on Matroska's:

    dotnet run --project tools/CodeBrix.VideoPlayback.Tools -c Release -- \
        cbvinfo tests/assets/authoring/CodeBrix-Mode2/landscape_hd.cbv

Like Mode1, this folder carries plain audio and video only - no captions, no
chapters - which is what this corpus needs. The authoring library's own tests
exercise captions and multilingual chapters, on clips generated on the spot.


MANIFEST.txt
============
Written by the generator every time it runs. For each of the twenty-four files it
records the resolution, the frame rate, the duration, the size, the video and
audio codecs, the exact encoder settings used, the result of reading the finished
file back, the full streamable-profile report, and the exact ffmpeg command lines
that produced it - one for the FFmpeg-muxed folders, two for Mode2, plus what the
muxer made of them. It also records what was found in the two originals and the
encoder-settings table with the reasoning behind the numbers.

It always describes the WHOLE corpus, even after a run that rebuilt one folder:
the folders that were not re-encoded are read back and re-verified, their command
lines are re-derived from the plan, and their encode times are carried over from
the previous manifest.

Read MANIFEST.txt before changing anything here, the way you would read ASSETS.txt
one level up.


REGENERATING EVERYTHING
=======================
    dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release

That rebuilds MKV/, WebM/, CodeBrix-Mode1/, CodeBrix-Mode2/ and MANIFEST.txt from
the two files in MP4/, verifies every output, and exits non-zero if any check
fails. It takes a couple of minutes on a many-core machine. Useful switches:

    --dry-run              print every command line and produce nothing
    --only <folder>        re-encode only that folder - MKV, WebM,
                           CodeBrix-Mode1 or CodeBrix-Mode2. The manifest is
                           still rewritten in full; see MANIFEST.txt above.
    --skip-profile-check   do not judge each finished file against the profile

A NOTE ON NOT REGENERATING. CodeBrix-Mode2 was added on 2026-08-29 and the other
three folders were NOT rebuilt when it landed. That was deliberate: the generator
now builds its command lines through the CodeBrix authoring library rather than
by hand, and the library renders the same command text byte for byte, so the
eighteen files already here are still exactly what this manifest says they are.
A test checks that claim on every run of the suite. Rebuilding the whole corpus
is a decision to take deliberately, not a step to slip into a commit - the bytes
change even when nothing else does.

WHAT IT NEEDS. FFmpeg and FFprobe on the PATH, built with libsvtav1, libopus and
libvorbis - on a Debian-based machine, `sudo apt install ffmpeg` is enough; check
with `ffmpeg -encoders | grep -E 'libsvtav1|libopus|libvorbis'`. NOTHING ELSE:
the profile check runs in-process now, and the Mode2 container is written by
managed code.

The generator downloads nothing and installs nothing.

WHAT IS DETERMINISTIC AND WHAT IS NOT. The SHAPE of the corpus is: the same
twenty-four files, the same resolutions, the same settings, the same command lines
every run. The BYTES are not - the Matroska muxer writes a random track UID and a
fresh muxing date into every file, so two runs differ even when the encoder made an
identical bitstream. Those fields are fixed-length, so the file sizes do tend to
come back the same; that is a convenience, not a guarantee. Do not pin these files
with a hash.


THE TESTS THAT READ THIS FOLDER
===============================
tests/CodeBrix.VideoPlayback.Tests/AuthoringCorpusTests.cs opens all twenty-four
files with this repository's own readers - three folders with the Matroska reader
and CodeBrix-Mode2 with the bespoke one, because a CBVF file is not Matroska at
all - and checks the track codecs, the frame sizes, the declared duration, the
presence of a seek index, that the Mode1 cues really do precede the first cluster,
and that the Mode2 index really does sit in front of the chunks. It decodes
nothing: there is no AV1 decoder in that project, and the container is what is
being measured.

tests/CodeBrix.VideoPlayback.Authoring.Tests DOES decode the six Mode2 files, with
the published Dav1d decoder package, and checks that their Vorbis sound needs no
Opus package registered.

Those tests SKIP THEMSELVES, naming the folder and the command that fills it, when
the corpus has not been generated on this machine. A checkout without it still has
a green suite.
================================================================================
