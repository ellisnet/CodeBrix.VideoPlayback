================================================================================
AGENT-README: CodeBrix.VideoPlayback.Authoring
A Guide for AI Coding Agents - CONSUMING the CodeBrix.VideoPlayback.Authoring.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========
CodeBrix.VideoPlayback.Authoring WRITES the files CodeBrix.VideoPlayback plays.

It is the other end of the same program. The playback library opens a ".cbv"
file, demultiplexes it and hands frames to a presenter; this library takes a
video you already have, encodes it, packages it with its captions and its
chapters, and produces the ".cbv" the player expects - in either of that
format's two flavours, from one request object.

    your source video ─► CbvAuthor.Write(request) ─► clip.cbv
      (anything ffmpeg      (ffmpeg for the pixels    (WebM-profile, or
       can decode)           and the sound; CodeBrix   the bespoke CBVF)
                             for the container)

THIS IS A DEVELOPER-MACHINE LIBRARY, AND THAT IS NOT A DISCLAIMER - IT IS THE
DESIGN. It launches ffmpeg as a child process, so it expects an encoder to be
sitting on the machine. An application that PLAYS video needs none of that: it
needs CodeBrix.VideoPlayback, a decoder package and, optionally, a presenter.
Put this package in your build tool, your asset pipeline, your content-authoring
utility - never in the thing you ship to a customer.

WHAT IT ACTUALLY DOES, AND WHY THE SHAPE IS WHAT IT IS

  * TWO FLAVOURS, ONE REQUEST. A ".cbv" file is either a constrained WebM with
    its seek index moved to the front, or the bespoke "CBVF" container. They
    share an extension because a reader sniffs the first four bytes and knows
    which it has. They share a REQUEST here for the same reason: almost every
    decision - the encoder, the frame size, the rate, the captions, the
    chapters - means the same thing either way, and the handful that do not are
    named where they differ.

  * ONE FFMPEG PASS, OR TWO AND A MUX. The WebM-profile flavour is a single
    ffmpeg command: pixels, sound and every caption file go in together and
    ffmpeg's own WebM muxer writes the result. The bespoke flavour is two
    commands into temporary files - the picture as an AV1 elementary stream in
    an IVF wrapper, the sound as an Ogg stream - which the playback library's
    own managed muxer then turns into a CBVF file together with the caption and
    chapter text. The temporary files are deleted whether the run succeeded or
    failed.

  * ONE TOOL, AND ONLY ONE. FFmpeg (ffmpeg and ffprobe) is the only thing that
    has to be installed. Not mkvtoolnix, not a dav1d command-line tool, not
    Python, not anything else. Everything past the encoder is CodeBrix code.
    That is a rule of this program, not an accident of the current version, and
    a step that needed a second tool would be a step designed wrong.

  * A DRY RUN THAT TOUCHES NOTHING. RenderCommands returns the exact command
    lines a request would execute, without running them, without reading the
    source, and without writing a temporary file. It works on a machine with no
    ffmpeg at all. That is what makes the arguments TESTABLE, and it is what
    lets a pipeline show its work before it spends an hour encoding.

  * IT JUDGES WHAT IT WROTE. Every authored file is read back and checked
    against the streamable-profile rules that live in the playback library -
    the same rules the cbvinfo tool prints - and the report comes back in the
    result. By default a file that does not pass is a failure.

  * IT TELLS YOU WHAT IT COULD NOT KEEP. Two things do not survive the
    WebM-profile flavour, and neither is papered over: per-language chapter
    titles and the hearing-impaired caption flag. See THE TWO ASYMMETRIES.

Package references: the playback core, and CodeBrix.VideoProcessing. There is
never a third. No SkiaSharp, no codec package, no Opus: this library writes
files, it does not play them or draw them.

Target framework: .NET 10 or later. License: MIT.


INSTALLATION
============
    dotnet add package CodeBrix.VideoPlayback.Authoring.MitLicenseForever

Or in a project file:

    <PackageReference Include="CodeBrix.VideoPlayback.Authoring.MitLicenseForever" Version="*" />

That pulls in CodeBrix.VideoPlayback (and, through it, CodeBrix.Audio) and
CodeBrix.VideoProcessing. Then install FFMPEG on the machine that will run the
authoring, built with the encoders you intend to name:

    sudo apt install ffmpeg          (a Debian-based machine)
    ffmpeg -encoders | grep -E 'libsvtav1|libaom-av1|libopus|libvorbis'

