================================================================================
AGENT-README: CodeBrix.VideoPlayback
A Guide for AI Coding Agents — CONSUMING the CodeBrix.VideoPlayback.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========
CodeBrix.VideoPlayback plays video in .NET without a royalty-bearing codec, a
copyleft binary, or an MP4-family dependency anywhere in the shipped
application.

It reads two container families:

  * WebM and Matroska (.webm, .mkv) carrying AV1 video with Opus or Vorbis
    audio and any number of text caption tracks;
  * ".cbv" - a bespoke container this package writes and reads, laid out so that
    the whole index and every caption cue sit in front of the media data.

It gives you the machinery around a codec, and no codec: a demultiplexer, a
playback session with a transport and a clock, a zero-copy frame-buffer pool, a
one-slot frame mailbox, a managed SIMD YUV-to-BGRA converter, and an authoring
muxer. The video decoder itself arrives as a separate package and registers
itself; audio plays through CodeBrix.Audio.

What that buys you: this package has NO native binaries, NO drawing dependency,
and exactly one NuGet dependency. It runs anywhere .NET 10 runs.

Target framework: .NET 10 or later. License: MIT.

    application ─► VideoPlaybackSession ─► VideoFramePresenter ─► your view
                          │                        (newest frame)
                          ├─ container reader (WebM/Matroska or .cbv)
                          ├─ IVideoDecoder    (from a decoder package)
                          └─ PacketAudioPlayer (CodeBrix.Audio)

WHAT YOU MUST REGISTER. A file whose codec has no decoder fails with a message
naming the package to add. Nothing is guessed at and nothing is reflected on.

  video "av01"     needs an AV1 decoder package registered with
                   VideoDecoders.Register(...)
  audio "vorbis"   works out of the box - CodeBrix.Audio has it built in
  audio "opus"     needs CodeBrix.Audio.Opus referenced and
                   CodeBrixAudioOpus.Register() called
  video "raw"      uncompressed video: no decoder ships for it either (it is a
                   test and diagnostics codec)


INSTALLATION
============
    dotnet add package CodeBrix.VideoPlayback.MitLicenseForever

Or in a project file:

    <PackageReference Include="CodeBrix.VideoPlayback.MitLicenseForever" Version="*" />

Its only dependency is CodeBrix.Audio.MitLicenseForever, which is pulled in for
you. Add, as your application needs them:

  * an AV1 decoder package, to play AV1 video;
  * CodeBrix.Audio.Opus.BsdLicenseForever, to play Opus audio;
  * CodeBrix.VideoPlayback.Skia.MitLicenseForever, if you would rather have
    frames drawn for you than draw them yourself. It adds one class,
    SkiaVideoPresenter, which composes the newest frame on an off-screen surface
    - on the graphics device through a single shader, or on the processor
    through the converter above - lets you draw over it, and blits it into
    whatever canvas your application owns. Its own consumer guide is
    src/CodeBrix.VideoPlayback.Skia/AGENT-README.txt in the repository, and
    AGENT-README.txt at the root of its package.

Nothing else. There is no native binary in this package and no platform-specific
build.


KEY NAMESPACES / USINGS
=======================
    using CodeBrix.VideoPlayback;               // VideoPlaybackSession, VideoPlaybackOptions,
                                                //   VideoPlaybackException
    using CodeBrix.VideoPlayback.Playback;      // VideoPlaybackState, VideoSeekMode, the event args
    using CodeBrix.VideoPlayback.Presentation;  // VideoFramePresenter
    using CodeBrix.VideoPlayback.Frames;        // VideoFrame, VideoFramePlane, the buffer pool
    using CodeBrix.VideoPlayback.Decoding;      // VideoDecoders, IVideoDecoder, colour metadata
    using CodeBrix.VideoPlayback.Color;         // VideoFrameConverter, BgraFrameBufferPool
    using CodeBrix.VideoPlayback.Captions;      // CaptionTrack, CaptionCue, CaptionFiles
    using CodeBrix.VideoPlayback.Chapters;      // Chapter, FfMetadataChapters
    using CodeBrix.VideoPlayback.Sources;       // IMediaSource and the five ways to open one
    using CodeBrix.VideoPlayback.Containers;    // MediaTrackInfo, MediaPacket, IMediaContainerReader
    using CodeBrix.VideoPlayback.Containers.Cbv;       // CbvReader, CbvMuxer, CbvAuthoring
    using CodeBrix.VideoPlayback.Containers.Matroska;  // MatroskaReader
    using CodeBrix.VideoPlayback.Containers.Ogg;       // OggAudioStream (authoring)
    using CodeBrix.VideoPlayback.Containers.Ivf;       // IvfReader (authoring)
    using CodeBrix.VideoPlayback.Codecs;        // Av1Bitstream, RawVideoFormat

