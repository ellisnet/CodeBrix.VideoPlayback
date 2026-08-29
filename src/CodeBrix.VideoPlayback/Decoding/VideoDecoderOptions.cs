using System;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The knobs a decoder factory is handed when it builds a decoder: where frames should be written, how much
/// parallelism is welcome, and what counts as an unreasonable frame.
/// </summary>
/// <remarks>
/// These are the settings every video decoder understands. A decoder package with settings of its own
/// derives from this type and its factory checks for the derived type; a factory that is handed the base
/// type simply uses the defaults for anything extra.
/// </remarks>
public class VideoDecoderOptions
{
    private int threads;
    private int maxFrameDelay;
    private long frameSizeLimit = 8192L * 8192L;

    /// <summary>
    /// The pool decoded frames should be written into.
    /// </summary>
    /// <remarks>
    /// A decoder that reports <see cref="IVideoDecoder.SupportsExternalBuffers" /> installs this as its own
    /// allocator, so the samples land in memory the presenter can upload from with no copy anywhere. A
    /// decoder that cannot do that still uses the pool - it copies into a rented buffer once - so the frame
    /// path is identical either way.
    /// </remarks>
    public IVideoFrameBufferPool BufferPool { get; set; }

    /// <summary>
    /// How many threads the decoder may use, or 0 to let it choose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A negative value was assigned.</exception>
    public int Threads
    {
        get => threads;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The thread count cannot be negative.");
            threads = value;
        }
    }

    /// <summary>
    /// How many frames the decoder may keep in flight before it must produce one, or 0 to let it choose.
    /// </summary>
    /// <remarks>
    /// A larger number lets a frame-threaded decoder work further ahead and decode faster; a smaller one
    /// shortens the delay between a packet going in and a frame coming out, which is what a low-latency
    /// application wants.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A negative value was assigned.</exception>
    public int MaxFrameDelay
    {
        get => maxFrameDelay;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The frame delay cannot be negative.");
            maxFrameDelay = value;
        }
    }

    /// <summary>
    /// The largest frame, in luma samples (width times height), that will be decoded. Defaults to 8192 by
    /// 8192.
    /// </summary>
    /// <remarks>
    /// This is the guard against a hostile file: a stream header can legally claim enormous dimensions, and
    /// honouring that claim would allocate gigabytes before anything else noticed. A decoder that is asked
    /// for a larger frame refuses the stream with a message naming the requested size.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A value of zero or less was assigned.</exception>
    public long FrameSizeLimit
    {
        get => frameSizeLimit;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The frame-size limit must be greater than zero.");
            frameSizeLimit = value;
        }
    }

    /// <summary>
    /// True when the decoder may apply the film grain a stream asks for. Defaults to true.
    /// </summary>
    /// <remarks>
    /// Grain synthesis is applied after decoding and costs real time on a slow device; turning it off gives a
    /// cleaner but less faithful picture. A decoder that has no grain support ignores this.
    /// </remarks>
    public bool ApplyFilmGrain { get; set; } = true;

    /// <summary>Returns a copy of these options.</summary>
    /// <returns>A new instance carrying the same values.</returns>
    public virtual VideoDecoderOptions Clone() => (VideoDecoderOptions)MemberwiseClone();
}