The library never downloads or installs anything. If ffmpeg is somewhere other
than the PATH, point the video-processing wrapper at it once at start-up:

    using CodeBrix.VideoProcessing;
    GlobalFFOptions.Configure(o => o.BinaryFolder = "/opt/ffmpeg/bin");

CbvAuthor.TryVerifyTools(out string problem) answers "is it there?" without
throwing, and the message it hands back names both binaries and every place they
were looked for.

WHAT NOT TO INSTALL. Nothing else. In particular this package must not appear in
an application that plays video: ffmpeg's own binaries carry licences that the
whole point of this family is to keep out of a shipped app, and they stay on the
developer's machine.


KEY NAMESPACES / USINGS
=======================
    using CodeBrix.VideoPlayback.Authoring;           // CbvAuthor, the request,
                                                      //   the result, the flavour
    using CodeBrix.VideoPlayback.Authoring.Encoding;  // the video and audio settings,
                                                      //   frame sizes, encoder names
    using CodeBrix.VideoPlayback.Authoring.Captions;  // AuthoringCaptionInput
    using CodeBrix.VideoPlayback.Authoring.Effects;   // AuthoringLutInput
    using CodeBrix.VideoPlayback.Authoring.Presets;   // the device-class table
    using CodeBrix.VideoPlayback.Authoring.Commands;  // AuthoringCommand, AuthoringProgress

And, from the playback library, for the types this one borrows rather than
duplicates:

    using CodeBrix.VideoPlayback.Captions;            // CaptionTrackFlags
    using CodeBrix.VideoPlayback.Containers;          // StreamableProfileReport
    using CodeBrix.VideoPlayback.Containers.Cbv;      // CbvAuthoringResult (the mux summary)


CORE API REFERENCE
==================

CbvAuthor (static) - the whole front door
------------------------------------------
    IReadOnlyList<AuthoringCommand> RenderCommands(VideoAuthoringRequest request)
        The dry run. Validates the request, resolves the colour-grade chain to
        the one file ffmpeg would look up, and renders the command lines. Reads
        nothing, writes nothing, needs no ffmpeg. One command for a WebM-profile
        file, two for a bespoke one (one when its audio is left out).

    VideoAuthoringResult Write(VideoAuthoringRequest request)
        The real run. Validates, checks that ffmpeg is there, composes the grade
        chain if it needs composing, encodes, muxes where muxing is needed,
        deletes every temporary file, reads the finished file back and checks it
        against the streamable profile.

    bool TryVerifyTools(out string problem)
    void VerifyTools()
        Is ffmpeg installed? The message names ffmpeg AND ffprobe and says where
        they were looked for - the PATH, or the configured binary folder.

  Everything throws VideoAuthoringException, which derives from the playback
  library's VideoPlaybackException, so one catch covers the family. Every message
  names the piece involved.


VideoAuthoringRequest - one file's worth of decisions
-----------------------------------------------------
    VideoAuthoringFlavour Flavour            WebMProfile (default) | Bespoke
    string SourcePath                        what to encode from
    string OutputPath                        where the finished file goes
    AuthoringVideoSettings Video             the picture (see below)
    AuthoringAudioSettings Audio             the sound (see below)
    IList<AuthoringCaptionInput> Captions    the text tracks
    string ChaptersPath                      an ffmetadata file, or null

    AuthoringContainerFormat Container       WebM (default) | Matroska
    bool CuesToFront                         true by default
    bool SelectStreamsExplicitly             true by default
    bool CopySourceMetadata                  false by default
    bool RequireNoExtraPlaybackPackages      false by default
    bool ValidateProfile                     true by default
    bool FailWhenProfileFails                true by default
    string TemporaryFolder                   null means the system's own
    TimeSpan SourceDuration                  needed for progress
    Action<AuthoringProgress> ProgressCallback

  Container and CuesToFront are WebM-profile settings; the bespoke flavour
  writes its own container and is index-first by construction. Setting Container
  to Matroska and CuesToFront to false produces an ORDINARY .mkv - useful as a
  negative control, and it will not pass the profile, so pair it with
  FailWhenProfileFails = false.

  SelectStreamsExplicitly renders `-map 0:v:0 -map 0:a:0` instead of leaving the
  choice to ffmpeg. Leave it on: a phone recording carries a third, timed-metadata
  stream that nothing downstream wants.

  CopySourceMetadata off renders `-map_metadata -1` and drops the recording's
  creation time and device strings. A chapter file overrides it, because that is
  where the chapters have to come from.

  RequireNoExtraPlaybackPackages refuses a request whose audio would be Opus.
  Opus needs the PLAYING application to reference CodeBrix.Audio.Opus and call
  CodeBrixAudioOpus.Register(); Vorbis needs nothing. Set it when the point of
  the file is that the shipped application carries no Opus binary at all, and the
  refusal happens at authoring time rather than on a customer's machine.