Playing a file needs the first three. Everything else is there when you want it.


CORE API REFERENCE
==================

VideoPlaybackSession (CodeBrix.VideoPlayback) - the player
---------------------------------------------------------
    new VideoPlaybackSession()
    new VideoPlaybackSession(VideoPlaybackOptions options)

    void Open(string pathOrUrl)
    void Open(string pathOrUrl, FileSourceMode mode)
    void Open(Stream stream, string name = null, bool leaveOpen = false)
    void Open(PreloadedClip clip)
    void Open(IMediaSource mediaSource, bool leaveOpen = false)

    void Play()                 void Pause()      void Stop()
    void Seek(TimeSpan position)
    void Close()                void Dispose()

    TimeSpan Position { get; }          TimeSpan Duration { get; }
    bool IsPlaying { get; }             bool IsOpen { get; }
    bool IsLooping { get; set; }
    float Volume { get; set; }          bool IsMuted { get; set; }
    VideoPlaybackState State { get; }
    VideoPlaybackOptions Options { get; }

    IReadOnlyList<MediaTrackInfo> Tracks { get; }
    MediaTrackInfo VideoTrack { get; }   MediaTrackInfo AudioTrack { get; }
    VideoStreamInfo VideoStreamInfo { get; }
    IReadOnlyList<string> Notices { get; }

    VideoFramePresenter Presenter { get; }
    IVideoFrameBufferPool BufferPool { get; }

    IReadOnlyList<CaptionTrack> CaptionTracks { get; }
    CaptionTrack SelectedCaptionTrack { get; set; }     // null = captions off
    bool ShowForcedCaptions { get; set; }               // default true
    IReadOnlyList<CaptionCue> ActiveCues { get; }

    IReadOnlyList<Chapter> Chapters { get; }
    Chapter CurrentChapter { get; }
    void SeekToChapter(int index)
    bool NextChapter()        bool PreviousChapter()
    string TitleFor(Chapter chapter, IReadOnlyList<string> preferredLanguages)

    void RegisterDecoderFactory(IVideoDecoderFactory factory)   // this session only

    event EventHandler MediaOpened
    event EventHandler<VideoPositionChangedEventArgs> PositionChanged
    event EventHandler PlaybackEnded
    event EventHandler<MediaFailedEventArgs> MediaFailed
    event EventHandler<VideoFrameReadyEventArgs> FrameReady
    event EventHandler CaptionCuesChanged
    event EventHandler<ChapterChangedEventArgs> ChapterChanged

VideoPlaybackOptions - the knobs, all with working defaults
-----------------------------------------------------------
    VideoSeekMode SeekMode              Exact (default) | KeyFrameOnly
    int VideoQueueCapacity              32
    int AudioQueueCapacity              128
    long MaxTrackParkingBytes           32 MB per track
    TimeSpan PositionUpdateInterval     100 ms
    TimeSpan LateFrameTolerance         40 ms
    int ConsecutiveLateFramesBeforeSkip 4
    bool PlayAudio                      true
    int AudioSampleRate                 48000  (0 = leave the shared output alone)
    bool DecodeAheadWhilePaused         true
    VideoDecoderOptions DecoderOptions  Threads, MaxFrameDelay, FrameSizeLimit,
                                        ApplyFilmGrain (the pool is filled in for you)

VideoFramePresenter (…Presentation) - the hand-off to whatever draws
--------------------------------------------------------------------
    bool TryTakeLatest(out VideoFrame frame)   // you now own the frame; dispose it
    void Post(VideoFrame frame)                // the presenter takes its own reference
    bool HasFrame { get; }
    TimeSpan LastPresentedTimestamp { get; }
    event EventHandler Invalidated
    VideoFramePresenterStatistics GetStatistics()   // Posted/Presented/Superseded/Late
    void NotifyLateFrameDropped(int count = 1)
    void Clear()   void ResetStatistics()   void Dispose()

