================================================================================
MAINTAINER-README: CodeBrix.VideoPlayback
Notes for people and agents MAINTAINING this repository - not for package
consumers
================================================================================

If you are CONSUMING the NuGet package, read AGENT-README.txt instead. Nothing
in this file is needed to use the package.


PURPOSE AND SCOPE
=================
This repository produces THREE NuGet packages:

  CodeBrix.VideoPlayback.MitLicenseForever
      License:       MIT
      Consumer doc:  AGENT-README.txt (repo root)
      Dependency:    CodeBrix.Audio.MitLicenseForever, and nothing else

  CodeBrix.VideoPlayback.Skia.MitLicenseForever
      License:       MIT
      Consumer doc:  src/CodeBrix.VideoPlayback.Skia/AGENT-README.txt
      Dependencies:  CodeBrix.VideoPlayback.MitLicenseForever (same version) and
                     plain SkiaSharp, and nothing else

  CodeBrix.VideoPlayback.Authoring.MitLicenseForever
      License:       MIT
      Consumer doc:  src/CodeBrix.VideoPlayback.Authoring/AGENT-README.txt
      Dependencies:  CodeBrix.VideoPlayback.MitLicenseForever (same version) and
                     CodeBrix.VideoProcessing.MitLicenseForever, and nothing else

The first is the Skia-free core of a video-playback family: containers, a
playback session, a frame-buffer pool, a frame presenter, a CPU colour converter
and an authoring muxer. It contains no video codec and no drawing surface, on
purpose.

The second draws what the first decodes, and is THE ONE SANCTIONED EXCEPTION to
the family's "no Skia outside the platform library" rule. It is a separate
package precisely so that the rule holds everywhere else: an application that
draws frames itself, or on a platform Skia does not reach, takes the core alone.

The third WRITES what the first reads. It drives the ffmpeg installed on the
authoring machine, through CodeBrix.VideoProcessing, and produces ".cbv" files in
both flavours - the WebM-profile one in a single ffmpeg pass, the bespoke one in
two passes plus the core's own muxer. It is a DEVELOPER-MACHINE package: it has
no place in a shipped application, and nothing an application needs to PLAY video
depends on it. It is published here rather than in the video-processing
repository because the muxer and the container knowledge live here (plan decision
21: no .cbv creation code in CodeBrix.VideoProcessing).

All THREE projects carry the SAME date-stamped version block, so one pack run
stamps them with one version and the dependency the presenter and the authoring
library each declare on the core is that same number. THEY ARE PUBLISHED
TOGETHER, as one event, at one version - core, .Skia and .Authoring.

Planned siblings, elsewhere:

  an AV1 decoder package                          a separate repository, with a
      different licence and native binaries. It implements this library's
      IVideoDecoderFactory / IVideoDecoder and registers itself.
  a platform video-player element                 a separate repository, built on
      the Skia presenter.


HARD RULES FOR THIS REPOSITORY
==============================
1. THE CORE REFERENCES CodeBrix.Audio.MitLicenseForever AND NOTHING ELSE. Not
   the Opus package, not a codec package, not SkiaSharp, not any drawing library.
   The test project may reference the Opus package - and does, to prove the Opus
   path works once an application registers it - but the library must not.
1a. THE PRESENTER REFERENCES THE CORE AND PLAIN SkiaSharp, AND NOTHING ELSE.
   Never SkiaSharp.Views.*, never a windowing toolkit, and NEVER a
   SkiaSharp.NativeAssets.* package: the application picks the native asset that
   suits the platforms it ships on, and a library that picks one for it breaks
   every other platform. The Skia test project and the sample DO reference
   SkiaSharp.NativeAssets.Linux, because they are things that run here.
1d. AND ONLY WHAT NEEDS SkiaSharp LIVES IN THE PRESENTER PACKAGE. A type that
   compiles without naming a Skia type belongs in the core, whatever it is for -
   see THE CORE / .Skia RE-SPLIT. The check is one grep and it must come back
   empty:
       grep -rn 'SkiaSharp\|\bSK[A-Z]' src/CodeBrix.VideoPlayback \
           --include=*.cs --include=*.csproj
1b. THE AUTHORING LIBRARY REFERENCES THE CORE AND CodeBrix.VideoProcessing, AND
   NOTHING ELSE. Never SkiaSharp, never a decoder package, never
   CodeBrix.Audio.Opus, never the presenter: it writes files, it does not play
   them or draw them. Its TEST project references the Dav1d decoder and the Opus
   package, because proving that an authored file is a video means playing it -
   but the library itself never does.
1c. THE AUTHORING LIBRARY NEVER BECOMES A DEPENDENCY OF AN APPLICATION. It
   launches ffmpeg as a child process; ffmpeg's binaries carry exactly the
   licences this family exists to keep out of a shipped app. Nothing in src/
   except the authoring project may reference it, and no sample that models a
   shipped application may either.
2. NO CODEC. There is no video decoding in src/. The uncompressed "raw" codec is
   a FORMAT (RawVideoFormat) in the library; its DECODER lives with the tests and
   is linked into the tools project.
3. NRT IS OFF. No `?` on a reference type anywhere, and no `!` operator. Nullable
   value types are fine.
4. XML DOC COMMENTS ON EVERY PUBLIC MEMBER. CS1591 is fixed at source, never
   suppressed, and no <NoWarn> is ever added. Release must build with zero
   warnings.
5. CLEAN-ROOM CONTAINERS. The EBML, Matroska, Ogg, IVF and AV1 readers are
   written from published specifications only. No code from any other
   implementation has been read or copied. See THIRD-PARTY-NOTICES.txt, which
   says so and must stay true.
6. NOTHING PER FRAME IN THE STEADY STATE. The demultiplexer, the packet queues,
   the frame pool, the presenter and the converter all allocate nothing once
   playback is warm, and there are tests that fail if that stops being true.


BUILDING
========
    dotnet build CodeBrix.VideoPlayback.slnx -c Release

Eight projects: the three libraries, their three test projects, the two console
tools, and the consumer-shape sample. `global.json` selects the
Microsoft.Testing.Platform runner, matching the rest of the family.

The tools project and the sample set AllowUnsafeBlocks because the uncompressed
decoder they link in writes through plane pointers. Both libraries set it for the
same reason - the converter and the plane uploads work in pointers throughout.


TESTING
=======
    dotnet test CodeBrix.VideoPlayback.slnx -c Release

Two suites are opt-in, and both are skipped by default:

    CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1      opens the audio device and makes a
                                             noise (VideoPlaybackSessionAudioTests)
    CODEBRIX_VIDEOPLAYBACK_RUN_BENCHMARKS=1  runs the colour-conversion benchmark

Run one class at a time with `--filter-class "CodeBrix.VideoPlayback.Tests.<Name>"`.
Passing several class names separated by a bar reports "zero tests ran" on this
SDK; run them one at a time or run the whole suite.

THE "Process-wide registries" COLLECTION. Four classes share it and therefore
never run at the same time: VideoDecodersTests (which REGISTERS process-wide video
factories serving 'av01' and clears the registry), VideoPlaybackFailureTests and
AudioDecoderProbeTests (which READ those registries and need them empty), and
VideoPlaybackSessionAudioTests (which starts the shared audio output and registers
the Opus packet codec for the rest of the process). Without that, an av01 file
opened by a refusal test could find one of the fake factories and open
successfully, and the refusal being tested never happens - a real intermittent
failure, seen and fixed here, not a theoretical one.

One consequence, and it is honest rather than hidden: in the opt-in run, whichever
of the Opus-refusal tests happens to be scheduled after the audible tests SKIPS,
because once CodeBrixAudioOpus.Register() has run the missing-decoder path cannot
be reached in that process. Both of those tests run in full in the default,
device-free run.

THE TEST PROJECT'S OPUS PIN. tests/CodeBrix.VideoPlayback.Tests references
CodeBrix.Audio.Opus.BsdLicenseForever so the Opus path can be proved once an
application registers it; the LIBRARY never does. The pin was bumped to the
2026-08-29 Opus release, which overrides IPacketSoundDecoder.ConcealLoss and
reports SupportsLossConcealment true - so the suite now runs against the package
with real Opus PLC. Nothing in THIS library ever reports a loss (see REPORTED
PACKET LOSS above), so the bump changes nothing these tests can observe today; it
keeps the corpus current, and the PLC path becomes observable the day a streaming
source lands. Keeping the pin at the newest published Opus is the standing rule.