AuthoringVideoSettings - the picture
-------------------------------------
    AuthoringVideoEncoder Encoder            LibSvtAv1 (default) | LibAomAv1
    int SpeedPreset                          0..13; 6 by default
    int ConstantRateFactor                   0..63; 30 by default
    AuthoringFrameSize FrameSize             Source by default
    string ScalerFlags                       "lanczos" by default
    double FrameRate                         0 leaves the source's own alone
    AuthoringFrameRateMode FrameRateMode     Encoder (default) | Filter
    int KeyframeIntervalFrames               0 leaves the encoder's default
    bool AutoRotate                          true by default
    IList<AuthoringLutInput> Luts            the colour grade, in order
    string TrackName                         bespoke flavour only

    const string PixelFormat = "yuv420p"     NOT a setting - see below

  THE PIXEL FORMAT IS PINNED. Every file this library authors is 8-bit 4:2:0,
  which is what the streamable profile recommends and what every decoder and
  every display path handles without a conversion nobody asked for. The chain
  pins it twice - as the last filter and as the encoder's -pix_fmt - so neither
  an unusual source nor a filter that hands on RGB can change it.

  THE SPEED PRESET means -preset for SVT-AV1 (0 slowest and best, 13 fastest and
  worst) and -cpu-used for libaom (0..8). libaom also needs -b:v 0 for the rate
  factor to take effect; this library emits it for you.

  THE FRAME SIZE has three shapes:
        AuthoringFrameSize.Source            no scale filter at all
        AuthoringFrameSize.Exact(w, h)       both numbers, both even
        AuthoringFrameSize.LongSide(n)       n on the longer side, aspect kept
        AuthoringFrameSize.ShortSide(n)      n on the shorter side, aspect kept
    The aspect-preserving forms render an ffmpeg expression rather than a
    number, so they still need no probe of the source: one rung table row -
    "1080 on the long side" - serves a landscape clip and a portrait one. Odd
    dimensions are refused: 4:2:0 chroma has half as many samples per axis, so
    an odd dimension has nowhere to put the last one.

  THE FRAME RATE goes either at the encoder (-r N, which makes the output
  constant-frame-rate and therefore makes a keyframe interval in FRAMES mean a
  fixed number of seconds) or in the filter chain (fps=N, which drops frames
  BEFORE the grade runs, so a lookup is never computed for a frame that is about
  to be thrown away).

  AUTOROTATE is an INPUT option, and the library puts it where ffmpeg will honour
  it - before the -i it belongs to. With it on, a portrait phone recording comes
  out with TRUE portrait pixels and no rotation left for a player to apply.

  THE ONE FILTER CHAIN, and the order it is built in:
        1. scale     the resample first, so everything after works on less
        2. fps       only in Filter mode; drops frames before the expensive part
        3. lut3d     the colour grade; ffmpeg's lookup works in RGB and inserts
                     the conversion itself, exactly as the playback presenter
                     applies its own lookup to RGB after the same conversion
        4. format    LAST, so the encoder is handed 8-bit 4:2:0 whatever the
                     chain did
    ffmpeg keeps only the last -vf it is given, so there is exactly one, always.