VideoFrame (…Frames) - one decoded picture, reference-counted
--------------------------------------------------------------
    VideoFramePlane Y, U, V           // IntPtr Data, int Stride, Width, Height,
                                      //   BytesPerSample; GetRowBytes(row)
    VideoFrameBuffer Buffer
    int Width, Height, DisplayWidth, DisplayHeight
    VideoPixelLayout Layout           // Gray | I420 | I422 | I444
    int BitDepth, MaxSampleValue, ChromaShiftX, ChromaShiftY
    TimeSpan Timestamp                long PresentationTimestamp, FrameNumber
    bool IsKeyFrame
    VideoColorInfo Color              HdrMetadata Hdr        VideoFrameInfo Info
    int ReferenceCount { get; }
    VideoFrame Retain()               // +1; the SAME object, dispose it in turn
    void Dispose()                    // -1; at zero the buffer goes back to the pool
    static VideoFrame Create(VideoFrameBuffer buffer, in VideoFrameInfo info,
                             IVideoFrameBufferPool pool)

VideoFrameConverter (…Color) - planar YUV to BGRA on the CPU
------------------------------------------------------------
    static int GetBgraStride(int width)
    static int GetBgraBufferSize(int width, int height)
    static void ToBgra32(VideoFrame frame, Span<byte> destination, int destinationStride)
    static void ToBgra32(in VideoFramePlane y, in VideoFramePlane u, in VideoFramePlane v,
                         int width, int height, VideoPixelLayout layout, int bitDepth,
                         in VideoColorInfo color, Span<byte> destination, int destinationStride)
    static bool IsHardwareAccelerated { get; }

    BgraFrameBufferPool: Rent(width, height) / Return(buffer) / Allocations / Pooled
    BgraFrameBuffer: IntPtr Data, int Width/Height/Stride, Span<byte> AsSpan(), GetRow(row)

Sources (…Sources) - five ways in
----------------------------------
    MediaSources.Open(pathOrUrl, FileSourceMode.Streaming | MemoryMapped | Preloaded)
    new FileMediaSource(path)              streams from disk, seeks with the file system
    new MemoryMappedMediaSource(path)      free seeks, the operating system pages it
    PreloadedClip.FromFile(path)           whole file in a pooled buffer; .OpenSource()
    HttpMediaSource.Create(uri)            byte ranges when the server has them,
                                           progressive download when it does not
    new StreamMediaSource(stream)          any stream; seekable ones can seek
    new MemoryMediaSource(bytes)           bytes you already have

Decoder registration (…Decoding)
---------------------------------
    VideoDecoders.Register(IVideoDecoderFactory factory)     // process-wide, idempotent
    VideoDecoders.Unregister(factory)                        // returns true if it was there
    VideoDecoders.IsCodecSupported("av01")
    VideoDecoders.RegisteredFactories                        // highest priority first
    session.RegisterDecoderFactory(factory)                  // this session only, tried first
    VideoCodecIds.Av1 / Opus / Vorbis / WebVtt / SubRip / Ass / Raw

Containers, for reading a file without playing it
--------------------------------------------------
    new MatroskaReader(IMediaSource source, bool leaveSourceOpen = false)
    new CbvReader(IMediaSource source, bool leaveSourceOpen = false)
    Both implement IMediaContainerReader:
        FormatName, Duration, CanSeek, Tracks, CaptionTracks, Chapters, Notices
        bool TryReadPacket(out MediaPacket packet)
        bool IsTrackExhausted(int trackId)
        TimeSpan? GetTrackEndTimestamp(int trackId)
        TimeSpan Seek(TimeSpan position, int keyFrameTrackId)

    IsTrackExhausted answers true only when the reader can PROVE that a track
    has no packets left. CbvReader knows from its index, exactly and before it
    has read anything, so it can tell you the sound has finished while the
    picture is still running. MatroskaReader can only be certain once it has
    read the whole file, because nothing in Matroska records where a track stops
    - cues index key frames, usually of the video track alone. False therefore
    means "not proven", never "there is definitely more".
    Sniff first: MatroskaReader.IsMatroska(firstFourBytes), CbvReader.IsCbv(firstFourBytes)