The suite depends on the golden corpus under tests/assets. When it is missing,
the tests that need it SKIP rather than fail - see EXTRAS-README.txt for how to
regenerate it.

The Skia suite is a second test project, tests/CodeBrix.VideoPlayback.Skia.Tests.
It references SkiaSharp.NativeAssets.Linux so that Skia actually runs here, and
its graphics-path tests use a headless surfaceless-EGL context (see P9 below).
Where no such context can be had they skip themselves and name the reason; the
shader itself is still measured against the core converter on Skia's raster
backend, which works everywhere.

THE "ALLOCATES NOTHING" TESTS ARE MEASURED MORE THAN ONCE, and there is a reason.
Six tests assert that a warm loop allocates EXACTLY zero managed bytes - the frame
pool, the frame object, the presenter mailbox, the colour converter, the whole
decode path, and the Skia presenter's draw loop. The measurement is
GC.GetAllocatedBytesForCurrentThread, which counts everything the THREAD did,
including the runtime's own work - and TIERED COMPILATION charges the measuring
thread a few kilobytes when it promotes the loop body, which a warm-up makes
unlikely rather than impossible. The suite therefore failed roughly one full run
in eight, on a busy machine, in a different test each time ("Expected value to be
0L, but found 3256L"). Found and fixed 2026-08-29.

The fix is tests/CodeBrix.VideoPlayback.Tests/SteadyStateAllocation.cs (LINKED
into the Skia test project, one copy of the reasoning): it runs the loop a fixed
five times and keeps the SMALLEST measurement. A tier-up spoils one pass; a real
per-frame allocation spoils every pass, so the smallest is still non-zero and the
test still fails. The assertion is exactly as strict as it was - zero, not
"small" - and the pass count is fixed so that the tests which also assert a rent
count or a frame count can state it.

THE AUTHORING SUITE is a third test project,
tests/CodeBrix.VideoPlayback.Authoring.Tests, and it is in three layers:

  * ARGUMENT TESTS, which assert the exact ffmpeg command text a request renders
    and the refusals a bad request earns. They run in milliseconds, touch no
    file and need no ffmpeg at all - which is the whole point of the dry run.
  * END-TO-END TESTS, which author real files from clips generated on the spot
    from ffmpeg's own synthetic sources, then read them back with this
    repository's readers. They SKIP, naming what was looked for, when ffmpeg is
    absent.
  * PLAYBACK TESTS, which open what was authored with the published Dav1d
    decoder registered and decode through to a frame count and a set of frame
    hashes. This project is the only one in the repository that references a
    decoder package. The six CodeBrix-Mode2 corpus files are played here too,
    and skip themselves when the corpus has not been generated.

That project also references tools/CodeBrix.VideoPlayback.AssetAuthoring, for one
test: that the authoring library still renders EXACTLY the command lines the
committed sample-video manifest records. That test is what lets the eighteen
FFmpeg-muxed corpus files stand unregenerated - see THE SAMPLE-VIDEO CORPUS
below.


PACKAGING / PUBLISHING
======================
Jeremy packs and publishes. The csproj carries the family's canonical
date-stamped version block, PackageId CodeBrix.VideoPlayback.MitLicenseForever,
PackageLicenseExpression MIT, and pack lines putting README.md, AGENT-README.txt,
icon-codebrix-128.png, THIRD-PARTY-NOTICES.txt and LICENSE at the package root.

Verify a pack with:

    dotnet pack src/CodeBrix.VideoPlayback/CodeBrix.VideoPlayback.csproj -c Release -o <folder>
    unzip -l <folder>/CodeBrix.VideoPlayback.MitLicenseForever.<version>.nupkg

The package must carry exactly one dependency, CodeBrix.Audio.MitLicenseForever.
A second dependency appearing is a defect, not a decision.

The presenter packs the same way, with its own AGENT-README from
src/CodeBrix.VideoPlayback.Skia/ landing at the package ROOT:

    dotnet pack src/CodeBrix.VideoPlayback.Skia/CodeBrix.VideoPlayback.Skia.csproj -c Release -o <folder>
    unzip -l <folder>/CodeBrix.VideoPlayback.Skia.MitLicenseForever.<version>.nupkg

It must carry exactly two dependencies: CodeBrix.VideoPlayback.MitLicenseForever
at the version being packed, and SkiaSharp. A SkiaSharp.NativeAssets.* entry
appearing there is a defect - see hard rule 1a. The core must appear as a
DEPENDENCY and not as a bundled assembly; there is one lib/net10.0 folder in each
package and it holds one assembly.

The authoring library packs the same way, with its own AGENT-README from
src/CodeBrix.VideoPlayback.Authoring/ landing at the package ROOT:

    dotnet pack src/CodeBrix.VideoPlayback.Authoring/CodeBrix.VideoPlayback.Authoring.csproj -c Release -o <folder>
    unzip -l <folder>/CodeBrix.VideoPlayback.Authoring.MitLicenseForever.<version>.nupkg

It must carry exactly two dependencies: CodeBrix.VideoPlayback.MitLicenseForever
at the version being packed, and CodeBrix.VideoProcessing.MitLicenseForever. A
third entry is a defect - see hard rule 1b.

Pack all three in ONE run so they agree on a version:

    dotnet pack CodeBrix.VideoPlayback.slnx -c Release -o <folder>

PUBLISH ALL THREE TOGETHER. The presenter and the authoring library each declare
the core at the exact version they were packed beside, so publishing one without
the others leaves a package on nuget.org whose dependency does not exist. The
release event is: pack the solution once, verify the three dependency sets, push
the three.


PROVENANCE / VENDORED SOURCES
=============================
Nothing is vendored. Every line here was written for this repository from the
published specifications:

  RFC 8794    EBML
  RFC 9559    Matroska
  RFC 3533    Ogg framing
  RFC 7845    Ogg Opus (the identification header and pre-skip)
  the WebM Container Guidelines
  the AV1 bitstream specification (the sequence header and unit framing only)
  the AV1 codec ISO Media File Format binding (the av1C configuration record)
  the AV1-in-Matroska, Opus-in-Matroska and Vorbis-in-Matroska mappings
  ISO/IEC 23091-2 (the colour primaries, transfer and matrix code points)

See THIRD-PARTY-NOTICES.txt.


DESIGN NOTES
============

The buffer pool contract
------------------------
PinnedFrameBufferPool hands out 64-byte-aligned unmanaged memory with strides a
multiple of 64 bytes, dimensions rounded up to a multiple of 128 samples, 64
bytes of slack after each plane, the two chroma planes sharing a stride, and
10-bit and 12-bit samples as little-endian 16-bit words justified towards the
least significant bit.

That is not an arbitrary choice. It is the contract a frame-threaded software
video decoder asks of a host allocator, and it is simultaneously what a graphics
upload wants to read. Meeting it means a decoder can write STRAIGHT into the
memory a presenter will upload from, with no copy anywhere between the two - and
that no reformatting step can ever creep in.

Unmanaged rather than a pinned managed array, deliberately: the memory must stay
at one address while a native decoder writes into it and a driver reads out of
it, and a block the garbage collector never sees cannot fragment the heap the
way a long-lived pinned array does. Allocation is NativeMemory.AlignedAlloc with
a 64-byte alignment; release is NativeMemory.AlignedFree.

Generations. Buffers are pooled by frame shape. When the shape changes, the pool
increments its generation, frees the buffers of the old shape as they come back,
and settles again. A frame still on screen across the change stays valid, because
the buffer it holds is only freed when its last reference drops.

Reference counting and fences
-----------------------------
A VideoFrame is reference-counted because more than one part of the system
genuinely holds one at the same time: a video decoder keeps decoded pictures
alive as prediction references for LATER frames while a presenter is still
reading the same picture. One owner cannot express that.

Create() takes the first reference; Retain() adds one and returns THE SAME
object; Dispose() removes one; at zero the buffer goes back to the pool and the
frame OBJECT is recycled too, so a decode loop allocates nothing at all.

The frame free list belongs to the POOL, not to the process. It was static once,
and that was wrong twice over: two sessions handed each other's recycled frame
objects about, and a test that disposed a frame and then checked that reading it
throws could have that frame taken off the shared list by another test between
the two statements - which it did, about once in twenty runs. Per-pool ownership
removes both, and removes a process-wide lock from the hot path as well.

The recycling goes through the POOL CONTRACT rather than through a type test.
IVideoFrameBufferPool carries TakeFrame() and ReturnFrame(), Create() and
Dispose() call them, and PinnedFrameBufferPool overrides both onto the internal
free list it already had. Both are DEFAULT interface methods - allocate one, keep
none - so no existing implementation had to change.

That indirection is not decoration. Create() used to ask "is this pool a
PinnedFrameBufferPool?", and for the only real decoder in the family the answer
is NO, permanently: a decoder that hands its frames to an application must
interpose a pool of its own between the frame and the session's pool, because
Dispose() on that pool is the only signal it gets that the application has
finished with a picture - and it has to turn that signal into a native release
rather than a return. So the one pool that recycled frame objects was never the
one a frame was created with, and every decoded picture allocated an object.
Measured through the dav1d binding: 128 bytes a frame before, zero after.
A frame created with a null pool still simply allocates.

Fences exist because a graphics device does its work later than the code that
asked for it. A presenter puts an IVideoFrameFence (or a Func<bool>) in
VideoFrameBuffer.Tag before it starts an upload; the pool refuses to reuse a
buffer whose fence has not signalled, parks it, and re-examines every parked
buffer on each rent and return, and on PumpFences(). A buffer with no fence in
its tag returns immediately, which is what the CPU path does.

Session threading
-----------------
Two threads of the session's own, plus the audio engine's.

  demux thread   reads the container and copies packets into two bounded rings
  decode thread  drives IVideoDecoder and posts frames to the presenter
  clock thread   raises PositionChanged, updates the active cues and the current
                 chapter, and decides when playback has ended
  audio thread   the engine's own; it PULLS packets out of the audio ring through
                 IAudioPacketSource, and must never block

The rings (Internal/PacketRing) reuse their slot buffers forever, so demuxing
allocates nothing. One short lock covers a whole enqueue including the payload
copy - a few kilobytes a few hundred times a second, which costs nothing and
removes every question about what a Clear does to a packet a consumer is holding.
Clear KEEPS the in-flight packet and drops the rest, and every packet also
carries a seek generation the consumer checks, so a stale packet is skipped
rather than decoded.

Close() stops the threads BEFORE taking the lock that the demux thread uses. The
other order deadlocks, and did once.

The clock
---------
When there is an audio track, the clock IS PacketAudioPlayer.Position - the
position of the audio actually handed to the mixer - because that is what a
listener hears and everything else has to follow it. With no audio it is a
Stopwatch based at the last seek.

One correction is applied. PacketAudioPlayer discards the codec's own priming
(Opus's pre-skip) at Open and lets those discarded samples advance Position,
because they are media time. Presentation time is therefore Position minus the
priming duration until the first seek; after a seek the player re-bases to the
timestamp we hand it and the correction is zero. The session tracks that in
audioClockCorrection. For Opus it is about 6.5 milliseconds - below any A/V sync
threshold, but free to get right.

Clock policy: a frame later than LateFrameTolerance is dropped rather than shown
at the wrong time, and counted in the presenter's Late statistic (which is a
different number from Superseded - see the type's own documentation). After
ConsecutiveLateFramesBeforeSkip late frames in a row, the decode thread discards
packets until the next key frame and flushes, rather than trying to catch up
frame by frame.

Seeking
-------
Seek asks the container reader to land on the key frame at or before the target,
clears both rings, bumps the seek generation, and sets the reported position
immediately so a scrubber does not lag. In Exact mode the decode thread then
throws frames away until it reaches the target and presents that one at once,
even while paused - which is what makes scrubbing show the right picture. In
KeyFrameOnly mode the landing point is the target.

Audio trimming and pre-roll
---------------------------
Matroska's CodecDelay is the codec's own priming and is handled by
PacketAudioPlayer through the decoder's PreSkipSamples; the session does not
apply it twice.

On a seek, the session calls PacketAudioPlayer.Seek(firstPacketTimestamp,
preRoll) at the moment it enqueues the FIRST audio packet of the new generation,
with preRoll = target - firstPacketTimestamp. That decodes and discards the audio
between the landing point and the target, which is both the correct trim and the
pre-roll a lapped codec needs. Opus wants about 80 milliseconds; when the landing
point is closer than that, it gets less, and the first few milliseconds are
imperfect. Measured elsewhere in the family: 80 ms of pre-roll leaves about 7 per
cent relative error on a pure sweep, 240 ms leaves none. A player that wants a
seek to be inaudible should land further back.

TRAILING TRIM: THE ENCODER'S TAIL PADDING, AND WHO CUTS IT.
The audio package grew both of the shapes this library asked for, and the session
now uses both. There is no workaround here any more: the padding is trimmed out of
the AUDIO, by the audio engine, rather than being hidden by stopping the clock.

  BESPOKE .cbv - EXACT, AND KNOWN BEFORE A PACKET IS READ.
  The track header carries trailing_trim_samples, in samples per channel at the
  track's rate. CreateAudioPlayer converts it to frames at the DECODER's rate
  (VideoPlaybackSession.ResolveTrailingTrimFrames - the same number for every
  codec we read, but they are different questions) and calls
  PacketAudioPlayer.SetTrailingTrimFrames before Open. The frames form is the
  exact instrument: it applies from the first sample, so the whole trim is
  honoured however large it is.

  MATROSKA - PER BLOCK, THEN ARMED WHEN THE LAST BLOCK IS KNOWN.
  Nothing in a Matroska track header says where the sound stops; the padding is a
  DiscardPadding on the last block of the track. Two routes carry it, and both are
  used:
    * every audio packet's padding travels on the packet, through the
      AudioPacket(data, timestamp, discardPadding) constructor in
      SessionAudioPacketSource. The audio player holds back the LARGER of the
      track-level trim and the padding of the most recent packet, so the value on
      the last block is the one still raised when the stream ends. This route is
      BEST-EFFORT: a value is only learned when its packet arrives, so it can hold
      back only what is still in the player's hand plus what that packet decodes
      to. A padding bigger than one packet is therefore not fully honoured by this
      route alone.
    * so the same value is ALSO set as the track-level trim, which is exact.
      ArmTrailingTrimFromLastAudioPacket does it from PublishTrackExhaustion, at
      the moment the reader proves the track is finished and before the end of
      stream is published - the demultiplexer is normally a whole queue ahead of
      the audio thread, so the trim is in place well before the tail is decoded.
      It is only ever RAISED, never lowered, so a .cbv's exact header value is
      never clobbered by a stray block padding.
  A seek puts it back: the demultiplexing loop notices the generation change,
  forgets the padding it had seen and re-applies the container's own trim, because
  the end of the track has moved and a padding read off the block that used to be
  the last one says nothing about the new one.

  THE DURATION STOP IS STILL THERE, AS THE OUTER BOUND, and it is correct: it
  covers the PICTURE as well as the sound, and it is what stops a file whose media
  runs past what it declared. It is no longer the trimming mechanism, and
  HasReachedEnd says so in a comment.

  ALLOCATION. The per-packet route allocates nothing - AudioPacket is a struct
  built from values PacketRing already holds. SetTrailingTrimFrames allocates the
  audio package's hold-back ring once, on the calling thread (Open's, or the
  demultiplexing thread's), never on the audio thread. The steady state is
  unchanged.

  TESTS. Device-free: AudioTrailingTrimTests (the .cbv header round-trip, the
  frames conversion including a mismatched decoder rate, the Matroska last-block
  fact, the packet source carrying the padding, and a trimmed clip still playing to
  its Duration). Audible, opt-in: VideoPlaybackSessionAudioTests - a .cbv whose
  header states 4800 frames (100 ms) reaching the player before anything is heard,
  a trim longer than the whole sound track still reaching the end, and
  raw-opus.mkv's 13.5 ms last-block padding becoming the track's trim.

ASKING WHETHER AN AUDIO CODEC CAN BE PLAYED, WITHOUT OPENING A DEVICE.
SharedAudioOutput.CreatePacketDecoder starts the shared output - the codec registry
lives on the running engine - so it must never be used as a QUESTION. The audio
package's SharedAudioOutput.IsPacketCodecSupported answers without starting
anything, and CodeBrix.VideoPlayback.Decoding.AudioDecoders is the thin forwarder
this library exposes so a consumer does not have to reach past it into the audio
package.

  CreateAudioPlayer asks it FIRST and throws the contractual missing-decoder
  message before Configure or CreatePacketDecoder is reached, so the whole refusal
  path is device-free. The try/catch around CreatePacketDecoder stays as a
  backstop: the probe answers for the seam, and a factory can still decline one
  particular track's codec-private data.

  cbvinfo prints "decoder available: yes/no (via the shared audio output)" for
  every audio track, which is why that tool still runs on a machine with no sound
  card.

  Phase B1's G2 caveat - "a device-free test of the missing-Opus-decoder message
  cannot exist" - is WITHDRAWN. AudioDecoderProbeTests contains exactly that test,
  and it watches SharedAudioOutput.IsRunning across the call to prove no device was
  opened.

REPORTED PACKET LOSS: A SEAM, NOT A FEATURE, TODAY.
A file source cannot lose a packet - a byte range either reads or throws - so
nothing in this library ever produces AudioPacket.Loss, and the audio player never
sees one from here. The seam is documented because a future source CAN see a gap: a
live stream, or a lossy transport behind a custom IMediaSource. Such a source
reports the gap's LENGTH with AudioPacket.Loss(TimeSpan) or Loss(int frames), the
decoder conceals it when its IPacketSoundDecoder.SupportsLossConcealment says it
can, and the player fills whatever the decoder cannot with silence - so the gap
always comes out the length it really was and the audio after it keeps its
position. What must NOT be reported as a loss is an underrun: a moment when the
demultiplexer has not kept up is TryReadPacket returning false with EndOfStream
still false, which consumes none of the timeline. SessionAudioPacketSource's own
documentation says both.

  CHECKED, NOT ASSUMED: nothing in the session's audio path would mishandle a loss
  packet if one ever appeared. AudioPacket is a readonly struct that carries the
  loss members itself, PacketRing stores a zero-length payload without complaint,
  and SessionAudioPacketSource only ever constructs the ordinary three-argument
  form. AudioTrailingTrimTests pins all of that
  (The_audio_packet_source_never_reports_a_loss and
  A_loss_packet_from_a_future_source_would_travel_the_queue_intact).

HTTP behaviour
--------------
HttpMediaSource probes with a one-byte Range request. A 206 answer, or an
Accept-Ranges header, means ranges are available: the source then seeks freely,
serves small reads out of a 256 KB read-ahead window, and turns a single large
read into a single request - which is what makes a file whose index is at the END
open in one extra request rather than a download. Otherwise it falls back to one
progressive response body, reports CanSeek false, and throws a message saying so
if anybody seeks. RequestCount is exposed so a test can prove the window works.

The .cbv format
---------------
CBV-FORMAT.txt at the repository root is the authority, and its last section
lists every deviation from the draft layout in the program plan and why. In
short: byte-packed with no padding, an index and every caption cue in front of
the media data, a per-track entry length so an unknown track kind can be stepped
over, colour metadata on video tracks (without which an uncompressed track cannot
be interpreted at all), and two CRC-32 checksums with their coverage stated
exactly.

Two CRC-32 variants are in play and they are NOT the same one. Matroska and the
.cbv header use CRC-32/ISO-HDLC (reflected, 0xEDB88320, inverted at both ends);
Ogg uses the direct form (0x04C11DB7, no inversion). Feeding a page to the wrong
one produces a plausible number that never matches.

Timestamps in Ogg audio
-----------------------
Opus states a packet's duration in its first byte, so Opus timestamps are exact.
Vorbis does not: a packet's length depends on a mode number that can only be read
after the setup header's codebooks have been decoded, which is a decoder's job.
So Vorbis packets are timed from the page granule positions, which ARE exact, and
share their page's duration equally within the page. Page boundaries are exact;
the intra-page error is bounded by one page. It affects only where a seek lands,
never the audio, because the audio clock counts samples the mixer actually
received.


PER-TRACK EXHAUSTION, AND THE DEADLOCK IT PREVENTS
==================================================
E1. THE DEADLOCK, AS IT WAS. A clip whose sound ends before its picture does -
    a silent tail, music shorter than the footage - used to stop the player
    dead. Every link in the chain was individually correct:

      the audio player's Position stops advancing once its queue has drained
        -> the clock, which follows the audio player, freezes
        -> the decode thread holds a frame it may not present yet
        -> the video packet queue stops draining and fills
        -> the demultiplexer blocks trying to enqueue into it
        -> the end of the FILE is never reached
        -> the audio source is never told that no more packets are coming
        -> the audio player never reports its end
        -> the clock never moves off it.

    Position froze, State stayed Playing, and PlaybackEnded never fired.

E2. THE CONTRACT. IMediaContainerReader gained two members:

        bool IsTrackExhausted(int trackId)
        TimeSpan? GetTrackEndTimestamp(int trackId)

    IsTrackExhausted may return true ONLY when the reader can prove no further
    packet for that track exists between where it stands and the end of the
    media. False means "not proven", which covers both "there is more" and "I
    cannot tell yet". A track the file does not declare reads as exhausted, and
    a reader repositioned by Seek answers for its NEW position. A false negative
    costs latency; a false positive truncates somebody's media, so every reader
    here is written that way round.

E3. WHAT EACH READER CAN ACTUALLY PROVE.

    CbvReader        EXACT, AND EARLY. The index at the front of the file names
                     the track of every chunk in it, so the reader works out
                     where each track's last chunk sits while it is opening the
                     file and then answers from the read cursor alone - a second
                     and a half before the end of the file, for the clip above.
                     GetTrackEndTimestamp is exact for every track. A file with
                     no index yields this reader no packets at all, so every
                     track of one reads as exhausted immediately, which is true.

    MatroskaReader   EXACT ONLY AT THE END OF THE FILE, for every track at once.
                     Nothing in Matroska records where a track stops. Cues are
                     the obvious candidate and do not answer the question: a cue
                     point marks a KEY FRAME, so the last cue for a track is
                     followed by however many non-key frames the encoder chose,
                     and in practice a WebM file's cues index the video track
                     alone and say nothing about the audio. Cues CAN refute
                     exhaustion - a cue beyond the read position proves a track
                     continues - but refuting is not proving, and treating "past
                     the last cue and quiet since" as an ending would truncate
                     ordinary files. So it waits until it is certain.
                     GetTrackEndTimestamp is null until then.

E4. WHAT THE SESSION DOES WITH IT. The demultiplexer asks the reader after every
    read. The audio source is told EndOfStream as soon as the AUDIO track is
    exhausted, and the decode thread learns the VIDEO track is exhausted and
    drains - both independently of the container as a whole. Neither signal is
    published until that track's parking is empty, because the reader knowing a
    track is finished is not the same as this session having handed over
    everything it had for it; publishing early would cut the tail off.

    PlaybackEnded now fires at the LATER of the two ends. The video side is
    finished when the reader has no more video, the queue and parking are empty
    and the decoder has been drained; the audio side when the packet player says
    so. A picture that outlasts its sound plays to its own end with the
    stopwatch clock taking over at the sound's end; a sound that outlasts its
    picture is heard out with the last frame still on screen.

E5. THE DEMULTIPLEXER CANNOT STARVE A TRACK ANY MORE. Two changes:

    (a) The old pre-check refused to read at all while EITHER queue was full, so
        a full audio queue stopped video being demultiplexed and vice versa.
        That was its own defect and it is gone: each track is judged separately.

    (b) A packet whose queue has no room is PARKED - held aside, in order - and
        the demultiplexer carries on reading. Parked packets always go back into
        the queue before any newer packet of the same track, so a track's
        sequence is exactly what the file stored. Parking is bounded by
        VideoPlaybackOptions.MaxTrackParkingBytes, 32 MB per track by default,
        and the demultiplexer waits only when a track can take nothing at all -
        queue full AND parking at budget. Hitting the budget adds a Notice
        naming the option rather than failing silently.

    WHY A BOUNDED PARKING LIST RATHER THAN A ONE-PACKET SLOT: one slot buys one
    packet, and the skew that matters here is a whole track's tail - fifty
    packets in the corpus's own no-cues file. Why not bounded waits with
    re-check: the wait is not the problem, the not-reading is; a demultiplexer
    that is asleep learns nothing, and for Matroska the only way to learn that a
    track has ended is to reach the end of the file. Nothing is parked at all
    while a file interleaves its tracks normally, so this costs nothing.

E6. WHICH MECHANISM CARRIES WHICH FILE - measured, by turning each off:

                                   exhaustion ON        exhaustion OFF
      parking ON (32 MB)           both complete        both complete
      parking OFF (budget 0)       .cbv completes,      BOTH HANG at 1.015 s
                                   .mkv HANGS           (the original defect)

    The two are independent and both are needed: the index carries the bespoke
    container, and the parking carries Matroska, which has no index to carry it.

E7. WHAT IS STILL TRUE. A Matroska file whose one-track skew exceeds the parking
    budget will still make the demultiplexer wait, and with it the discovery
    that another track has ended. 32 MB is roughly two minutes of 1080p video at
    a few megabits a second, which is far more skew than any ordinary file has;
    the Notice says when it has been hit and which option to raise. A container
    that could answer IsTrackExhausted early would not need the budget at all,
    which is exactly what the bespoke format does.


THE PRESENTER: TWO RENDER PATHS, ONE SHADER, ONE OFF-SCREEN SURFACE
===================================================================
P1. THE COMPOSITION SURFACE IS THE FRAME'S CODED SIZE, and the display aspect
    ratio is applied at the BLIT. That keeps the shader's coordinates at exact
    luma texel centres - a 1:1 mapping, no resampling of the source - which is
    what lets the shader be compared with the CPU converter pixel for pixel. A
    layer therefore draws in video pixels and is scaled and letterboxed along
    with the picture, which is almost always what a layer wants.

P2. THE PROCESSOR PATH HAS NO COPY IN IT. The composition surface is created
    with SKSurface.Create(info, pixels, rowBytes) over a BgraFrameBuffer rented
    from the core's pool, and the converter writes STRAIGHT INTO those pixels.
    Layers then draw onto the same memory through the surface's canvas, and Draw
    blits with SKSurface.Draw - no snapshot, no image object, nothing allocated.
    Verified on 4.151.0: a raster-direct surface's Snapshot() COPIES rather than
    sharing, so a snapshot taken while the buffer is later overwritten does not
    change under the caller and no copy-on-write ever moves the surface off our
    memory. Both facts are load-bearing; re-check them if the Skia pin moves.

P2a. AND WRITING BEHIND SKIA'S BACK HAS A PRICE, PAID WITH ONE LINE. Skia CACHES
    the image a surface's Snapshot() handed back and only throws that cache away
    when something draws through the surface's canvas. The converter does not
    draw - that is the whole point of P2 - so Skia never learned the pixels had
    changed, and CaptureComposedFrame() and CurrentImage handed back the FIRST
    composed frame for the life of the presenter on the processor path with no
    overlay layers. (A layer or a Composing handler hid it, because their drawing
    invalidated the cache.) Found 2026-08-30 while building Recompose(), which
    could not otherwise show its own work; fixed by calling
    surface.Canvas.Discard() before the converter writes - which says exactly
    what is about to happen, every pixel replaced and nothing preserved, and
    costs nothing on a surface that does not own its pixels. The regression test
    is SkiaVideoPresenterTests.Every_frame_composed_on_the_processor_is_captured
    _as_itself: two frames through ONE presenter, captured, compared.

P3. THE GRAPHICS PATH UPLOADS WITH SKImage.FromPixels(SKPixmap) FOLLOWED BY
    SKImage.ToTextureImage(GRContext). FromPixels BORROWS the plane's memory
    rather than copying it, so the samples the decoder wrote reach the driver
    from where they already are; ToTextureImage is the only public SkiaSharp
    call that uploads host pixels to a texture. Planes go up as R8Unorm for
    8-bit content and R16Unorm for 10- and 12-bit, and the shader multiplies by
    255 or 65535 accordingly. SkiaSharp offers no way to REFILL an existing
    texture from host memory, so the graphics path allocates a handful of small
    wrapper objects per frame; the pixels are still never copied. If a future
    SkiaSharp exposes a texture write, that is the allocation to remove.

P4. ONE SkSL SHADER, IN TWO VARIANTS. Rendering/YuvShaderSource - in the CORE
    package since the 2026-08-30 re-split - builds the source with and without
    the lookup-table stage; building two rather than binding an identity table to
    the plain one saves a texture sample per pixel in the common case.
    Rendering/YuvShaderUniforms computes the matrix rows, the sample offsets and
    the chroma-siting flags, and MIRRORS the CPU converter's ConversionConstants
    exactly, in floating point rather than fixed point. Both are shader TEXT and
    shader ARITHMETIC, not a drawing dependency: the core compiles nothing.

P5. CHROMA SITING IN THE SHADER IS THE COORDINATE, NOT A BRANCH. A luma
    coordinate maps to a chroma coordinate one of three ways per axis: straight
    through when the axis is not subsampled; snapped to the covering texel's
    centre when the chroma sample sits ON the luma sample; and simply HALVED
    when it sits between two, because a pixel centre at x + 0.5 becomes
    x/2 + 0.25 - a quarter of a chroma texel from the near centre, which is
    exactly the 3:1 blend the specification asks for and which the sampler's own
    linear filter then performs for free. Vertical siting means co-sited
    HORIZONTALLY and halfway VERTICALLY; reading it the other way inverts the
    upsampling, and there is a test named for it.

P6. THE RESULTANT LOOKUP TABLE IS A STRIP, NOT A CUBE. SkSL has no
    three-dimensional sampler, so the composed grid is laid out as size tiles of
    size-by-size pixels side by side: the node (r, g, b) is at
    x = b * size + r, y = g, in RGBA one byte per channel with alpha 255. Red
    and green interpolate in the sampler; the shader interpolates between two
    tiles itself for blue. Clamping the input to 0..1 before scaling by size - 1
    is what keeps every sample inside its own tile, so no tile bleeds into its
    neighbour. Eight bits a channel is the same quantum as the output surface,
    so nothing is lost; a float atlas was tried and buys nothing here.

P7. THE FENCE IS INSTALLED AND SIGNALLED AROUND THE WHOLE DRAW. GpuUploadFence
    goes into VideoFrameBuffer.Tag before the upload and is signalled after
    GRContext.Flush() and Submit(false) - the point at which Skia no longer
    references the host memory. The pool parks the buffer until then.

P8. THE SHADER IS TESTED WITHOUT A GRAPHICS DEVICE. SKRuntimeEffect compiles AND
    RUNS on Skia's raster backend, so .Skia's Internal/YuvSurfaceRenderer takes a null
    GRContext and draws the very same shader with the very same bindings onto a
    software surface. YuvShaderSourceTests then compares it with the core
    converter for ten layout/siting/range/matrix combinations and for six real
    decoded frames: worst channel difference 2, mean under half a level. That is
    the correctness proof for the graphics path, and it needs no display.

P9. A REAL GRAPHICS CONTEXT IS ALSO AVAILABLE HERE, headlessly. Mesa's
    EGL_MESA_platform_surfaceless gives a display with no window behind it, and
    tests/CodeBrix.VideoPlayback.Skia.Tests/HeadlessGraphicsContext.cs reaches it
    through four P/Invokes to libEGL - nothing is installed. Where it cannot be
    had, the graphics tests skip themselves and say why.


THE CORE / .Skia RE-SPLIT (2026-08-30)
======================================
THE RULE, and it is the whole of it: ONLY code with a DIRECT SkiaSharp dependency
lives in CodeBrix.VideoPlayback.Skia. Everything else lives in the core, and
anything that moves loses a Skia-flavoured name.

WHY. A second presenter is coming - a CodeBrix.Platform add-in - and it must not
depend on .Skia. The Platform family publishes as one unit and pins ONE SkiaSharp
version of its own; .Skia pins its own. A package compiled against one SkiaSharp
and run against another fails at runtime the first time SkiaSharp breaks a
signature it uses, which happens across majors as a matter of routine. So the
add-in carries its own Skia binding compiled against the family's pin, and takes
everything else from the core - which is only possible if "everything else" is
actually IN the core.

WHAT MOVED, and where it went:

  src/CodeBrix.VideoPlayback/Rendering/     (namespace CodeBrix.VideoPlayback.Rendering)
      VideoRenderPath, VideoRenderBackend, VideoStretch,
      VideoRenderPathChangedEventArgs             unchanged names
      VideoCompositionStatistics                  was SkiaVideoPresenterStatistics
      GpuUploadFence                              was SkiaGpuUploadFence
      LutAtlas, CpuLutApplier,
      YuvShaderUniforms, YuvShaderSource          were internal; now PUBLIC
      VideoCompositionContext                     was .Skia.Composition; its rect is
                                                  now a VideoRectangle, not an SKRect
      VideoRectangle, VideoStretchMath            NEW

  src/CodeBrix.VideoPlayback/Effects/       (namespace CodeBrix.VideoPlayback.Effects)
      IVideoFrameEffect, LutEffect, EffectComposer, VideoColorTransform

WHAT STAYED, because it genuinely needs SkiaSharp: SkiaVideoPresenter,
Internal/YuvSurfaceRenderer, Composition/IVideoLayer (it draws on an SKCanvas),
Composition/VideoComposingEventArgs, and the new SkiaRectangles bridge.

THE JUDGMENT CALL WAS YuvShaderSource, and it went to the core. The text is SkSL,
Skia's dialect - but it is TEXT. The core compiles nothing and references no
drawing library; a presenter takes the string and compiles it with whatever it is
built on. Keeping it in .Skia would have forced the add-in to duplicate the one
thing that IS the colour arithmetic. Its XML docs say so in as many words.

THE NAMESPACE CHANGE IS SOURCE-BREAKING FOR .Skia CONSUMERS, deliberately and
with a decision behind it: a [TypeForwardedTo] cannot bridge a namespace change,
so there is nothing to add that would help. Only using lines break - no member
signature changed shape except VideoCompositionContext.VideoRect, and
SkiaRectangles.ToSKRect()/FromSKRect() carry a layer across in one call. The
package was one day old and its consumers were the sample in this repository and
Jeremy's own work.

TWO NEW CONSTANTS AND ONE FORWARD. EffectComposer.DefaultSize = 33 is the number
now, and SkiaVideoPresenter.DefaultEffectLutSize forwards to it, so existing code
compiles and the two can never drift.

THE TEST SUITES FOLLOWED THE CODE. Every test that needs no drawing device moved
to tests/CodeBrix.VideoPlayback.Tests: EffectComposerTests (the eight
presenter-free ones), LutAtlasTests, LutCrossStageEquivalenceTests, LutEffectTests
(six of seven), GpuUploadFenceTests (was SkiaGpuUploadFenceTests),
YuvShaderUniformsTests, plus the new VideoRectangleTests and VideoStretchMathTests.
The five tests in those files that drive a SkiaVideoPresenter came back to
SkiaVideoPresenterTests, where a presenter belongs. YuvShaderSourceTests and
LutShaderInterpolationTests stayed in the Skia suite because they COMPILE the
shader, which needs Skia. TestLuts.cs - the hand-built "invert" and "halve" tables
both suites grade with - lives in the core suite and is LINKED into the Skia one,
the same arrangement SteadyStateAllocation.cs already had.

THE PROOF the split is real, and the check to re-run after any change here:

    grep -rn 'SkiaSharp\|\bSK[A-Z]' src/CodeBrix.VideoPlayback \
        --include=*.cs --include=*.csproj

It must return NOTHING. It does.


THE COLOUR LOOKUP ENGINE, AND THE ONE-COMPOSER RULE
===================================================
Added 2026-08-29. Everything about ".cube" lookup tables - the only lookup
format supported - lives in the CORE package under
src/CodeBrix.VideoPlayback/Color/Luts/, namespace CodeBrix.VideoPlayback.Color.Luts:

    Lut3D, Lut1D            the tables, with DOMAIN_MIN/DOMAIN_MAX
    LutInterpolation        Tetrahedral (default) | Trilinear
    CubeLut, CubeLutFile    the file format, read AND write
    LutLayer                one table plus its apply-at percentage
    LutComposer             the effective-table engine
    LutComposerOptions      interpolation, output size, output domain
    LutDomain               internal - the domain arithmetic the tables share

THE MOVE. Lut3D, Lut1D and CubeLutFile used to live in
CodeBrix.VideoPlayback.Skia.Effects. They are pure arithmetic with no drawing in
them, and the AUTHORING side needs them without any drawing dependency at all -
so they moved down into the core while both packages were still unpublished, and
nothing was kept in .Skia for compatibility. CubeLutFile.ReadFile used to return
a LutEffect; it returns a CubeLut now, and LutEffect.FromCubeFile is the
convenience that wraps one. LutEffect, EffectComposer, LutAtlas and CpuLutApplier
followed them down on 2026-08-30 - see THE CORE / .Skia RE-SPLIT above.

THE ONE-COMPOSER RULE. There is exactly ONE implementation of the composition
arithmetic in this program, LutComposer, and it is in the core.
EffectComposer.Reset delegates to LutComposer.FillIdentity and every ApplyLut
overload delegates to LutComposer.ApplyLayer. Do not add a second walk of a
lattice anywhere. The reason is not tidiness: the picture an application shows
at playback and the picture the authoring pipeline encodes have to be the same
picture, and they are only guaranteed to be so while both go through this code.

HOW A CHAIN FOLDS. Walk the nodes of the OUTPUT table. Each node starts as the
colour it stands for. For each layer in order, sample THAT layer at ITS OWN size
and over ITS OWN domain - never at the output's - and mix:

    colour += (layerSample(colour) - colour) * (percent / 100)

A layer at 0 per cent is skipped entirely. Order is meaningful.

THE RULES, AND WHY THEY ARE WHAT THEY ARE:

  OUTPUT SIZE   the largest size any APPLIED layer has, floored at 33 and capped
                at 65 (LutComposer.DefaultMinimum/MaximumOutputSize). 33 is the
                ".cube" convention and enough for any smooth grade; 65 is where
                a cube stops buying fidelity and starts costing graphics memory
                (65 cubed is 274,625 nodes, 1.07 MB as an 8-bit atlas). A 129
                node layer therefore composes at 65. LutComposerOptions.OutputSize
                overrides the rule outright.

  OUTPUT DOMAIN 0 to 1, always, unless the caller states another. A layer's OWN
                domain is honoured where it belongs, on the way in to that
                layer's lookup, so a table declaring 0..4 handed an ordinary
                picture uses the bottom quarter of itself - which is what such a
                table means. Propagating that 0..4 to the OUTPUT instead would
                spend three quarters of the output's nodes on input values that
                cannot occur, and leave about eight useful nodes across the range
                that can. Both render paths and FFmpeg's raw video carry 0-to-1
                colour, so 0 to 1 is the right output domain for all of them.
                LutComposer.TryGetChainDomain reports the chain's own domain for
                a caller that deliberately wants to bake wider, and
                LutComposerOptions.OutputDomainMinimum/Maximum take it.

  1-D CHAINS    still compose to a Lut3D. A chain of curves only IS exactly
                per-channel and could be a Lut1D - LutComposer.TryComposeCurves
                returns one - but the default is a cube because that is what the
                shader's atlas samples and what FFmpeg's lut3d filter reads;
                FFmpeg's lut1d is a DIFFERENT filter with a different file, so a
                baked 1-D file would break the authoring command line. The curve
                route is opt-in and documented.

  INTERPOLATION Tetrahedral by default EVERYWHERE - LutComposer, EffectComposer,
                lutbake, the SkSL shader and CpuLutApplier - because it is
                FFmpeg's lut3d default and the grading tools', and because it
                holds the neutral axis exactly. Jeremy's ruling, 2026-08-29: the
                shader's atlas read is tetrahedral by default AND configurable.
                One consumer knob does all of it - SkiaVideoPresenter.
                EffectInterpolation, backed by EffectComposer.Interpolation -
                so the folding, the shader and the processor path can never
                disagree with each other. Lut3D.Sample WITHOUT an interpolation
                argument is still TRILINEAR; that overload is the raw-table
                convenience and every caller inside this repository passes the
                setting explicitly.

  COMBINED FILES A ".cube" stating BOTH LUT_1D_SIZE and LUT_3D_SIZE is Resolve's
                shaper-plus-table arrangement and is ACCEPTED: the curves run
                first and hand their answer to the table, each half keeps its own
                INPUT_RANGE as its domain, and the pair is ONE LutLayer with ONE
                percentage - because the two halves are one artistic step and
                applying each at half strength would be a different picture. The
                rows are read in the order the two sizes were declared.

  THE WRITER    CubeLutFile.Write emits LF endings, UTF-8 with no byte-order
                mark, the invariant culture, and numbers in PLAIN DECIMAL - never
                an exponent, which the format has no form for. The digits are the
                SHORTEST that read back as the very same float (ToString("R")),
                expanded into plain notation by shifting the decimal point when
                the runtime would have chosen an exponent. That is exact rather
                than merely close: F6 would lose a table's sixth decimal and
                anything below it. TITLE is written quoted with quotes and
                control characters stripped; DOMAIN_MIN/DOMAIN_MAX are written
                only when the domain is not the default.

  PERFORMANCE   NOT parallelised, on measurement: a 65-node table through four
                layers is 19.6 ms on the development machine (274,625 nodes,
                1.1 M samples). Composition happens when a chain CHANGES, never
                per frame, so there is nothing to win and a thread pool to
                explain. Flat float arrays and spans throughout; no allocation
                per sample.

THE AUTHORING HOOK is the `lutbake` verb in tools/CodeBrix.VideoPlayback.Tools
(LutBakeCommand.cs). It uses the core and nothing else, and it writes the file
whose PATH goes to FFmpeg's lut3d filter. See EXTRAS-README.txt.

THE ATLAS READ IN THE SHADER. SkSL has no three-dimensional sampler and no way
to ask for a tetrahedron, so the tetrahedral read is written out by hand in
Internal/YuvShaderSource.cs: four EXACT node fetches (the cell's black corner,
its white corner, and the two the wedge passes through) and the same six-way
selection on the order of the three fractional parts that Lut3D.Interpolate-
Tetrahedral does in the core. THREE shader variants are compiled and cached, not
one with a uniform branch - plain, lookup-trilinear, lookup-tetrahedral - so each
carries only the fetches it needs (two for trilinear, four for tetrahedral)
instead of the larger of the two, and there is no per-pixel branch at all. The
chain changes rarely and the shaders are cached, so the extra compile is never
measured. The ATLAS FILTER differs with the variant and must: trilinear binds it
LINEAR and leans on the sampler for red and green, tetrahedral binds it NEAREST
because its fetches must be node values and a filter would blend them into
something that is not a node. YuvShaderSource.NeedsFilteredAtlas says which.

CROSS-STAGE EQUIVALENCE, AND ITS MEASURED TOLERANCE. The claim the engine exists
to make is that a grade shown at playback and a grade encoded by the pipeline are
the same grade, and there is a test that measures it:
tests/CodeBrix.VideoPlayback.Tests/LutCrossStageEquivalenceTests.cs bakes an
effective table from two layers with percentages, applies it to the same 64x48
RGB24 frame through FFmpeg's lut3d filter and through CpuLutApplier, and compares
byte for byte. Measured against FFmpeg 7.1.5-0+deb13u1 on this machine:

    worst channel difference   1 level of 255
    mean channel difference    0.490 of a level

and an identity chain round-trips to within 1 level. The pinned bounds are 3 and
0.8, a little above the measurement.

WHAT THAT RESIDUE ACTUALLY IS - diagnosed 2026-08-29, and NOT what it was first
assumed to be. It is ROUNDING, not interpolation. FFmpeg is one level LOW on
about half the differing bytes and one level HIGH on NONE of them, which is the
signature of truncation against round-half-up: FFmpeg's eight-bit lut3d path
truncates the interpolated value, CpuLutApplier rounds half up. The test asserts
that one-directionality (ffmpegHigher <= 2, measured 0) so the diagnosis is
fenced rather than merely written down. Reading the SAME table both ways with
FFmpeg out of the picture entirely differs by a mean of 0.00152 of a level, worst
1 - three hundred times smaller than the rounding gap. So the tetrahedral default
is right because it is what a lookup MEANS to a grading tool and because it holds
the neutral axis, NOT because it repaints a smooth 33-node grade. Where the two
readings genuinely part company is a COARSE table: composing a chain of 17-node
layers into a 33-node one differs by up to 0.019970 (5.1 levels) with a mean of
0.000107 - measured in LutComposerTests. Rounding was left alone: half-up is the
unbiased convention and matching FFmpeg's truncation would make this library
worse to agree with a quirk.

THE SHADER'S OWN NUMBERS (LutShaderInterpolationTests, one effective 33-node
table composed once and read two ways, 48x32 synthetic I420 frame):
    shader vs CpuLutApplier   worst 2, mean 0.2322 tetrahedral / 0.2289 trilinear
    shader vs FFmpeg lut3d    worst 2, mean 0.4167 tetrahedral / 0.4188 trilinear
    Mesa device vs raster     worst 1, mean 0.0035 tetrahedral / 0.0043 trilinear
The first two carry one rounding the product does not - the comparison hands the
other implementation the shader's ALREADY eight-bit conversion output, while the
shader grades the float colour before it is ever quantised - so they are ceilings
on the whole graded pixel rather than a measurement of the read. The third is the
one that could have gone wrong and did not: the tetrahedral variant's manual node
fetches land on the same texels on a real graphics device as on the raster
backend.

FFmpeg is an ORACLE for these tests only; it never enters the product path and
the tests skip themselves when /usr/bin/ffmpeg is absent.

THE ".cube" CORPUS. tests/assets/LUTs holds twenty-three files - twenty-one that
must parse and two under invalid/ that must be refused - and LutCorpusTests walks
all of them, skipping itself when the folder is not in the checkout. Two of them
decide questions this reader had to answer: generated/domain_test_33.cube
declares -0.5..1.5 (honoured) and found/smol-cube/shaper_3d.cube is a combined
shaper-plus-table with two INPUT_RANGE keywords and values up to 13.5 (accepted).


THE STREAMABLE-PROFILE RULES, AND WHY THEY MOVED
================================================
Added 2026-08-29. The eight rules that decide whether a file is a "CodeBrix Video"
file rather than merely a WebM one used to live inside
tools/CodeBrix.VideoPlayback.Tools/CbvInfoCommand.cs, as a local function that
printed as it went. Nothing but a shell script could reach them, and the corpus
generator really did reach them that way: it started `dotnet`, ran the cbvinfo
verb as a child process, and PARSED WHAT IT PRINTED.

They are now CodeBrix.VideoPlayback.Containers.StreamableProfile - additive, in
the core, with no behaviour change at all:

  StreamableProfile.EvaluateFile(path)                   open, walk, judge
  StreamableProfile.Evaluate(reader, outOfOrderPackets)  judge a reader you hold
  StreamableProfile.CountOutOfOrderPackets(reader)       the packet walk on its own
  StreamableProfileReport   Rules / Failed / Warnings / Passes / Verdict /
                            FailedRules() / ToString()
  StreamableProfileRule     Rule / Detail / Outcome / Passed / Tag / ToString()
  StreamableProfileOutcome  Pass | Warn | Fail

WHAT DID NOT CHANGE, ON PURPOSE. StreamableProfileReport.ToString() renders the
heading, the indented rule lines, the blank line and the "result      ..." line
exactly as cbvinfo printed them, and cbvinfo now prints that string. The verb's
output text and its exit codes are byte for byte what they were, which is what
lets the committed manifest's recorded rule lines stand.

WHAT IT BOUGHT. The authoring library validates every file it writes with the same
code the tool prints; the corpus generator no longer spawns a process, no longer
needs the tools project to have been BUILT, and no longer parses console output;
and the rules are now testable as functions (StreamableProfileTests).

MediaContainers.Open moved for the same reason. The "sniff four bytes and pick a
reader" step existed in three places - the session, cbvinfo and cbvdecode - and is
now one public method in the core that all three, and the authoring library, call.


BENCHMARKS
==========
Measured 2026-08-28 on a 12th Gen Intel Core i7-12850HX (24 threads), LMDE 7,
.NET 10.0.400, Release, 20 warm-up plus 200 timed iterations:

  CPU colour conversion, 1080p 8-bit 4:2:0 BT.709 limited range to BGRA32
      Vector256 (the default here)   3.006 ms/frame     332.7 frames per second
      Vector128 only                 3.55  ms/frame     282   frames per second
      scalar fallback               22.42  ms/frame      44.6 frames per second

  Uncompressed decode through the whole container and pool path, 64x36 4:2:0
      0.037 ms/frame mean over 60 frames (cbvdecode on tests/assets/raw-synthetic.cbv)

The converter benchmark lives in VideoFrameConverterTests and prints its own
numbers; re-run it with CODEBRIX_VIDEOPLAYBACK_RUN_BENCHMARKS=1 rather than
trusting these.


ASSET REGENERATION
==================
tests/assets holds the golden corpus. Two scripts rebuild it:

  generate-assets.sh       needs FFmpeg and mkvmerge. Rebuilds every .webm, .mkv,
                           .ivf and .ogg file, their ffprobe oracles, and
                           ASSETS.txt.
  generate-cbv-assets.sh   needs only the .NET SDK. Rebuilds the .cbv samples
                           through this repository's own muxer.

The FFmpeg-produced files are NOT byte-reproducible (FFmpeg picks random track
UIDs and Ogg serial numbers); the mkvmerge ones are. ASSETS.txt records the
SHA-256 of what is committed. Regenerating them changes the bytes, so do not do
it casually - the committed files are the oracle.


THE SAMPLE-VIDEO CORPUS, AND THE RULE ABOUT REGENERATING IT
===========================================================
tests/assets/authoring is the OTHER corpus: twenty-four files of real video, in
four container profiles, derived from two Public-Domain phone clips. It is not an
oracle and no byte-level assertion is made against it; it exists so that a player,
a sample or a measurement has something to open that looks like the video people
actually have.

    MKV/              six off-the-shelf Matroska files, AV1 + Opus, cues at the end
    WebM/             the same six as WebM
    CodeBrix-Mode1/   the same six as WebM-profile .cbv - cues at the FRONT
    CodeBrix-Mode2/   six BESPOKE .cbv files, AV1 + Vorbis, index-first

THE RULE. CodeBrix-Mode2 was generated by the authoring library, and the other
three folders were NOT regenerated when it was added. That was deliberate. The
tool that made them used to build its own ffmpeg arguments; it now asks the
authoring library instead, and the library renders the same command text byte for
byte - which is what makes the eighteen existing files still exactly what the
manifest beside them says they are. There is a test that says so:
CorpusCommandEquivalenceTests in tests/CodeBrix.VideoPlayback.Authoring.Tests
compares the committed manifest's command lines with what the library renders
today. If that test ever fails, the corpus has to be rebuilt - which is a
decision, taken deliberately, not something to slip into a commit.

Regenerating one folder no longer leaves the manifest half stale. `--only <folder>`
re-encodes that folder and REWRITES THE WHOLE MANIFEST: every other folder is read
back and re-verified rather than re-encoded, its command line is re-derived from
the plan (which is where it came from in the first place), and its encode time is
carried over from the previous manifest. So MANIFEST.txt always describes all
twenty-four files, and it always describes them as they are on disk now.

Mode2 files cannot be probed with ffprobe - a CBVF file is not a container ffmpeg
has ever heard of - so the generator reads them back with this repository's own
container reader instead. The other three folders are still probed by ffprobe,
which keeps them judged by an implementation other than the one that wrote them.


DELIBERATE DIVERGENCES WORTH KNOWING
====================================
- An absent Matroska Language element is read as "eng" per RFC 9559, which
  normalises to "en". Writers almost always state "und" explicitly, which
  normalises to an empty tag, so the golden corpus is unaffected.
- FFmpeg's WebM muxer writes ONE untagged chapter title per chapter, so a
  WebM-profile file cannot carry per-language chapter titles even when the
  chapter file names them. The bespoke container carries all of them. The
  authoring library reports the loss in its result's Notes rather than papering
  over it (Jeremy's ruling, 2026-08-29), and the end-to-end tests assert the
  difference in both directions. Matroska CAN express the missing titles as Tags
  attached to a chapter's identifier; writing them is deferred work, not a bug.
- A WebM document has NO element for the hearing-impaired track flag - Matroska
  gained FlagHearingImpaired, WebM's element list never did - so FFmpeg's webm
  muxer writes the default and forced flags and drops that one, even though it is
  on the command line. Measured 2026-08-29 while building the authoring library.
  The bespoke container carries all three flags, and so does -f matroska. The
  authoring library reports this loss in its Notes too.
- FFmpeg writes WebVTT tracks as D_WEBVTT/SUBTITLES with the cue identifier on
  the first line and the settings on the second; mkvmerge writes S_TEXT/WEBVTT
  with the payload in the Block and settings-then-identifier in a BlockAddition.
  The reader handles both by deciding per line which one looks like a settings
  list, rather than trusting either codec id.
- For laced audio with neither DefaultDuration nor BlockDuration, every frame in
  a lace carries its block's timestamp. FFmpeg derives per-frame times from the
  codec's sample count instead. The difference is recorded in Notices; it moves
  where a seek lands, not what is heard.
- Cluster CRC-32 checking is OFF by default (MatroskaReader.VerifyClusterChecksums),
  because verifying a cluster means reading it twice. The metadata checksums are
  always verified.
- The uncompressed decoder is not shipped. Its source is
  tests/CodeBrix.VideoPlayback.Tests/RawVideoDecoder*.cs and the tools project
  LINKS those two files rather than duplicating them.
- CubeLutFile.Parse USED to refuse a domain other than 0..1 and to refuse a file
  stating both LUT_1D_SIZE and LUT_3D_SIZE. Both are now supported (see THE
  COLOUR LOOKUP ENGINE above). The corpus README.txt and MANIFEST.txt in
  tests/assets/LUTs were written against the old behaviour and their two
  descriptions of those files were corrected on 2026-08-29.


NOTES
=====
- The eight AI-agent pointer stubs point at README-INDEX.txt (the family's
  2026-08-24 convention). Do not let a scaffold overwrite them with the older
  "read AGENT-README.txt" wording.
- The .slnx lists AGENT-README.txt, CBV-FORMAT.txt, EXTRAS-README.txt,
  MAINTAINER-README.txt, README-INDEX.txt, README.md, LICENSE,
  THIRD-PARTY-NOTICES.txt, global.json, .gitignore and icon-codebrix-128.png
  under Solution Items, both libraries at the top level, both test projects
  under Tests, the sample under Samples and the tools under Tools.
- The presenter's own AGENT-README lives beside its project, not at the root,
  and is packed to the package ROOT with PackagePath="AGENT-README.txt". The
  root AGENT-README.txt belongs to the core package and stays there.
- A CLIP WHOSE AUDIO ENDS BEFORE ITS VIDEO used to freeze where the sound
  stopped, in two ways that had to be fixed separately. The clock half:
  OnAudioPlaybackEnded now moves the clock onto the session's own stopwatch from
  exactly where the audio left off, and IsUsingFallbackClock decides which one
  is running. The supply half: the reader now says which track has ended and the
  demultiplexer can no longer be stopped by one track's full queue. Both are
  described under PER-TRACK EXHAUSTION above and both are fenced by tests.
================================================================================