AuthoringAudioSettings - the sound
-----------------------------------
    AuthoringAudioCodec Codec                Default | LibOpus | LibVorbis
    bool Include                             true by default
    int BitrateKilobitsPerSecond             128 by default
    double? VorbisQuality                    null means rate-control by bit rate
    int SampleRateHz                         48000 by default
    int Channels                             2 by default
    string Language                          a BCP 47 tag, or null
    string Name                              a menu name, or null

  DEFAULT RESOLVES PER FLAVOUR: Opus for a WebM-profile file, which is what the
  wider world expects of a WebM, and Vorbis for a bespoke one, which is the
  flavour an application ships inside itself. That is the whole reason for the
  split - a Vorbis file plays with the core package alone.

  FFMPEG'S BUILT-IN vorbis ENCODER IS NEVER NAMED. It is experimental and poor.
  This library asks for libvorbis, always.

  libvorbis refuses very low bit rates for stereo - 96 kbit/s and up is safe at
  48 kHz. Below that, use VorbisQuality instead, or fewer channels.


AuthoringCaptionInput - one text track
---------------------------------------
    new AuthoringCaptionInput(path, language, name = null,
                              flags = CaptionTrackFlags.None)
        string Path         string Language      string Name
        CaptionTrackFlags Flags     bool IsWebVtt

  The flags are the playback library's own: Default, Forced, HearingImpaired.
  The bespoke flavour reads WebVTT (.vtt) and SubRip (.srt); the WebM-profile
  flavour takes WebVTT ONLY, and says so rather than producing a file ffmpeg
  would have refused.

  CAPTION TRACKS ARE COPIED, NEVER RE-ENCODED. The command carries `-c:s copy`.
  ffmpeg's webvtt ENCODER discards cue identifiers and positioning settings, so a
  track that went through it would arrive stripped of the parts a player needs.


AuthoringLutInput - one step of a colour grade
-----------------------------------------------
    new AuthoringLutInput(cubePath, applyAtPercent = 100)
        string Path      double ApplyAtPercent      bool HasEffect

  A chain of these is folded into ONE effective table by the playback library's
  own LutComposer - the same code the presenter uses to fold its own chain - so
  a grade baked into a file and the same grade applied live are the same
  arithmetic and therefore the same colour.

  ONE table at 100 percent is handed to ffmpeg as it stands; there is nothing to
  compose. Anything else - two tables, or one at some other percentage - is
  composed and written to a temporary ".cube" file, which ffmpeg looks up and
  the run then deletes. The result records what was composed:
  ComposedLutTitle and ComposedLutSize.

  A table applied at 0 percent is skipped entirely.


VideoAuthoringResult - what came out
-------------------------------------
    string OutputPath                        long SizeInBytes
    VideoAuthoringFlavour Flavour            TimeSpan Elapsed
    IReadOnlyList<AuthoringCommand> Commands the lines that actually ran
    StreamableProfileReport Profile          null when validation was switched off
    bool PassesProfile
    CbvAuthoringResult Mux                   bespoke only; frame and packet counts
    IReadOnlyList<string> Notes              what could not be kept
    string ComposedLutTitle                  int ComposedLutSize


DeviceClassPresets - starting numbers, not limits
--------------------------------------------------
    DeviceClassPresets.Desktop4K     3840 long side, preset 6, crf 28, 128 kbit/s
    DeviceClassPresets.Pi1080p       1920 long side, preset 5, crf 26, 128 kbit/s
    DeviceClassPresets.RiscV720p     1280 long side, preset 4, crf 24,  96 kbit/s
    DeviceClassPresets.All / For(DeviceClass)
    preset.ApplyTo(request)          writes its four numbers and returns the request

  The two rules the table encodes: the speed preset gets FASTER as the frame
  gets bigger, because encode cost scales with the pixel count and that keeps a
  whole ladder's wall clock level; and the rate factor gets LOWER as the frame
  gets smaller, because AV1's rate factor is resolution-relative and the same
  number looks better the more pixels are hiding the error.

  They are a starting point. Apply one and then override anything.


THE TWO ASYMMETRIES
===================
The two flavours are not interchangeable, and where they differ this library
says so in the result's Notes rather than letting you find out later.

1. CHAPTER TITLES ARE SINGLE-LANGUAGE IN THE WEBM-PROFILE FLAVOUR.
   One ffmetadata chapter file serves both flavours, and it may name a title per
   language:

       [CHAPTER]
       TIMEBASE=1/1000
       START=0
       END=12000
       title=Opening
       title-de=Anfang
       title-fr=Ouverture

   The BESPOKE flavour keeps all three. The WEBM-PROFILE flavour keeps only the
   untagged one: ffmpeg's Matroska muxer writes a single, unlanguaged chapter
   title per chapter, so `title-de` and `title-fr` are dropped on the way in.
   The result carries a note naming the languages that were lost.

   This is a limitation of the container path, not a decision, and it is not
   worked around here. Matroska can express the missing titles as Tags attached
   to a chapter's identifier; teaching the pipeline to write them is a separate
   piece of work.