Authoring a .cbv file
----------------------
    CbvAuthoring.Write(new CbvAuthoringRequest { OutputPath, VideoIvfPath,
        AudioOggPath, ChaptersPath, Captions, AudioLanguage, VideoName, AudioName })
    CbvMuxer.Create(path) - AddVideoTrack / AddAudioTrack / AddCaptionTrack /
        AddChapters / WriteChunk / Complete, for building one packet at a time
    CaptionFiles.ReadFile(path, id, language, name, flags)   // .vtt and .srt
    FfMetadataChapters.ReadFile(path)                        // FFmpeg's chapter format


COMPLETE EXAMPLES
=================

1. Play a file and repaint a view when a frame arrives.

    using System;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Frames;

    VideoPlaybackSession session = new VideoPlaybackSession();
    session.MediaOpened += (s, e) => Console.WriteLine($"{session.Duration} of video");
    session.MediaFailed += (s, e) => Console.WriteLine(e.Message);
    session.FrameReady += (s, e) => myView.Invalidate();   // mark dirty; draw later

    session.Open("clip.cbv");
    session.Play();

    // ... on the thread that draws, inside your paint handler:
    if (session.Presenter.TryTakeLatest(out VideoFrame frame))
    {
        using (frame)
        {
            // frame.Y / frame.U / frame.V are the planes; see example 2.
        }
    }

2. Turn the newest frame into BGRA pixels you can blit.

    using System;
    using CodeBrix.VideoPlayback.Color;
    using CodeBrix.VideoPlayback.Frames;

    BgraFrameBufferPool pixels = new BgraFrameBufferPool();

    if (session.Presenter.TryTakeLatest(out VideoFrame frame))
    {
        using (frame)
        {
            BgraFrameBuffer surface = pixels.Rent(frame.Width, frame.Height);
            try
            {
                VideoFrameConverter.ToBgra32(frame, surface.AsSpan(), surface.Stride);
                // surface.Data is a 64-byte-aligned pointer to Width * Height BGRA pixels.
            }
            finally
            {
                pixels.Return(surface);
            }
        }
    }

3. Register a decoder package and play AV1.

    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Decoding;

    // At start-up, once. The decoder package supplies the factory.
    VideoDecoders.Register(SomeAv1DecoderPackage.Factory);

    VideoPlaybackSession session = new VideoPlaybackSession();
    session.Open("https://example.invalid/clip.webm");
    session.Play();

4. Play Opus audio.

    using CodeBrix.Audio.Opus;   // from CodeBrix.Audio.Opus.BsdLicenseForever
    using CodeBrix.Audio.Wave;
    using CodeBrix.VideoPlayback;

    SharedAudioOutput.Configure(48000);   // before the first sound of any kind
    CodeBrixAudioOpus.Register();

    VideoPlaybackSession session = new VideoPlaybackSession();
    session.Open("clip.webm");
    session.Play();

5. A clip that ships with the application, loaded once and played over and over.

    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Sources;

    PreloadedClip logo = PreloadedClip.FromFile("assets/logo.cbv");   // at start-up

    VideoPlaybackSession session = new VideoPlaybackSession();
    session.Open(logo);          // no file-system work at all
    session.IsLooping = true;
    session.Play();

6. Scrub, and choose how precise the landing has to be.

    using System;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Playback;

    VideoPlaybackOptions options = new VideoPlaybackOptions
    {
        SeekMode = VideoSeekMode.Exact,          // KeyFrameOnly is instant but coarse
        PositionUpdateInterval = TimeSpan.FromMilliseconds(50),
    };

    VideoPlaybackSession session = new VideoPlaybackSession(options);
    session.PositionChanged += (s, e) => scrubber.Value = e.Position.TotalSeconds;
    session.Open("clip.mkv");
    session.Seek(TimeSpan.FromMinutes(3));

7. Show captions.

    using System;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Captions;

    foreach (CaptionTrack track in session.CaptionTracks)
    {
        Console.WriteLine($"{track.Language} {track.Name} {track.Flags}");
    }

    session.SelectedCaptionTrack = session.CaptionTracks[0];   // null switches them off

    session.CaptionCuesChanged += (s, e) =>
    {
        foreach (CaptionCue cue in session.ActiveCues)
        {
            Console.WriteLine(cue.Text);       // cue.Settings holds the raw placement string
        }
    };

