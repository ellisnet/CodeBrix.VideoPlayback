using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback.Audio;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Internal;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.Presentation;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback;

/// <summary>
/// The player: it opens a media file, demultiplexes it, decodes the video into a
/// <see cref="VideoFramePresenter" />, plays the audio, keeps the two in step, and offers the transport an
/// application drives.
/// </summary>
/// <remarks>
/// <para>
/// One session plays one file at a time. Open it, hook up the events you care about, and call
/// <see cref="Play" />:
/// </para>
/// <code>
/// using CodeBrix.VideoPlayback;
///
/// VideoPlaybackSession session = new VideoPlaybackSession();
/// session.MediaOpened += (s, e) => Console.WriteLine($"{session.Duration} of video");
/// session.FrameReady += (s, e) => view.Invalidate();
/// session.Open("clip.cbv");
/// session.Play();
/// </code>
/// <para>
/// <b>Where the picture comes out.</b> Nothing is drawn here - this package has no drawing surface at all.
/// Decoded frames go into <see cref="Presenter" />, a one-slot mailbox holding the newest frame; a view
/// repaints when <see cref="FrameReady" /> fires and takes the frame with
/// <see cref="VideoFramePresenter.TryTakeLatest" /> on its own thread.
/// </para>
/// <para>
/// <b>What has to be registered.</b> A video decoder arrives as a separate package and registers itself with
/// <see cref="VideoDecoders" /> (or with this session through
/// <see cref="RegisterDecoderFactory" />). Vorbis audio needs nothing; Opus audio needs the Opus package
/// registered. A file whose codec has no decoder fails with a message that names the package to add.
/// </para>
/// <para>
/// <b>Threads.</b> Two of the session's own: one reading and demultiplexing, one decoding video. Audio is
/// pulled by the audio engine on its own thread. Events are raised from whichever of those threads noticed
/// the change, so a handler that touches a user interface must marshal.
/// </para>
/// </remarks>
public sealed class VideoPlaybackSession : IDisposable
{
    private static readonly CaptionCue[] NoCues = Array.Empty<CaptionCue>();

    private readonly VideoPlaybackOptions options;
    private readonly List<IVideoDecoderFactory> sessionFactories = new List<IVideoDecoderFactory>();
    private readonly PinnedFrameBufferPool bufferPool = new PinnedFrameBufferPool();
    private readonly VideoFramePresenter presenter = new VideoFramePresenter();
    private readonly object gate = new object();
    private readonly object readerGate = new object();
    private readonly object clockGate = new object();
    private readonly Stopwatch fallbackClock = new Stopwatch();
    private readonly object noticeGate = new object();
    private readonly List<string> sessionNotices = new List<string>();
    private readonly List<CaptionCue> cueScratch = new List<CaptionCue>();
    private readonly List<CaptionCue> forcedScratch = new List<CaptionCue>();

    private IMediaSource source;
    private bool leaveSourceOpen;
    private IMediaContainerReader reader;
    private MediaTrackInfo videoTrack;
    private MediaTrackInfo audioTrack;
    private IVideoDecoder decoder;
    private PacketRing videoQueue;
    private PacketRing audioQueue;
    private SessionAudioPacketSource audioSource;
    private TrackParkingBuffer videoParking;
    private TrackParkingBuffer audioParking;
    private PacketAudioPlayer audioPlayer;
    private IPacketSoundDecoder audioDecoder;
    private Thread demuxThread;
    private Thread decodeThread;
    private Thread clockThread;

    private TimeSpan clockBase;
    private TimeSpan audioClockCorrection;
    private int audioTrailingTrimFrames;
    private TimeSpan lastAudioDiscardPadding;
    private bool audioTrailingTrimArmed;
    private TimeSpan duration;
    private long frameNumber;
    private int seekGeneration;
    private long seekTargetTicks = -1;
    private volatile bool pendingImmediatePresent;
    private volatile bool skipToKeyFrame;
    private volatile bool demuxFinished;
    private volatile bool videoTrackExhausted;
    private volatile bool audioTrackExhausted;
    private volatile bool videoSupplyFinished;
    private volatile bool parkingBudgetReported;
    private volatile bool videoDrained;
    private volatile bool audioEnded;
    private volatile bool stopping;
    private volatile bool disposed;
    private int stateValue = (int)VideoPlaybackState.Idle;
    private CaptionTrack selectedCaptionTrack;
    private CaptionCue[] activeCues = NoCues;
    private Chapter currentChapter;
    private float volume = 1.0f;
    private bool muted;
    private int seekPendingAudioRebase;
    private long lastFrameEndTicks;

    /// <summary>Creates a session with the default settings.</summary>
    public VideoPlaybackSession()
        : this(null)
    {
    }

    /// <summary>Creates a session with settings of your own.</summary>
    /// <param name="options">The settings to use, or null for the defaults. A copy is taken.</param>
    public VideoPlaybackSession(VideoPlaybackOptions options)
    {
        this.options = options == null ? new VideoPlaybackOptions() : options.Clone();
        if (this.options.DecoderOptions == null) this.options.DecoderOptions = new VideoDecoderOptions();
        this.options.DecoderOptions.BufferPool = bufferPool;
    }

    /// <summary>Raised once the container has been read and the tracks and decoders are ready.</summary>
    public event EventHandler MediaOpened;

    /// <summary>Raised while playing, at <see cref="VideoPlaybackOptions.PositionUpdateInterval" />.</summary>
    public event EventHandler<VideoPositionChangedEventArgs> PositionChanged;

    /// <summary>Raised when playback reaches the end of the media and is not looping.</summary>
    public event EventHandler PlaybackEnded;

    /// <summary>Raised when something fails - while opening, or later on a background thread.</summary>
    public event EventHandler<MediaFailedEventArgs> MediaFailed;

    /// <summary>Raised when a new frame has reached <see cref="Presenter" /> and the display should repaint.</summary>
    public event EventHandler<VideoFrameReadyEventArgs> FrameReady;

    /// <summary>Raised when the set of captions that should be on screen has changed.</summary>
    public event EventHandler CaptionCuesChanged;

    /// <summary>Raised when playback crosses into a different chapter.</summary>
    public event EventHandler<ChapterChangedEventArgs> ChapterChanged;

    /// <summary>The settings this session was built with.</summary>
    public VideoPlaybackOptions Options => options;

    /// <summary>What the session is doing.</summary>
    public VideoPlaybackState State => (VideoPlaybackState)Volatile.Read(ref stateValue);

    /// <summary>The mailbox decoded frames arrive in. A view takes the newest frame from it and draws that.</summary>
    public VideoFramePresenter Presenter => presenter;