2. THE HEARING-IMPAIRED CAPTION FLAG IS LOST IN THE WEBM-PROFILE FLAVOUR.
   A WebM document has no element for it - Matroska gained one, WebM's element
   list never did - so ffmpeg's WebM muxer writes the default and forced flags
   and silently drops that one. The bespoke flavour keeps all three, and so does
   Container = Matroska. Again the result says which tracks it happened to.

If your captions carry an SDH track, or your chapters are multilingual, author
the BESPOKE flavour.


COMPLETE EXAMPLES
=================

1. The smallest possible authoring run.

    using CodeBrix.VideoPlayback.Authoring;

    VideoAuthoringRequest request = new VideoAuthoringRequest
    {
        SourcePath = "master.mov",
        OutputPath = "clip.cbv",
    };

    VideoAuthoringResult result = CbvAuthor.Write(request);
    Console.WriteLine(result);          // path, size, flavour, profile verdict

2. Show the command line without running it.

    foreach (AuthoringCommand command in CbvAuthor.RenderCommands(request))
    {
        Console.WriteLine(command);     // "ffmpeg -autorotate -i ... -f webm ..."
    }

3. A clip to ship inside an application: bespoke, Vorbis, nothing extra needed.

    VideoAuthoringRequest request = new VideoAuthoringRequest
    {
        Flavour = VideoAuthoringFlavour.Bespoke,
        SourcePath = "master.mov",
        OutputPath = "intro.cbv",
        RequireNoExtraPlaybackPackages = true,
    };

    DeviceClassPresets.Pi1080p.ApplyTo(request);
    request.Video.FrameRate = 30;
    request.Video.KeyframeIntervalFrames = 60;

    CbvAuthor.Write(request);

4. A ladder: the same source at three device classes.

    foreach (DeviceClassPreset preset in DeviceClassPresets.All)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = "master.mov",
            OutputPath = $"intro-{preset.LongSidePixels}.cbv",
        };

        preset.ApplyTo(request);
        CbvAuthor.Write(request);
    }

5. Captions and chapters.

    request.Captions.Add(new AuthoringCaptionInput(
        "captions.en.vtt", "en", "English", CaptionTrackFlags.Default));
    request.Captions.Add(new AuthoringCaptionInput(
        "captions.en.sdh.vtt", "en", "English SDH", CaptionTrackFlags.HearingImpaired));
    request.Captions.Add(new AuthoringCaptionInput(
        "captions.de.vtt", "de", "Deutsch"));
    request.ChaptersPath = "chapters.ffmeta";

    VideoAuthoringResult result = CbvAuthor.Write(request);
    foreach (string note in result.Notes) Console.WriteLine("note: " + note);

6. A colour grade, baked in, that matches what the player would have shown.

    request.Video.Luts.Add(new AuthoringLutInput("looks/teal-orange.cube", 60));
    request.Video.Luts.Add(new AuthoringLutInput("looks/warm.cube", 25));

    VideoAuthoringResult result = CbvAuthor.Write(request);
    Console.WriteLine(result.ComposedLutTitle + " at " + result.ComposedLutSize);

  The same two files at the same two percentages, handed to the presenter's
  Effects collection, produce the same picture - one composer, one answer.

7. Progress.

    request.SourceDuration = FFProbe.Analyse(request.SourcePath).Duration;
    request.ProgressCallback = p => Console.WriteLine(p);   // "video pass (1 of 2): 40%"

8. An ordinary .mkv, on purpose, as a negative control.

    request.Container = AuthoringContainerFormat.Matroska;
    request.CuesToFront = false;
    request.FailWhenProfileFails = false;

    VideoAuthoringResult result = CbvAuthor.Write(request);
    foreach (StreamableProfileRule rule in result.Profile.FailedRules())
    {
        Console.WriteLine(rule);       // "[FAIL] cues sit before the first cluster"
    }