8. Walk the chapters.

    using System;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Chapters;

    string[] preferred = new[] { "fr", "en" };

    foreach (Chapter chapter in session.Chapters)
    {
        Console.WriteLine($"{chapter.Start} {session.TitleFor(chapter, preferred)}");
    }

    session.ChapterChanged += (s, e) => Console.WriteLine($"now in {e.Chapter?.Index}");
    session.SeekToChapter(2);
    session.NextChapter();

9. Author a .cbv file from an encoder's output.

    using CodeBrix.VideoPlayback.Captions;
    using CodeBrix.VideoPlayback.Containers.Cbv;

    CbvAuthoringRequest request = new CbvAuthoringRequest
    {
        OutputPath = "clip.cbv",
        VideoIvfPath = "video.ivf",     // an AV1 elementary stream
        AudioOggPath = "audio.ogg",     // Ogg Opus or Ogg Vorbis
        ChaptersPath = "chapters.ffmeta",
        AudioLanguage = "en",
    };

    request.Captions.Add(new CbvCaptionInput(
        "captions.en.vtt", "en", "English", CaptionTrackFlags.Default));

    CbvAuthoringResult result = CbvAuthoring.Write(request);

10. Read a file's structure without playing it.

    using System;
    using CodeBrix.VideoPlayback.Containers;
    using CodeBrix.VideoPlayback.Containers.Matroska;
    using CodeBrix.VideoPlayback.Sources;

    using MatroskaReader reader = new MatroskaReader(new FileMediaSource("clip.webm"));

    Console.WriteLine($"{reader.DocType} {reader.Duration}");
    foreach (MediaTrackInfo track in reader.Tracks) Console.WriteLine(track);

    while (reader.TryReadPacket(out MediaPacket packet))
    {
        // packet.Data is borrowed until the next call - copy it to keep it.
    }


MINIMUM VIABLE PROJECT TEMPLATE
===============================
Project file:

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.VideoPlayback.MitLicenseForever" Version="*" />
      </ItemGroup>
    </Project>

Program.cs - plays a file to the end and reports what it saw. It needs no video
decoder because it never asks for one: it reads the container and the audio only.

    using System;
    using System.Threading;
    using CodeBrix.VideoPlayback;
    using CodeBrix.VideoPlayback.Containers;
    using CodeBrix.VideoPlayback.Containers.Matroska;
    using CodeBrix.VideoPlayback.Sources;

    using MatroskaReader reader = new MatroskaReader(new FileMediaSource(args[0]));

    Console.WriteLine($"{reader.FormatName}, {reader.Duration}");
    foreach (MediaTrackInfo track in reader.Tracks) Console.WriteLine($"  {track}");
    foreach (string notice in reader.Notices) Console.WriteLine($"  note: {notice}");

    int packets = 0;
    while (reader.TryReadPacket(out MediaPacket _)) packets++;
    Console.WriteLine($"{packets} packets");

To play it as well, register a video decoder package and swap the body for
example 1.


PERFORMANCE TIPS
================
- CALL SharedAudioOutput.Configure(48000) AT START-UP. Media carries 48 kHz, it
  is the only rate Opus decodes at, and when the device runs at the media's rate
  no rate conversion happens at all. An application that has already played a
  44.1 kHz sound effect has started the shared output at 44.1 kHz, and every
  video after that runs through an interpolator.

- TAKE THE FRAME ON THE DRAWING THREAD, NOT IN THE EVENT. FrameReady fires on
  the decoding thread. Mark the view dirty there and call
  Presenter.TryTakeLatest inside your paint handler - that way you always draw
  the newest frame instead of whichever one was current when the event fired,
  and you never block decoding.

- DISPOSE EVERY FRAME YOU TAKE, EXACTLY ONCE. A frame you hold is a buffer the
  pool cannot reuse. Holding two or three is normal; holding a hundred is a
  leak.

- CHOOSE THE SOURCE MODE FOR THE JOB. A long film: FileSourceMode.Streaming, or
  MemoryMapped if it is seeked about a lot. A short clip played repeatedly:
  PreloadedClip, loaded once at start-up. Over a network: HttpMediaSource - and
  prefer files whose index is at the FRONT, because those open in one request.