    /// <summary>
    /// The pool every decoder in this session writes its frames into. Exposed so a presenter can read its
    /// statistics and, on a graphics path, park a fence in a buffer's tag.
    /// </summary>
    public IVideoFrameBufferPool BufferPool => bufferPool;

    /// <summary>What the video decoder knows about the stream, or an empty description when there is no video.</summary>
    public VideoStreamInfo VideoStreamInfo => decoder == null ? VideoStreamInfo.Unknown : decoder.Info;

    /// <summary>Every track the container declares, video, audio and captions together.</summary>
    public IReadOnlyList<MediaTrackInfo> Tracks =>
        reader == null ? Array.Empty<MediaTrackInfo>() : reader.Tracks;

    /// <summary>The video track being played, or null when the file has none.</summary>
    public MediaTrackInfo VideoTrack => videoTrack;

    /// <summary>The audio track being played, or null when the file has none or audio is switched off.</summary>
    public MediaTrackInfo AudioTrack => audioTrack;

    /// <summary>
    /// How much of the end of the audio track the audio player has been told to discard, for tests and
    /// diagnostics. Zero when there is no audio player or the container states no trim.
    /// </summary>
    internal TimeSpan AudioTrailingTrim
    {
        get
        {
            PacketAudioPlayer player = audioPlayer;
            return player == null ? TimeSpan.Zero : player.TrailingTrim;
        }
    }

    /// <summary>
    /// How much audio the packet player has actually handed to the device, for tests and diagnostics.
    /// </summary>
    /// <remarks>
    /// This is the audio player's own clock, WITHOUT the pre-skip correction the session applies to
    /// <see cref="Position" />, and it stops advancing once everything has been delivered - so read after the
    /// sound has finished it is exactly how much audio was heard. Trimmed frames are never handed over, so
    /// they never count towards it, which is what makes a trim measurable from outside.
    /// </remarks>
    internal TimeSpan AudioPlayerPosition
    {
        get
        {
            PacketAudioPlayer player = audioPlayer;
            return player == null ? TimeSpan.Zero : player.Position;
        }
    }

    /// <summary>
    /// Things the container reader stepped over and thought worth mentioning - a subtitle format it cannot
    /// read, an element it does not model.
    /// </summary>
    /// <remarks>
    /// The session adds a few of its own where it has had to work around a file rather than refuse it - see
    /// <see cref="VideoPlaybackOptions.MaxTrackParkingBytes" />.
    /// </remarks>
    public IReadOnlyList<string> Notices
    {
        get
        {
            IReadOnlyList<string> fromReader = reader == null ? Array.Empty<string>() : reader.Notices;

            lock (noticeGate)
            {
                if (sessionNotices.Count == 0) return fromReader;

                List<string> combined = new List<string>(fromReader.Count + sessionNotices.Count);
                combined.AddRange(fromReader);
                combined.AddRange(sessionNotices);
                return combined;
            }
        }
    }

    /// <summary>How long the media lasts, or <see cref="TimeSpan.Zero" /> when the container does not say.</summary>
    public TimeSpan Duration => duration;

    /// <summary>Where playback has reached.</summary>
    /// <remarks>
    /// When there is an audio track this is the AUDIO clock - the position of the audio actually handed to
    /// the device - because that is the one a viewer hears and everything else is synchronised to it. With no
    /// audio it is a monotonic clock that runs while playing and stops while paused.
    /// </remarks>
    public TimeSpan Position => GetClock();

    /// <summary>True while the clock is running.</summary>
    public bool IsPlaying => State == VideoPlaybackState.Playing;

    /// <summary>True when the media is open and can be played.</summary>
    public bool IsOpen => reader != null;

    /// <summary>True to start again from the beginning when the end is reached.</summary>
    public bool IsLooping { get; set; }

    /// <summary>The playback volume, from 0 to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside 0 to 1 was assigned.</exception>
    public float Volume
    {
        get => volume;
        set
        {
            if (value is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The volume runs from 0 to 1.");
            }

            volume = value;
            ApplyVolume();
        }
    }

    /// <summary>True to silence the audio without losing the volume setting.</summary>
    public bool IsMuted
    {
        get => muted;
        set
        {
            muted = value;
            ApplyVolume();
        }
    }

    /// <summary>The file's text caption tracks.</summary>
    public IReadOnlyList<CaptionTrack> CaptionTracks =>
        reader == null ? Array.Empty<CaptionTrack>() : reader.CaptionTracks;

    /// <summary>
    /// The caption track to show, or null for none. Setting it recomputes
    /// <see cref="ActiveCues" /> immediately.
    /// </summary>
    public CaptionTrack SelectedCaptionTrack
    {
        get => selectedCaptionTrack;
        set
        {
            selectedCaptionTrack = value;
            UpdateCaptions(GetClock(), true);
        }
    }

    /// <summary>
    /// True to surface a forced caption track's cues even when no track is selected - the signs and foreign
    /// dialogue a viewer is meant to read whatever they chose. Defaults to true.
    /// </summary>
    public bool ShowForcedCaptions { get; set; } = true;

    /// <summary>The captions that should be on screen right now. Never null; usually empty.</summary>
    public IReadOnlyList<CaptionCue> ActiveCues => Volatile.Read(ref activeCues);

    /// <summary>The file's chapters, in order.</summary>
    public IReadOnlyList<Chapter> Chapters =>
        reader == null ? Array.Empty<Chapter>() : reader.Chapters;

    /// <summary>The chapter playback is inside, or null when it is not inside one.</summary>
    public Chapter CurrentChapter => currentChapter;