9. Fail early on a machine with no encoder.

    if (!CbvAuthor.TryVerifyTools(out string problem))
    {
        Console.Error.WriteLine(problem);
        return 1;
    }

10. Check a file somebody else made.

    using CodeBrix.VideoPlayback.Containers;

    StreamableProfileReport report = StreamableProfile.EvaluateFile("theirs.webm");
    Console.WriteLine(report);          // the whole report, as cbvinfo prints it


MINIMUM VIABLE PROJECT TEMPLATE
===============================
A console tool that turns a folder of masters into a folder of .cbv files.

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.VideoPlayback.Authoring.MitLicenseForever" Version="*" />
      </ItemGroup>
    </Project>

    using CodeBrix.VideoPlayback.Authoring;
    using CodeBrix.VideoPlayback.Authoring.Presets;

    if (!CbvAuthor.TryVerifyTools(out string problem))
    {
        Console.Error.WriteLine(problem);
        return 1;
    }

    foreach (string master in Directory.GetFiles(args[0], "*.mov"))
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = master,
            OutputPath = Path.Combine(args[1], Path.GetFileNameWithoutExtension(master) + ".cbv"),
        };

        DeviceClassPresets.Desktop4K.ApplyTo(request);
        Console.WriteLine(CbvAuthor.Write(request));
    }

    return 0;


PERFORMANCE TIPS
================
  * THE SPEED PRESET IS THE BIG KNOB, and it is not linear. On SVT-AV1 the step
    from preset 4 to preset 6 is most of the wall clock on a 2160p rung and very
    little of the quality. Encode a ladder with the faster preset at the top.

  * PUT THE FRAME-RATE CAP IN THE FILTER CHAIN when there is a grade. In Filter
    mode the fps filter drops frames before lut3d runs; in Encoder mode the
    lookup is computed for frames the encoder then discards.

  * COMPOSE THE GRADE ONCE. A chain of ten tables costs ffmpeg exactly one
    lookup, because this library folds it before ffmpeg ever sees it.

  * PUT TemporaryFolder ON THE SAME VOLUME AS THE OUTPUT for large bespoke
    files: the intermediates are the whole elementary stream, and the muxer
    reads them back.

  * ONE PROCESS AT A TIME PER FILE, SEVERAL FILES AT A TIME. SVT-AV1 already
    uses every core; running two encodes side by side rarely helps and can hurt.
    Author a ladder in sequence and let the encoder have the machine.

  * REUSE A DRY RUN. RenderCommands is free; call it in a test, record its
    output in your manifest, and you have a reproducible record of exactly what
    produced each asset.


COMMON PITFALLS TO AVOID
========================
  * DO NOT SHIP THIS PACKAGE IN AN APPLICATION. It expects an ffmpeg on the
    machine, and ffmpeg's binaries carry the licences this family exists to keep
    out of a shipped app. Playing needs CodeBrix.VideoPlayback and a decoder.

  * DO NOT ASSUME A REQUEST OBJECT IS REUSABLE ACROSS THREADS. It is a plain
    settings object with no locking. Build one per file.

  * DO NOT PUT A SUBRIP FILE IN A WEBM-PROFILE REQUEST. It is refused, with a
    message, rather than handed to ffmpeg to fail on. The bespoke flavour reads
    SubRip.

  * DO NOT EXPECT MULTILINGUAL CHAPTER TITLES OR AN SDH FLAG TO SURVIVE THE
    WEBM-PROFILE FLAVOUR. See THE TWO ASYMMETRIES. The result's Notes say so
    every time it happens; read them.

  * DO NOT SET A LOW VORBIS BIT RATE FOR STEREO. libvorbis refuses to open below
    roughly 64 kbit/s at 48 kHz, and the failure comes from ffmpeg rather than
    from a validation here. Use 96 and up, or VorbisQuality.

  * DO NOT PASS AN ODD FRAME DIMENSION. It is refused at the point of
    construction, because 4:2:0 chroma has no home for the last sample.

  * DO NOT CALL Write TWICE WITH THE SAME OutputPath CONCURRENTLY. The temporary
    file names are derived from the output's name, so two runs writing the same
    file would fight over them.

  * DO NOT READ THE COMMAND LINE AS A PROMISE ABOUT THE TEMPORARY FILES. They
    are named deterministically so that a dry run and a real run agree, but they
    are deleted when the run ends, successfully or not.

  * REMEMBER THAT AN OFF-THE-SHELF FILE WILL NOT PASS THE PROFILE. A plain
    Matroska with its cues at the end fails one rule on purpose. Pair
    Container = Matroska with FailWhenProfileFails = false.