- REUSE THE BGRA BUFFER. BgraFrameBufferPool exists so that converting a frame
  allocates nothing after the first one. Rent, convert, blit, return.

- WATCH THE COUNTERS RATHER THAN GUESSING. PinnedFrameBufferPool.GetStatistics()
  should show Allocations flat after the first few frames; a rising count means
  the frame size is changing or frames are being held. The presenter's
  Superseded count rising is the display being slower than the video, which is
  fine; its Late count rising is the DECODER falling behind, which is not.

- 8-BIT 4:2:0 IS THE FAST PATH through the CPU converter. 10-bit and 12-bit
  content is converted correctly but costs more and is reduced to 8-bit output.

- MaxTrackParkingBytes IS NOT A BUFFER YOU NEED TO TUNE. Nothing is parked while
  a file interleaves its tracks normally. It only matters for a file whose tracks
  are badly out of step - a long stretch of picture with no sound in it, say -
  where it is what lets the demultiplexer keep reading instead of stopping. If a
  file ever hits the budget you get a Notice naming this option.

- A BIGGER VideoQueueCapacity buys smoother playback from a slow or distant
  source; a smaller one shortens the delay between a seek and a picture.


COMMON PITFALLS TO AVOID
========================
- FORGETTING TO REGISTER A DECODER. This package ships none. An AV1 file opened
  with nothing registered throws with the exact words "video codec 'av01' has no
  registered decoder" and the name of the package to add. That is the message,
  not a bug.

- FORGETTING CodeBrixAudioOpus.Register() FOR OPUS. Vorbis is built into
  CodeBrix.Audio and needs nothing; Opus is a separate package because its
  licence is different. Same shape of message.

- USING A FRAME AFTER DISPOSING IT. A frame is reference-counted, and at zero its
  buffer goes straight back to the pool for a decoder to overwrite. If you hand a
  frame to something that outlives your scope, give it Retain() and let it
  dispose that.

- KEEPING A MediaPacket's BYTES. MediaPacket.Data points into the reader's own
  buffer and is valid only until the next TryReadPacket call. Copy it if you want
  to keep it.

- EXPECTING TO SEEK A PROGRESSIVE DOWNLOAD. A server with no byte-range support
  gives you one forward-only read. Seek throws NotSupportedException saying so,
  and IMediaSource.CanSeek told you in advance.

- EXPECTING TO SEEK A FILE WITH NO INDEX. A Matroska file without Cues cannot be
  seeked either; IMediaContainerReader.CanSeek is the check.

- READING Position FROM SEVERAL PLACES AND EXPECTING THEM TO AGREE EXACTLY. When
  there is an audio track, Position is the audio clock - what a listener is
  actually hearing - and it advances in the mixer's own steps, not smoothly.

- ASSUMING THE FIRST FRAME IS AT ZERO AFTER A SEEK IN KeyFrameOnly MODE. It lands
  on the key frame at or before the moment you asked for, which can be a second
  earlier. Use VideoSeekMode.Exact when the frame matters.

- CALLING Open TWICE WITHOUT Close. Open closes whatever was open first, which
  stops its threads; that is fine, but it is not instantaneous, so do not do it
  in a tight loop.

- TREATING A "notice" AS AN ERROR. Notices list things that were stepped over on
  purpose - a bitmap subtitle track, rotation metadata - and the file still
  plays. VideoPlaybackSession.Notices adds the session's own to the reader's.

- EXPECTING PlaybackEnded WHEN THE PICTURE STOPS. It fires at the LATER of the
  two ends. A clip whose sound runs a second past its last frame keeps that
  frame on screen and plays the sound out; a clip whose picture outlasts its
  sound carries on to the end of the picture. Both are ordinary shapes and both
  end exactly once.

- HDR CONTENT IS NOT TONE-MAPPED. A high-dynamic-range stream decodes correctly
  and is converted as if it were BT.709, which looks flat. VideoColorInfo.
  IsHighDynamicRange tells you when that is happening.


WHAT THIS PACKAGE DOES NOT DO
=============================
- IT DOES NOT DECODE VIDEO. There is no codec here at all: the decoder seam is
  the product. A decoder package supplies the codec.