    /// <summary>Adds a video decoder factory that only this session will use.</summary>
    /// <param name="factory">The factory to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <remarks>
    /// Session factories are tried before the process-wide ones in
    /// <see cref="VideoDecoders" />, highest priority first, so this is how a test or an application with two
    /// sessions overrides a decoder without disturbing anybody else.
    /// </remarks>
    public void RegisterDecoderFactory(IVideoDecoderFactory factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));

        lock (gate)
        {
            foreach (IVideoDecoderFactory registered in sessionFactories)
            {
                if (ReferenceEquals(registered, factory)) return;
            }

            sessionFactories.Add(factory);
        }
    }

    /// <summary>Opens a media file by path or address.</summary>
    /// <param name="pathOrUrl">
    /// A file-system path, a <c>file://</c> address, or an <c>http://</c> or <c>https://</c> address.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pathOrUrl" /> is null or blank.</exception>
    /// <exception cref="VideoPlaybackException">The media cannot be played, with a message saying why.</exception>
    public void Open(string pathOrUrl) => Open(pathOrUrl, FileSourceMode.Streaming);

    /// <summary>Opens a media file by path or address, choosing how a local file is read.</summary>
    /// <param name="pathOrUrl">A path or an address.</param>
    /// <param name="mode">How a local file should be read.</param>
    /// <exception cref="ArgumentException"><paramref name="pathOrUrl" /> is null or blank.</exception>
    /// <exception cref="VideoPlaybackException">The media cannot be played, with a message saying why.</exception>
    public void Open(string pathOrUrl, FileSourceMode mode) => Open(MediaSources.Open(pathOrUrl, mode), false);

    /// <summary>Opens media from a stream.</summary>
    /// <param name="stream">The stream to read. A seekable stream can be seeked; a forward-only one cannot.</param>
    /// <param name="name">A short description used in error messages, or null.</param>
    /// <param name="leaveOpen">True to leave the stream open when the session closes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The media cannot be played, with a message saying why.</exception>
    public void Open(Stream stream, string name = null, bool leaveOpen = false) =>
        Open(new StreamMediaSource(stream, name, leaveOpen), false);

    /// <summary>Opens media from a clip that was loaded into memory earlier.</summary>
    /// <param name="clip">The preloaded clip.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The media cannot be played, with a message saying why.</exception>
    public void Open(PreloadedClip clip)
    {
        if (clip == null) throw new ArgumentNullException(nameof(clip));
        Open(clip.OpenSource(), false);
    }

    /// <summary>Opens media from a source.</summary>
    /// <param name="mediaSource">Where the bytes come from.</param>
    /// <param name="leaveOpen">True to leave the source open when the session closes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mediaSource" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The media cannot be played, with a message saying why.</exception>
    public void Open(IMediaSource mediaSource, bool leaveOpen = false)
    {
        if (mediaSource == null) throw new ArgumentNullException(nameof(mediaSource));
        ThrowIfDisposed();

        Close();

        try
        {
            source = mediaSource;
            leaveSourceOpen = leaveOpen;
            Volatile.Write(ref stateValue, (int)VideoPlaybackState.Opening);

            reader = OpenContainer(mediaSource);
            duration = reader.Duration;
            SelectTracks();
            CreateVideoDecoder();
            CreateAudioPlayer();
            ChooseDefaultCaptionTrack();

            videoQueue = new PacketRing(options.VideoQueueCapacity);
            audioQueue = new PacketRing(options.AudioQueueCapacity);
            videoParking = new TrackParkingBuffer(options.MaxTrackParkingBytes);
            audioParking = new TrackParkingBuffer(options.MaxTrackParkingBytes);

            if (audioPlayer != null)
            {
                audioSource = new SessionAudioPacketSource(audioQueue);
                audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
                audioPlayer.Open(audioDecoder, audioSource);
                ApplyVolume();
            }

            clockBase = TimeSpan.Zero;
            fallbackClock.Reset();
            pendingImmediatePresent = true;
            Volatile.Write(ref seekTargetTicks, -1);
            Volatile.Write(ref stateValue, (int)VideoPlaybackState.Stopped);
            StartThreads();
        }
        catch (Exception ex)
        {
            Close();
            Volatile.Write(ref stateValue, (int)VideoPlaybackState.Failed);
            RaiseFailure(ex);
            throw;
        }

        MediaOpened?.Invoke(this, EventArgs.Empty);
        UpdateCaptions(TimeSpan.Zero, true);
        UpdateChapter(TimeSpan.Zero);
    }

    /// <summary>Starts, or resumes, playback.</summary>
    /// <exception cref="InvalidOperationException">No media is open.</exception>
    public void Play()
    {
        ThrowIfDisposed();
        if (reader == null) throw new InvalidOperationException("No media is open, so there is nothing to play.");

        if (State == VideoPlaybackState.Ended)
        {
            Seek(TimeSpan.Zero);
        }

        lock (clockGate)
        {
            if (IsUsingFallbackClock) fallbackClock.Start();
        }

        Volatile.Write(ref stateValue, (int)VideoPlaybackState.Playing);
        audioPlayer?.Play();
    }

    /// <summary>Stops the clock where it stands.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        if (reader == null || State != VideoPlaybackState.Playing) return;

        lock (clockGate)
        {
            if (IsUsingFallbackClock)
            {
                clockBase += fallbackClock.Elapsed;
                fallbackClock.Reset();
            }
        }

        Volatile.Write(ref stateValue, (int)VideoPlaybackState.Paused);
        audioPlayer?.Pause();
    }

    /// <summary>Stops playback and puts the position back to the beginning.</summary>
    public void Stop()
    {
        ThrowIfDisposed();
        if (reader == null) return;

        audioPlayer?.Pause();

        if (reader.CanSeek)
        {
            Seek(TimeSpan.Zero);
        }
        else
        {
            lock (clockGate)
            {
                clockBase += fallbackClock.Elapsed;
                fallbackClock.Reset();
            }
        }

        Volatile.Write(ref stateValue, (int)VideoPlaybackState.Stopped);
    }

    /// <summary>Moves playback to a different moment.</summary>
    /// <param name="position">Where to move to. Values outside the media are clamped into it.</param>
    /// <exception cref="InvalidOperationException">No media is open.</exception>
    /// <exception cref="NotSupportedException">The source or the container cannot seek.</exception>
    /// <remarks>
    /// With <see cref="VideoSeekMode.Exact" /> - the default - the reader positions itself at the key frame
    /// before the requested moment and the decoder works forward to it, so the frame that appears is the right
    /// one. With <see cref="VideoSeekMode.KeyFrameOnly" /> it lands on the key frame itself, which costs
    /// nothing but is only as precise as the key frames are spaced.
    /// </remarks>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        if (reader == null) throw new InvalidOperationException("No media is open, so there is nothing to seek.");

        if (!reader.CanSeek)
        {
            throw new NotSupportedException(
                $"'{source.Name}' cannot be seeked: {(source.CanSeek ? "the container carries no index" : "the source is forward-only")}.");
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;

        lock (readerGate)
        {
            if (reader == null) return;

            TimeSpan landed = reader.Seek(position, videoTrack == null ? -1 : videoTrack.Id);

            videoQueue?.Clear();
            audioQueue?.Clear();

            Volatile.Write(ref seekTargetTicks, options.SeekMode == VideoSeekMode.Exact ? position.Ticks : landed.Ticks);
            Interlocked.Exchange(ref seekPendingAudioRebase, audioPlayer == null ? 0 : 1);
            Interlocked.Increment(ref seekGeneration);

            demuxFinished = false;
            videoTrackExhausted = false;
            audioTrackExhausted = false;
            videoSupplyFinished = false;
            videoDrained = false;
            audioEnded = false;
            skipToKeyFrame = false;
            pendingImmediatePresent = true;
            Volatile.Write(ref lastFrameEndTicks, 0);

            lock (clockGate)
            {
                clockBase = options.SeekMode == VideoSeekMode.Exact ? position : landed;
                audioClockCorrection = TimeSpan.Zero;
                fallbackClock.Reset();
                if (State == VideoPlaybackState.Playing && IsUsingFallbackClock) fallbackClock.Start();
            }

            if (State == VideoPlaybackState.Ended) Volatile.Write(ref stateValue, (int)VideoPlaybackState.Paused);
        }

        UpdateCaptions(position, true);
        UpdateChapter(position);
    }

    /// <summary>Moves playback to the start of a chapter.</summary>
    /// <param name="index">The chapter's index in <see cref="Chapters" />.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no chapter with that index.</exception>
    public void SeekToChapter(int index)
    {
        IReadOnlyList<Chapter> chapters = Chapters;
        if (index < 0 || index >= chapters.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"This media has {chapters.Count} chapters.");
        }

        Seek(chapters[index].Start);
    }

    /// <summary>Moves playback to the start of the next chapter.</summary>
    /// <returns>False when there is no next chapter.</returns>
    public bool NextChapter()
    {
        IReadOnlyList<Chapter> chapters = Chapters;
        TimeSpan now = GetClock();

        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Start <= now) continue;
            Seek(chapters[i].Start);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Moves playback to the start of the previous chapter, or to the start of the current one when playback
    /// is more than three seconds into it - the behaviour a listener expects from a track-back button.
    /// </summary>
    /// <returns>False when there is no chapter to go back to.</returns>
    public bool PreviousChapter()
    {
        IReadOnlyList<Chapter> chapters = Chapters;
        if (chapters.Count == 0) return false;

        TimeSpan now = GetClock();
        TimeSpan threshold = TimeSpan.FromSeconds(3);

        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            Chapter chapter = chapters[i];
            if (chapter.Start > now) continue;

            if (now - chapter.Start > threshold || i == 0)
            {
                Seek(chapter.Start);
                return true;
            }

            Seek(chapters[i - 1].Start);
            return true;
        }

        return false;
    }

    /// <summary>Picks a chapter's title in the language a viewer would prefer.</summary>
    /// <param name="chapter">The chapter.</param>
    /// <param name="preferredLanguages">BCP 47 tags in order of preference, or null for no preference.</param>
    /// <returns>A title, or an empty string when the chapter has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chapter" /> is null.</exception>
    public string TitleFor(Chapter chapter, IReadOnlyList<string> preferredLanguages)
    {
        if (chapter == null) throw new ArgumentNullException(nameof(chapter));
        return chapter.TitleFor(preferredLanguages);
    }

    /// <summary>Stops playback, releases the decoders and closes the media.</summary>
    public void Close()
    {
        StopThreads();
        lock (gate) TearDown();
    }

    /// <summary>Closes the media and releases everything the session owns.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Close();
        presenter.Dispose();
        bufferPool.Dispose();
    }

    // The session keeps ownership of the source - it may have to close it itself, and it may have been told
    // to leave it open - so the reader is always built with leaveSourceOpen true.
    private IMediaContainerReader OpenContainer(IMediaSource mediaSource) =>
        MediaContainers.Open(mediaSource, true);

    private void SelectTracks()
    {
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Video && videoTrack == null) videoTrack = track;
            else if (track.Kind == MediaTrackKind.Audio && audioTrack == null && options.PlayAudio) audioTrack = track;
        }

        if (videoTrack == null && audioTrack == null)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares no video and no audio track this library can play. Its tracks are: "
                + (reader.Tracks.Count == 0 ? "none at all." : string.Join(", ", reader.Tracks.Select(t => t.ToString())) + "."));
        }
    }

    private void CreateVideoDecoder()
    {
        if (videoTrack == null) return;

        IReadOnlyList<IVideoDecoderFactory> factories = BuildFactoryList();
        decoder = VideoDecoders.TryCreateDecoder(
            factories,
            videoTrack.CodecId,
            DescribeCodecPrivate(videoTrack),
            options.DecoderOptions);

        if (decoder != null) return;

        throw new VideoPlaybackException(DescribeMissingVideoDecoder(videoTrack.CodecId));
    }

    /// <summary>
    /// Supplies a decoder with codec-private data even when the container carried none.
    /// </summary>
    /// <remarks>
    /// A Matroska <c>V_UNCOMPRESSED</c> track states its shape in ordinary track elements rather than in a
    /// codec-private block, so the descriptor an uncompressed decoder needs is built from those. Every other
    /// codec's private data is passed through exactly as the container stored it.
    /// </remarks>
    private static ReadOnlyMemory<byte> DescribeCodecPrivate(MediaTrackInfo track)
    {
        if (!track.CodecPrivate.IsEmpty) return track.CodecPrivate;
        if (!string.Equals(track.CodecId, VideoCodecIds.Raw, StringComparison.OrdinalIgnoreCase))
        {
            return track.CodecPrivate;
        }

        RawVideoDescriptor descriptor = new RawVideoDescriptor(
            track.Width,
            track.Height,
            track.BitDepth > 0 ? track.BitDepth : 8,
            track.Layout == VideoPixelLayout.Unknown ? VideoPixelLayout.I420 : track.Layout,
            track.Color);

        return descriptor.IsValid ? RawVideoFormat.CreateDescriptor(descriptor) : track.CodecPrivate;
    }

    private IReadOnlyList<IVideoDecoderFactory> BuildFactoryList()
    {
        List<IVideoDecoderFactory> factories = new List<IVideoDecoderFactory>();

        lock (gate)
        {
            factories.AddRange(sessionFactories.OrderByDescending(f => f.Priority));
        }

        factories.AddRange(VideoDecoders.Snapshot());
        return factories;
    }

    private static string DescribeMissingVideoDecoder(string codecId)
    {
        if (string.Equals(codecId, VideoCodecIds.Av1, StringComparison.OrdinalIgnoreCase))
        {
            return "video codec 'av01' has no registered decoder — reference CodeBrix.VideoPlayback.Dav1d and call "
                + "CodeBrixVideoPlaybackDav1d.Register()";
        }

        return $"video codec '{codecId}' has no registered decoder — register an IVideoDecoderFactory that serves "
            + "it with VideoDecoders.Register(...)";
    }

    private static string DescribeMissingAudioDecoder(string codecId)
    {
        if (string.Equals(codecId, VideoCodecIds.Opus, StringComparison.OrdinalIgnoreCase))
        {
            return "audio codec 'opus' has no registered decoder — reference CodeBrix.Audio.Opus and call "
                + "CodeBrixAudioOpus.Register()";
        }

        return $"audio codec '{codecId}' has no registered decoder — register an IPacketCodecFactory that serves "
            + "it with SharedAudioOutput.RegisterPacketCodecFactory(...)";
    }

    private void CreateAudioPlayer()
    {
        if (audioTrack == null) return;

        // ASK FIRST, AND ASK THE ONE THING THAT STARTS NOTHING. CreatePacketDecoder below opens the audio
        // device, because the audio package's codec registry lives on the running engine - so a file whose
        // codec has no decoder would otherwise open a device only to be refused a moment later. The probe
        // reads the same registry without starting anything, which is what lets the refusal below be
        // produced on a machine with no sound hardware at all.
        if (!AudioDecoders.IsCodecSupported(audioTrack.CodecId))
        {
            throw new VideoPlaybackException(DescribeMissingAudioDecoder(audioTrack.CodecId));
        }

        if (options.AudioSampleRate > 0)
        {
            try
            {
                SharedAudioOutput.Configure(options.AudioSampleRate);
            }
            catch (InvalidOperationException)
            {
                // The shared output is already running at a rate somebody else chose. Nothing to do: the
                // engine converts, and the AGENT-README says to configure it before the first sound plays.
            }
        }

        try
        {
            audioDecoder = SharedAudioOutput.CreatePacketDecoder(audioTrack.CodecId, audioTrack.CodecPrivate);
        }
        catch (NotSupportedException ex)
        {
            // The probe said the codec was served, so reaching here means the factory declined this
            // particular track. The message is the same one, because the answer is the same: the package
            // that can play it is not present.
            throw new VideoPlaybackException(DescribeMissingAudioDecoder(audioTrack.CodecId), ex);
        }

        if (audioDecoder == null) throw new VideoPlaybackException(DescribeMissingAudioDecoder(audioTrack.CodecId));

        audioPlayer = new PacketAudioPlayer();

        int preSkip = audioDecoder.PreSkipSamples;
        int rate = audioDecoder.SampleRate > 0 ? audioDecoder.SampleRate : audioTrack.SampleRate;
        audioClockCorrection = preSkip > 0 && rate > 0
            ? TimeSpan.FromTicks((long)preSkip * TimeSpan.TicksPerSecond / rate)
            : TimeSpan.Zero;

        audioTrailingTrimFrames = ResolveTrailingTrimFrames(audioTrack, audioDecoder.SampleRate);
        lastAudioDiscardPadding = TimeSpan.Zero;
        audioTrailingTrimArmed = false;
        ApplyContainerTrailingTrim();
    }

    /// <summary>
    /// Converts a track header's trailing trim into the frames-per-channel the audio player counts in.
    /// </summary>
    /// <param name="track">The audio track.</param>
    /// <param name="decoderSampleRate">The decoder's own output rate, or zero when it does not say.</param>
    /// <returns>Frames per channel to discard from the very end of the track, or zero.</returns>
    /// <remarks>
    /// The bespoke container states the trim in samples per channel at the TRACK's rate, and the audio
    /// player counts in frames per channel at the DECODER's rate. They are the same number for every codec
    /// this library reads, because a packet codec decodes at the rate its own header declares - but the two
    /// are different questions, so the conversion is done rather than assumed.
    /// </remarks>
    internal static int ResolveTrailingTrimFrames(MediaTrackInfo track, int decoderSampleRate)
    {
        if (track == null) return 0;

        int frames = track.TrailingTrimSamples;
        if (frames <= 0) return 0;

        int trackRate = track.SampleRate;
        if (decoderSampleRate <= 0 || trackRate <= 0 || decoderSampleRate == trackRate) return frames;

        return (int)((long)frames * decoderSampleRate / trackRate);
    }

    /// <summary>
    /// Gives the audio player the trim the CONTAINER states for the track, which is the exact instrument.
    /// </summary>
    /// <remarks>
    /// The bespoke container states it once, in the track header, so it is known before a single packet has
    /// been read and is applied here. Matroska states it per block instead, so there is nothing to apply
    /// yet - the padding travels on the packets themselves and the track-level value is armed later, by
    /// <see cref="ArmTrailingTrimFromLastAudioPacket" />, once the reader has proved which block was the
    /// last. Setting zero is the audio player's untouched path, not a cost.
    /// </remarks>
    private void ApplyContainerTrailingTrim()
    {
        PacketAudioPlayer player = audioPlayer;
        if (player == null) return;

        if (audioTrailingTrimFrames > 0) player.SetTrailingTrimFrames(audioTrailingTrimFrames);
        else player.SetTrailingTrim(TimeSpan.Zero);
    }

    /// <summary>
    /// Applies the discard padding the container put on the audio track's LAST block, once the reader has
    /// proved that block really was the last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The padding of every audio packet is passed to the audio player on the packet itself, which is enough
    /// whenever the padding is smaller than the packet it rides on - the player holds back the larger of the
    /// track-level trim and the most recent packet's padding, so the value on the last packet is the one
    /// still raised when the stream ends. That route is BEST-EFFORT, because a value is only learned when
    /// its packet arrives and can therefore only hold back what is still in hand plus what that packet
    /// decodes to.
    /// </para>
    /// <para>
    /// So the same value is also set as the track-level trim here, which is exact: it applies from this
    /// moment on and the demultiplexer is normally a queue's worth of packets ahead of the audio thread, so
    /// it is armed well before the tail is decoded. It is only ever raised, never lowered, so a container
    /// that stated an exact trim in its track header keeps it.
    /// </para>
    /// <para>Called on the demultiplexing thread only, which is also the only thread that records the value.</para>
    /// </remarks>
    private void ArmTrailingTrimFromLastAudioPacket()
    {
        if (audioTrailingTrimArmed) return;
        audioTrailingTrimArmed = true;

        PacketAudioPlayer player = audioPlayer;
        if (player == null || lastAudioDiscardPadding <= TimeSpan.Zero) return;
        if (lastAudioDiscardPadding <= player.TrailingTrim) return;

        player.SetTrailingTrim(lastAudioDiscardPadding);
    }

    private void ChooseDefaultCaptionTrack()
    {
        foreach (CaptionTrack track in reader.CaptionTracks)
        {
            if (!track.IsDefault || track.IsForced) continue;
            selectedCaptionTrack = track;
            return;
        }
    }

    private void StartThreads()
    {
        stopping = false;

        demuxThread = new Thread(DemuxLoop)
        {
            IsBackground = true,
            Name = "CodeBrix video demux",
        };

        decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "CodeBrix video decode",
        };

        clockThread = new Thread(ClockLoop)
        {
            IsBackground = true,
            Name = "CodeBrix video clock",
        };

        demuxThread.Start();
        decodeThread.Start();
        clockThread.Start();
    }

    private void DemuxLoop()
    {
        int generation = Volatile.Read(ref seekGeneration);

        while (!stopping)
        {
            try
            {
                int current = Volatile.Read(ref seekGeneration);
                if (current != generation)
                {
                    generation = current;
                    demuxFinished = false;
                    videoTrackExhausted = false;
                    audioTrackExhausted = false;
                    videoSupplyFinished = false;
                    videoParking?.Clear();
                    audioParking?.Clear();

                    // The end of the track moved, so a padding read off the block that used to be the last
                    // one no longer says anything. The container's own trim - which belongs to the track
                    // rather than to a position in it - is put back.
                    lastAudioDiscardPadding = TimeSpan.Zero;
                    audioTrailingTrimArmed = false;
                    ApplyContainerTrailingTrim();
                }

                // Anything parked goes out FIRST, so a track's packets reach its queue in exactly the order
                // the file stored them.
                bool videoParkingEmpty = videoParking == null || videoParking.TryDrainInto(videoQueue);
                bool audioParkingEmpty = audioParking == null || audioParking.TryDrainInto(audioQueue);

                PublishTrackExhaustion(videoParkingEmpty, audioParkingEmpty);

                if (demuxFinished)
                {
                    Thread.Sleep(4);
                    continue;
                }

                // The demultiplexer waits only when a track could take NOTHING - neither in its queue nor in
                // its parking budget. A full queue on its own no longer stops it, and one track's full queue
                // never stops another track from being fed, which is what used to let a clip whose sound ends
                // before its picture stop the whole session.
                if (IsTrackBlocked(videoQueue, videoParking) || IsTrackBlocked(audioQueue, audioParking))
                {
                    ReportParkingBudgetOnce();
                    Thread.Sleep(2);
                    continue;
                }

                MediaPacket packet;
                bool more;

                lock (readerGate)
                {
                    if (reader == null) return;
                    more = reader.TryReadPacket(out packet);

                    if (!more) demuxFinished = true;

                    // Ask the reader what it can prove after every read. The bespoke container answers
                    // exactly and early from its index; Matroska answers only once every cluster has been
                    // read, which is why reaching the end of the file above must always be possible.
                    if (videoTrack != null) videoTrackExhausted = reader.IsTrackExhausted(videoTrack.Id);
                    if (audioTrack != null) audioTrackExhausted = reader.IsTrackExhausted(audioTrack.Id);
                }

                if (!more) continue;

                if (videoTrack != null && packet.TrackId == videoTrack.Id)
                {
                    Deliver(videoQueue, videoParking, packet, TimeSpan.Zero, generation);
                    continue;
                }

                if (audioTrack != null && packet.TrackId == audioTrack.Id && audioQueue != null)
                {
                    if (Interlocked.Exchange(ref seekPendingAudioRebase, 0) == 1 && audioPlayer != null)
                    {
                        TimeSpan target = TimeSpan.FromTicks(Math.Max(0, Volatile.Read(ref seekTargetTicks)));
                        TimeSpan preRoll = target > packet.Timestamp ? target - packet.Timestamp : TimeSpan.Zero;
                        audioSource.SetEndOfStream(false);
                        audioPlayer.Seek(packet.Timestamp, preRoll);
                    }

                    // Remember what THIS packet said, padding or none, so that the value in hand when the
                    // track is declared finished is the one on its last block and not on some earlier one.
                    lastAudioDiscardPadding = packet.DiscardPadding;

                    Deliver(audioQueue, audioParking, packet, packet.DiscardPadding, generation);
                }
            }
            catch (Exception ex)
            {
                if (stopping) return;
                RaiseFailure(ex);
                Volatile.Write(ref stateValue, (int)VideoPlaybackState.Failed);
                return;
            }
        }
    }

    /// <summary>
    /// Hands a packet to its track's queue, or parks it behind whatever that track is already waiting to
    /// deliver.
    /// </summary>
    /// <remarks>
    /// It never blocks. The demultiplexer has already established that this track can take something, and a
    /// track with anything parked keeps its order by having the new packet go behind what is waiting rather
    /// than jumping the queue.
    /// </remarks>
    private static void Deliver(
        PacketRing ring,
        TrackParkingBuffer parking,
        in MediaPacket packet,
        TimeSpan discardPadding,
        int generation)
    {
        if (ring == null) return;

        if ((parking == null || parking.IsEmpty)
            && ring.TryEnqueue(
                packet.Data.Span,
                packet.Timestamp,
                packet.Duration,
                packet.IsKeyFrame,
                discardPadding,
                generation))
        {
            return;
        }

        parking?.Park(
            packet.Data.Span,
            packet.Timestamp,
            packet.Duration,
            packet.IsKeyFrame,
            discardPadding,
            generation);
    }

    /// <summary>True when a track could not accept another packet by any route.</summary>
    private static bool IsTrackBlocked(PacketRing ring, TrackParkingBuffer parking)
    {
        if (ring == null) return false;
        if (!ring.IsFull) return false;
        return parking == null || parking.IsAtBudget;
    }

    /// <summary>
    /// Tells the audio device and the decoding thread that their track has ended, once everything the reader
    /// had for it has actually been handed over.
    /// </summary>
    /// <remarks>
    /// The reader knowing a track is finished is not the same as the track being finished HERE: packets may
    /// still be parked, waiting for room. Publishing early would cut the tail off. So the signal waits for
    /// the parking to empty, which is also why it is re-evaluated on every pass of the demultiplexing loop
    /// rather than once at the end of the file.
    /// </remarks>
    private void PublishTrackExhaustion(bool videoParkingEmpty, bool audioParkingEmpty)
    {
        if (audioTrack != null && audioParkingEmpty && (audioTrackExhausted || demuxFinished))
        {
            // The last block for this track has now been handed over, so its discard padding - if the
            // container put one there - is the track's trailing trim. Arm it BEFORE the end of stream is
            // published, because the player discards what it is holding the moment it hears the stream has
            // ended.
            ArmTrailingTrimFromLastAudioPacket();
            audioSource?.SetEndOfStream(true);
        }

        if (videoTrack != null && videoParkingEmpty && (videoTrackExhausted || demuxFinished))
        {
            videoSupplyFinished = true;
        }
    }

    /// <summary>Records something the session did rather than refused to do, for the caller to read later.</summary>
    private void AddNotice(string notice)
    {
        lock (noticeGate) sessionNotices.Add(notice);
    }

    private void ReportParkingBudgetOnce()
    {
        if (parkingBudgetReported) return;
        parkingBudgetReported = true;

        long videoBytes = videoParking == null ? 0 : videoParking.Bytes;
        long audioBytes = audioParking == null ? 0 : audioParking.Bytes;

        AddNotice(
            $"This file interleaves its tracks so unevenly that one of them filled its {options.MaxTrackParkingBytes:N0}-byte "
            + $"parking budget (video {videoBytes:N0} bytes, audio {audioBytes:N0} bytes waiting). The "
            + "demultiplexer has to wait for room, which delays learning that another track has ended. Raise "
            + "VideoPlaybackOptions.MaxTrackParkingBytes if this file matters.");
    }

    private void DecodeLoop()
    {
        if (videoTrack == null || decoder == null)
        {
            videoDrained = true;
            return;
        }

        int generation = Volatile.Read(ref seekGeneration);
        VideoFrame held = null;
        int consecutiveLate = 0;
        bool drained = false;

        try
        {
            while (!stopping)
            {
                int current = Volatile.Read(ref seekGeneration);
                if (current != generation)
                {
                    generation = current;
                    held?.Dispose();
                    held = null;
                    consecutiveLate = 0;
                    drained = false;
                    videoDrained = false;
                    decoder.Flush();
                    presenter.Clear();
                }

                if (held != null)
                {
                    if (!TryPresent(held, generation))
                    {
                        Thread.Sleep(State == VideoPlaybackState.Playing ? 1 : options.DecodeAheadWhilePaused ? 5 : 25);
                        continue;
                    }

                    held = null;
                    continue;
                }

                if (decoder.TryReceiveFrame(out VideoFrame frame))
                {
                    held = ScreenFrame(frame, generation, ref consecutiveLate);
                    continue;
                }

                if (!videoQueue.TryBeginRead(out RingPacket queued))
                {
                    // The VIDEO track's own end, which for an indexed container arrives long before the end
                    // of the file - so a picture that finishes early stops waiting for the sound to be read.
                    if (videoSupplyFinished)
                    {
                        if (!drained)
                        {
                            drained = true;
                            decoder.Drain();
                            continue;
                        }

                        videoDrained = true;
                    }

                    Thread.Sleep(2);
                    continue;
                }

                if (queued.Generation != generation)
                {
                    videoQueue.EndRead();
                    continue;
                }

                if (skipToKeyFrame && !queued.IsKeyFrame)
                {
                    videoQueue.EndRead();
                    continue;
                }

                if (skipToKeyFrame)
                {
                    skipToKeyFrame = false;
                    consecutiveLate = 0;
                    decoder.Flush();
                }

                decoder.SendPacket(new VideoPacket(
                    queued.Data,
                    queued.Timestamp,
                    queued.IsKeyFrame,
                    queued.Duration,
                    frameNumber++));

                videoQueue.EndRead();
            }
        }
        catch (Exception ex)
        {
            if (!stopping)
            {
                RaiseFailure(ex);
                Volatile.Write(ref stateValue, (int)VideoPlaybackState.Failed);
            }
        }
        finally
        {
            held?.Dispose();
        }
    }

    private VideoFrame ScreenFrame(VideoFrame frame, int generation, ref int consecutiveLate)
    {
        long target = Volatile.Read(ref seekTargetTicks);
        if (target >= 0)
        {
            if (frame.Timestamp.Ticks < target)
            {
                frame.Dispose();
                return null;
            }

            Interlocked.Exchange(ref seekTargetTicks, -1);
        }

        if (pendingImmediatePresent) return frame;

        TimeSpan clock = GetClock();
        if (State == VideoPlaybackState.Playing && frame.Timestamp + options.LateFrameTolerance < clock)
        {
            consecutiveLate++;
            presenter.NotifyLateFrameDropped();

            if (consecutiveLate >= options.ConsecutiveLateFramesBeforeSkip) skipToKeyFrame = true;

            frame.Dispose();
            return null;
        }

        consecutiveLate = 0;
        return frame;
    }

    private bool TryPresent(VideoFrame frame, int generation)
    {
        if (Volatile.Read(ref seekGeneration) != generation) return true;

        if (!pendingImmediatePresent)
        {
            if (State != VideoPlaybackState.Playing) return false;

            TimeSpan clock = GetClock();
            if (frame.Timestamp > clock + TimeSpan.FromMilliseconds(2)) return false;
        }

        pendingImmediatePresent = false;
        Volatile.Write(ref lastFrameEndTicks, frame.Timestamp.Ticks);

        presenter.Post(frame);
        FrameReady?.Invoke(this, new VideoFrameReadyEventArgs(frame.Timestamp, frame.FrameNumber));
        frame.Dispose();
        return true;
    }

    private void ClockLoop()
    {
        TimeSpan interval = options.PositionUpdateInterval;
        TimeSpan lastReported = TimeSpan.MinValue;

        while (!stopping)
        {
            try
            {
                Thread.Sleep(interval);
                if (stopping || reader == null) continue;

                TimeSpan now = GetClock();

                if (now != lastReported)
                {
                    lastReported = now;
                    PositionChanged?.Invoke(this, new VideoPositionChangedEventArgs(now, duration));
                }

                UpdateCaptions(now, false);
                UpdateChapter(now);

                if (!HasReachedEnd(now)) continue;

                if (IsLooping)
                {
                    Seek(TimeSpan.Zero);
                    if (State != VideoPlaybackState.Playing) Play();
                    continue;
                }

                if (State == VideoPlaybackState.Ended) continue;

                lock (clockGate)
                {
                    if (IsUsingFallbackClock)
                    {
                        clockBase += fallbackClock.Elapsed;
                        fallbackClock.Reset();
                    }
                }

                audioPlayer?.Pause();
                Volatile.Write(ref stateValue, (int)VideoPlaybackState.Ended);
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (stopping) return;
                RaiseFailure(ex);
            }
        }
    }

    /// <summary>
    /// True once every selected track has finished - the LATER of the two ends, so a picture that outlasts
    /// its sound plays to its own end and a sound that outlasts its picture is heard to its own.
    /// </summary>
    /// <remarks>
    /// Each track is asked about separately, because they do not end together and a container is not obliged
    /// to make them. The video side is finished when the reader has no more video, the queue is empty and the
    /// decoder has been drained; the audio side is finished when the packet player says so, which happens
    /// once the audio track has been declared exhausted and everything queued has been heard.
    /// </remarks>
    private bool HasReachedEnd(TimeSpan now)
    {
        if (State == VideoPlaybackState.Opening) return false;

        if (videoTrack != null)
        {
            if (!videoDrained) return false;
            if (videoQueue != null && videoQueue.Count > 0) return false;
            if (videoParking != null && !videoParking.IsEmpty) return false;
        }
        else if (!demuxFinished)
        {
            return false;
        }

        if (audioPlayer != null && !audioEnded)
        {
            // The container's stated Duration is the OUTER BOUND, and it covers the picture as well as the
            // sound. The encoder's tail padding is no longer cut here - the audio player trims it out of the
            // audio itself, exactly, from the container's stated trim - so this is a backstop for a file
            // whose sound simply runs past what it declared, not the trimming mechanism.
            if (duration > TimeSpan.Zero && now >= duration) return true;
            return false;
        }

        if (duration > TimeSpan.Zero && now + TimeSpan.FromMilliseconds(60) < duration) return false;
        return true;
    }

    /// <summary>
    /// True when the clock is the session's own stopwatch rather than the audio player's position - because
    /// there is no audio, or because the audio has run out before the picture has.
    /// </summary>
    private bool IsUsingFallbackClock => audioPlayer == null || audioEnded;

    private TimeSpan GetClock()
    {
        PacketAudioPlayer player = audioPlayer;
        if (player != null && !audioEnded)
        {
            TimeSpan corrected = player.Position - audioClockCorrection;
            return corrected < TimeSpan.Zero ? TimeSpan.Zero : corrected;
        }

        lock (clockGate)
        {
            return clockBase + fallbackClock.Elapsed;
        }
    }

    private void ApplyVolume()
    {
        PacketAudioPlayer player = audioPlayer;
        if (player == null) return;
        player.Volume = muted ? 0f : volume;
    }

    /// <summary>
    /// Moves the clock off the audio player when the sound runs out, so that a file whose audio is shorter
    /// than its video keeps playing to the end of the picture instead of stopping where the sound did.
    /// </summary>
    /// <remarks>
    /// The audio player's Position stops advancing once its queue has drained, so leaving the clock on it
    /// would freeze the position short of the declared duration - and the session would then wait for an end
    /// that never arrives. The stopwatch takes over from exactly where the audio left off.
    /// </remarks>
    private void OnAudioPlaybackEnded(object sender, EventArgs e)
    {
        PacketAudioPlayer player = audioPlayer;

        lock (clockGate)
        {
            if (!audioEnded)
            {
                TimeSpan resume = player != null ? player.Position - audioClockCorrection : clockBase;
                clockBase = resume < TimeSpan.Zero ? TimeSpan.Zero : resume;
                fallbackClock.Reset();
                if (State == VideoPlaybackState.Playing) fallbackClock.Start();
            }

            audioEnded = true;
        }
    }

    private void UpdateCaptions(TimeSpan position, bool force)
    {
        CaptionTrack track = selectedCaptionTrack;
        List<CaptionCue> results = cueScratch;

        lock (results)
        {
            results.Clear();

            if (track != null)
            {
                track.GetActiveCues(position, results);
            }

            if (ShowForcedCaptions && reader != null)
            {
                foreach (CaptionTrack forced in reader.CaptionTracks)
                {
                    if (!forced.IsForced || ReferenceEquals(forced, track)) continue;
                    if (audioTrack != null
                        && audioTrack.Language.Length > 0
                        && forced.Language.Length > 0
                        && !LanguageTags.SameLanguage(audioTrack.Language, forced.Language))
                    {
                        continue;
                    }

                    forced.GetActiveCues(position, forcedScratch);
                    results.AddRange(forcedScratch);
                }
            }

            CaptionCue[] previous = Volatile.Read(ref activeCues);
            if (!force && SameCues(previous, results)) return;

            Volatile.Write(ref activeCues, results.Count == 0 ? NoCues : results.ToArray());
        }

        CaptionCuesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameCues(CaptionCue[] previous, List<CaptionCue> current)
    {
        if (previous.Length != current.Count) return false;

        for (int i = 0; i < previous.Length; i++)
        {
            if (!ReferenceEquals(previous[i], current[i])) return false;
        }

        return true;
    }

    private void UpdateChapter(TimeSpan position)
    {
        IReadOnlyList<Chapter> chapters = Chapters;
        Chapter found = null;

        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (chapters[i].Start > position) continue;
            found = chapters[i];
            break;
        }

        Chapter previous = currentChapter;
        if (ReferenceEquals(previous, found)) return;

        currentChapter = found;
        ChapterChanged?.Invoke(this, new ChapterChangedEventArgs(found, previous));
    }

    private void RaiseFailure(Exception ex) =>
        MediaFailed?.Invoke(this, new MediaFailedEventArgs(ex));

    private void StopThreads()
    {
        stopping = true;

        JoinThread(demuxThread);
        JoinThread(decodeThread);
        JoinThread(clockThread);

        demuxThread = null;
        decodeThread = null;
        clockThread = null;
    }

    private void TearDown()
    {
        if (audioPlayer != null)
        {
            audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
            audioPlayer.Stop();
            audioPlayer.Dispose();
            audioPlayer = null;
        }

        audioSource?.ReleaseHeldPacket();
        audioSource = null;
        audioDecoder = null;

        decoder?.Dispose();
        decoder = null;

        presenter.Clear();
        presenter.ResetStatistics();

        lock (readerGate)
        {
            reader?.Dispose();
            reader = null;
        }

        if (source != null && !leaveSourceOpen) source.Dispose();
        source = null;

        videoQueue?.Clear();
        audioQueue?.Clear();
        videoQueue = null;
        audioQueue = null;

        videoParking?.Clear();
        audioParking?.Clear();
        videoParking = null;
        audioParking = null;

        lock (noticeGate) sessionNotices.Clear();
        parkingBudgetReported = false;

        videoTrack = null;
        audioTrack = null;
        selectedCaptionTrack = null;
        currentChapter = null;
        Volatile.Write(ref activeCues, NoCues);

        duration = TimeSpan.Zero;
        clockBase = TimeSpan.Zero;
        audioClockCorrection = TimeSpan.Zero;
        audioTrailingTrimFrames = 0;
        lastAudioDiscardPadding = TimeSpan.Zero;
        audioTrailingTrimArmed = false;
        frameNumber = 0;
        demuxFinished = false;
        videoDrained = false;
        audioEnded = false;
        skipToKeyFrame = false;
        Volatile.Write(ref seekTargetTicks, -1);
        fallbackClock.Reset();

        if (State != VideoPlaybackState.Failed) Volatile.Write(ref stateValue, (int)VideoPlaybackState.Idle);
    }

    private static void JoinThread(Thread thread)
    {
        if (thread == null || !thread.IsAlive) return;
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(VideoPlaybackSession));
    }
}