WHAT THIS PACKAGE DOES NOT DO
=============================
  * IT DOES NOT DECODE OR PLAY ANYTHING. It has no decoder and no presenter, and
    it never opens an audio device.

  * IT DOES NOT EDIT. No trimming, no joining, no overlays, no transitions. It
    encodes one source into one output. The video-processing wrapper underneath
    it does all of that, and is a public package in its own right.

  * IT DOES NOT WRITE MP4, H.264, HEVC OR AAC. The whole family is built around
    a royalty-free codec pair, and authoring a file the player cannot open would
    be a strange thing to help with.

  * IT DOES NOT RENDER CAPTIONS INTO THE PICTURE. Caption tracks ride along as
    text, which is what makes them selectable, translatable and searchable.

  * IT DOES NOT TONE-MAP HDR. A high-dynamic-range source is encoded as it is
    found; the playback family presents it as BT.709 with one logged warning.

  * IT DOES NOT PROBE YOUR SOURCE FOR YOU. Where a decision needs the source's
    duration - progress reporting - you supply it. FFProbe.Analyse, from the
    video-processing package, is one call away.

  * IT DOES NOT WRITE MULTILINGUAL CHAPTER TITLES INTO A WEBM. See THE TWO
    ASYMMETRIES.


WORKING EXAMPLES ON GITHUB
==========================
  https://github.com/ellisnet/CodeBrix.VideoPlayback

  tools/CodeBrix.VideoPlayback.AssetAuthoring
      The reference consumer: it builds the repository's own sample-video corpus
      - twenty-four files in four container profiles from two source clips -
      entirely through this library, verifies every one of them, and writes a
      manifest recording the exact command lines. Read CorpusEncoder.cs to see a
      plan entry turned into a request.

  tests/CodeBrix.VideoPlayback.Authoring.Tests
      Every argument decision asserted as a string with no ffmpeg running, then
      the same requests run for real against clips generated on the spot, then
      the results PLAYED with a decoder registered.


QUICK REFERENCE CARD
====================
TASK                                  DO THIS
------------------------------------  ------------------------------------------
Author a WebM-profile .cbv            CbvAuthor.Write(request)
Author a bespoke .cbv                 request.Flavour = VideoAuthoringFlavour.Bespoke
See the command without running it    CbvAuthor.RenderCommands(request)
Check ffmpeg is installed             CbvAuthor.TryVerifyTools(out string problem)
Start from a device class             DeviceClassPresets.Pi1080p.ApplyTo(request)
Resize keeping the aspect ratio       Video.FrameSize = AuthoringFrameSize.LongSide(1920)
Resize to an exact frame              Video.FrameSize = AuthoringFrameSize.Exact(1920, 1080)
Pin the frame rate                    Video.FrameRate = 30
Drop frames before the grade          Video.FrameRateMode = AuthoringFrameRateMode.Filter
Make scrubbing quick                  Video.KeyframeIntervalFrames = 60
Use the reference encoder             Video.Encoder = AuthoringVideoEncoder.LibAomAv1
Bake one colour grade                 Video.Luts.Add(new AuthoringLutInput(path))
Bake a chain, dialled back            Video.Luts.Add(new AuthoringLutInput(path, 40))
Choose the audio codec                Audio.Codec = AuthoringAudioCodec.LibVorbis
Rate-control Vorbis by quality        Audio.VorbisQuality = 5
Leave the sound out                   Audio.Include = false
Add a caption track                   Captions.Add(new AuthoringCaptionInput(p, "en", n, flags))
Add chapters                          request.ChaptersPath = "chapters.ffmeta"
Refuse anything needing Opus          request.RequireNoExtraPlaybackPackages = true
Author a deliberate non-profile file  Container = Matroska; CuesToFront = false;
                                        FailWhenProfileFails = false
Report progress                       SourceDuration + ProgressCallback
See what could not be kept            result.Notes
See the profile report                result.Profile
Judge somebody else's file            StreamableProfile.EvaluateFile(path)
================================================================================