- IT DOES NOT DRAW. There is no drawing surface, no bitmap type, no dependency on
  any drawing library. It hands you planes and, if you want them, BGRA pixels.
- No MP4 / ISOBMFF, no H.264, no HEVC, no AAC.
- No hardware or GPU decoding.
- No HDR tone-mapping.
- No playback rate other than 1.0.
- No streaming protocol beyond plain HTTP and HTTPS - no HLS, no DASH, no RTSP.
- No DRM of any kind.
- No caption RENDERING. It carries caption tracks, cues, settings and flags
  faithfully; drawing them is a presenter's job - and a short layer in
  CodeBrix.VideoPlayback.Skia.
- No rotation metadata for foreign files, no AV1 alpha, no nested or ordered
  chapters, no multiple chapter editions.
- No writing of WebM or Matroska. It reads them; it writes only .cbv.
- No encoding of any kind.


WORKING EXAMPLES ON GITHUB
==========================
https://github.com/ellisnet/CodeBrix.VideoPlayback

  tools/CodeBrix.VideoPlayback.Tools/
      cbvinfo, cbvdecode and cbvmux - three headless verbs, and the clearest
      worked examples of reading a container, driving a decoder by hand, and
      authoring a .cbv file.
  tests/CodeBrix.VideoPlayback.Tests/
      the whole surface exercised against a golden corpus, including an
      uncompressed decoder that shows what implementing IVideoDecoder takes.
  tests/assets/
      the corpus itself, with the script that regenerates it.
  CBV-FORMAT.txt
      the bespoke container's layout, byte by byte.


QUICK REFERENCE CARD
====================
I want to...                          Do this
------------------------------------  ------------------------------------------
play a file                           session.Open(path); session.Play();
play from a URL                       session.Open("https://...")
play a clip repeatedly                PreloadedClip.FromFile(...) once, then
                                      session.Open(clip) each time
loop it                               session.IsLooping = true
draw the picture                      session.Presenter.TryTakeLatest(out frame)
get BGRA pixels                       VideoFrameConverter.ToBgra32(frame, span, stride)
know when to repaint                  session.FrameReady
know where playback is                session.Position (the audio clock, when there is audio)
scrub                                 session.Seek(TimeSpan)
seek instantly, less precisely        options.SeekMode = VideoSeekMode.KeyFrameOnly
list the tracks                       session.Tracks
show captions                         session.SelectedCaptionTrack = ...; session.ActiveCues
list chapters                         session.Chapters; session.TitleFor(chapter, languages)
jump a chapter                        session.NextChapter() / SeekToChapter(i)
play AV1                              VideoDecoders.Register(<decoder package factory>)
play Opus                             CodeBrixAudioOpus.Register()
avoid rate conversion                 SharedAudioOutput.Configure(48000) at start-up
inspect a file without playing        new MatroskaReader(source) / new CbvReader(source)
author a .cbv                         CbvAuthoring.Write(request)
find out why it failed                catch VideoPlaybackException, or session.MediaFailed

Signatures worth memorising:

    void  VideoPlaybackSession.Open(string pathOrUrl)
    void  VideoPlaybackSession.Seek(TimeSpan position)
    bool  VideoFramePresenter.TryTakeLatest(out VideoFrame frame)
    VideoFrame VideoFrame.Retain()
    void  VideoFrameConverter.ToBgra32(VideoFrame frame, Span<byte> destination, int stride)
    void  VideoDecoders.Register(IVideoDecoderFactory factory)
    CbvAuthoringResult CbvAuthoring.Write(CbvAuthoringRequest request)
    bool  IMediaContainerReader.TryReadPacket(out MediaPacket packet)
    bool  IMediaContainerReader.IsTrackExhausted(int trackId)

Eight rules:

  1. Register the decoder packages your files need; failures name what is missing.
  2. Call SharedAudioOutput.Configure(48000) before the first sound.
  3. Take the frame on the drawing thread; the event only says "repaint".
  4. Dispose every frame exactly once; Retain() to share one.
  5. Copy a MediaPacket's bytes if you keep them past the next read.
  6. Check CanSeek before offering a scrubber.
  7. Watch the pool's Allocations and the presenter's Late count, not the frame rate.
  8. A "notice" is information, not a failure.
================================================================================
