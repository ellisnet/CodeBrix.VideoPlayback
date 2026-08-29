================================================================================
README: tests/assets/authoring
The sample-video corpus - real recordings, in the containers this program reads
================================================================================

This folder is NOT the golden corpus. The golden corpus lives one level up in
tests/assets: tiny synthetic files, made by FFmpeg's own generators, that the
container reader is measured against byte by byte. Read ASSETS.txt there for it.

What is here instead is REAL VIDEO: two phone recordings and the eighteen files
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
WebM/ and CodeBrix-Mode1/ is Public Domain as well. Nothing in this folder is
third-party media and nothing here carries a licence obligation.

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


Mode2/  -  COMING LATER
=======================
There is no Mode2 folder yet, and its absence is deliberate rather than an
oversight. Mode2 is the next .cbv profile, and it will be authored by the
CodeBrix authoring library rather than by a re-mux of somebody else's encoder
output. When it lands, its files will appear here beside the other three folders
and this README will describe them.


MANIFEST.txt
============
Written by the generator every time it runs. For each of the eighteen files it
records the resolution, the frame rate, the duration, the size, the video and
audio codecs, the exact encoder settings used, the result of probing the finished
file, and - for the Mode1 files - the full profile report. It also records what
was found in the two originals and the encoder-settings table with the reasoning
behind the numbers.

Read MANIFEST.txt before changing anything here, the way you would read ASSETS.txt
one level up.


REGENERATING EVERYTHING
=======================
    dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release

That rebuilds MKV/, WebM/, CodeBrix-Mode1/ and MANIFEST.txt from the two files in MP4/,
verifies every output, and exits non-zero if any check fails. It takes a couple of
minutes on a many-core machine. Useful switches:

    --dry-run              print every command line and produce nothing
    --only <folder>        rebuild only MKV, WebM or CodeBrix-Mode1
    --skip-profile-check   do not run cbvinfo over each finished file

WHAT IT NEEDS. FFmpeg and FFprobe on the PATH, built with libsvtav1 and libopus -
on a Debian-based machine, `sudo apt install ffmpeg` is enough; check with
`ffmpeg -encoders | grep -E 'libsvtav1|libopus'`. It also runs this repository's
own cbvinfo over each finished file, so build the solution first
(`dotnet build CodeBrix.VideoPlayback.slnx -c Release`); when the tool is not
there the profile check is recorded as "not run" and the encoding still happens.

The generator downloads nothing and installs nothing.

WHAT IS DETERMINISTIC AND WHAT IS NOT. The SHAPE of the corpus is: the same
eighteen files, the same resolutions, the same settings, the same command lines
every run. The BYTES are not - the Matroska muxer writes a random track UID and a
fresh muxing date into every file, so two runs differ even when the encoder made an
identical bitstream. Those fields are fixed-length, so the file sizes do tend to
come back the same; that is a convenience, not a guarantee. Do not pin these files
with a hash.


THE TESTS THAT READ THIS FOLDER
===============================
tests/CodeBrix.VideoPlayback.Tests/AuthoringCorpusTests.cs opens all eighteen
files with this repository's own Matroska reader and checks the track codecs, the
frame sizes, the declared duration, the presence of a seek index, and - for the
Mode1 files - that the cues really do precede the first cluster. It decodes
nothing: there is no AV1 decoder in this repository's tests, and the container is
what is being measured.

Those tests SKIP THEMSELVES, naming the folder and the command that fills it, when
the corpus has not been generated on this machine. A checkout without it still has
a green suite.
================================================================================
