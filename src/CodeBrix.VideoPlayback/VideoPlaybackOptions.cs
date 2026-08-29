using System;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Playback;

namespace CodeBrix.VideoPlayback;

/// <summary>
/// The settings a <see cref="VideoPlaybackSession" /> is built with: how far ahead it reads, how precisely it
/// seeks, how often it reports its position, and what the video decoder is told.
/// </summary>
/// <remarks>
/// Every setting has a working default. Change them before <see cref="VideoPlaybackSession.Open(string)" />;
/// once media is open the session has already sized its queues.
/// </remarks>
public sealed class VideoPlaybackOptions
{
    private int videoQueueCapacity = 32;
    private int audioQueueCapacity = 128;
    private long maxTrackParkingBytes = 32L * 1024 * 1024;
    private TimeSpan positionUpdateInterval = TimeSpan.FromMilliseconds(100);
    private TimeSpan lateFrameTolerance = TimeSpan.FromMilliseconds(40);
    private int consecutiveLateFramesBeforeSkip = 4;

    /// <summary>How the session seeks. Defaults to <see cref="VideoSeekMode.Exact" />.</summary>
    public VideoSeekMode SeekMode { get; set; } = VideoSeekMode.Exact;

    /// <summary>
    /// How many video packets may wait between the demultiplexing thread and the decoding thread. Defaults
    /// to 32.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value below 2 was assigned.</exception>
    public int VideoQueueCapacity
    {
        get => videoQueueCapacity;
        set
        {
            if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), value, "The queue must hold at least two packets.");
            videoQueueCapacity = value;
        }
    }

    /// <summary>
    /// How many audio packets may wait between the demultiplexing thread and the audio device. Defaults to
    /// 128, which is a couple of seconds of ordinary packets.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value below 2 was assigned.</exception>
    public int AudioQueueCapacity
    {
        get => audioQueueCapacity;
        set
        {
            if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), value, "The queue must hold at least two packets.");
            audioQueueCapacity = value;
        }
    }

    /// <summary>
    /// How many bytes of one track's packets may wait for room in its queue before the demultiplexer stops
    /// reading. Defaults to 32 MB per track.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A negative value was assigned.</exception>
    /// <remarks>
    /// <para>
    /// A file is not obliged to interleave its tracks evenly, and one that does not would otherwise stop the
    /// demultiplexer dead: a clip whose sound finishes before its picture ends with a stretch of video
    /// packets and no audio packets at all, and if the video queue fills there, a demultiplexer that blocks
    /// never reaches the end of the file - so nothing ever learns that the audio track finished, and the
    /// session waits for an end that cannot arrive.
    /// </para>
    /// <para>
    /// Packets a queue has no room for are therefore held aside, in order, until it does, and this is how
    /// much of that holding is allowed per track. The demultiplexer stops only when a track's queue is full
    /// AND its parking is at this budget, which bounds the memory rather than the wait. Thirty-two megabytes
    /// covers about two minutes of 1080p video at a few megabits a second, which is far more skew than any
    /// ordinary file has.
    /// </para>
    /// <para>Nothing is parked at all while a file interleaves its tracks normally, so this costs nothing.</para>
    /// </remarks>
    public long MaxTrackParkingBytes
    {
        get => maxTrackParkingBytes;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The parking budget cannot be negative.");
            }

            maxTrackParkingBytes = value;
        }
    }

    /// <summary>
    /// How often <see cref="VideoPlaybackSession.PositionChanged" /> is raised while playing. Defaults to 100
    /// milliseconds - fast enough for a scrubber, slow enough not to matter.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value of zero or less was assigned.</exception>
    public TimeSpan PositionUpdateInterval
    {
        get => positionUpdateInterval;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The interval must be greater than zero.");
            }

            positionUpdateInterval = value;
        }
    }

    /// <summary>
    /// How far past its moment a frame may be and still be shown. A frame later than this is dropped rather
    /// than displayed at the wrong time. Defaults to 40 milliseconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A negative value was assigned.</exception>
    public TimeSpan LateFrameTolerance
    {
        get => lateFrameTolerance;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The tolerance cannot be negative.");
            }

            lateFrameTolerance = value;
        }
    }

    /// <summary>
    /// How many frames in a row may be dropped for lateness before the session stops trying to catch up frame
    /// by frame and skips to the next key frame instead. Defaults to 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value below 1 was assigned.</exception>
    public int ConsecutiveLateFramesBeforeSkip
    {
        get => consecutiveLateFramesBeforeSkip;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), value, "At least one frame must be allowed.");
            consecutiveLateFramesBeforeSkip = value;
        }
    }

    /// <summary>
    /// True to play the audio track when there is one. Set it false for a session that only wants frames -
    /// a thumbnail extractor, a headless verification run. Defaults to true.
    /// </summary>
    public bool PlayAudio { get; set; } = true;

    /// <summary>
    /// The sample rate the shared audio output is configured with before the first sound is played. Defaults
    /// to 48000, which is what media containers carry and the only rate Opus decodes at, so no rate
    /// conversion runs. Set it to zero to leave the shared output alone.
    /// </summary>
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>
    /// The settings handed to the video decoder. The session fills in the frame-buffer pool itself; everything
    /// else is yours.
    /// </summary>
    public VideoDecoderOptions DecoderOptions { get; set; } = new VideoDecoderOptions();

    /// <summary>
    /// True to keep decoding while the session is paused until the queues are full, so that resuming is
    /// instant. Defaults to true.
    /// </summary>
    /// <remarks>
    /// It never changes what is on SCREEN: a paused session holds the frame it is showing either way. It
    /// decides only whether the decoding thread keeps working ahead into its queues while paused, which costs
    /// a little power and buys an instant resume.
    /// </remarks>
    public bool DecodeAheadWhilePaused { get; set; } = true;

    /// <summary>Returns a copy of these options.</summary>
    /// <returns>A new instance carrying the same values, with the decoder options copied too.</returns>
    public VideoPlaybackOptions Clone()
    {
        VideoPlaybackOptions clone = (VideoPlaybackOptions)MemberwiseClone();
        if (DecoderOptions != null) clone.DecoderOptions = DecoderOptions.Clone();
        return clone;
    }
}
