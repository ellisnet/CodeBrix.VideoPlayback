using System;
using System.Threading;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// One decoded picture: the planes it lives in, the size and colour it should be interpreted with, and when
/// it should be shown. Reference-counted, and immutable from the moment the decoder hands it over.
/// </summary>
/// <remarks>
/// <para>
/// A frame is reference-counted rather than singly owned because more than one part of the system genuinely
/// holds it at once: a video decoder keeps decoded pictures alive as prediction references for LATER frames
/// while a presenter is still reading the same picture for display. One owner cannot express that; a count
/// can.
/// </para>
/// <para>
/// The rules are short. Whoever obtains a frame owns ONE reference and must
/// <see cref="Dispose" /> it. To hand the same frame to somebody else who will outlive you, call
/// <see cref="Retain" /> and give them the result - it is the same object, with the count raised, and they
/// dispose it in turn. When the count reaches zero the buffer goes back to its pool and the frame object
/// itself is recycled, so a reference kept past its disposal is a bug that will read somebody else's picture.
/// </para>
/// <para>
/// Nothing writes to a frame after the decoder publishes it, so any number of threads may READ one at the
/// same time - which is exactly what lets a graphics upload run on the render thread while the decoder keeps
/// producing on its own.
/// </para>
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private int references;
    private IVideoFrameBufferPool owningPool;
    private VideoFrameBuffer buffer;
    private VideoFrameInfo info;

    private VideoFrame()
    {
    }

    internal static VideoFrame CreateUninitialized() => new VideoFrame();

    /// <summary>
    /// Publishes a decoded frame over a buffer, taking the first reference to it.
    /// </summary>
    /// <param name="buffer">
    /// The buffer holding the samples. Ownership passes to the frame: when the last reference drops, the
    /// buffer is returned to <paramref name="pool" />.
    /// </param>
    /// <param name="info">The frame's description.</param>
    /// <param name="pool">
    /// The pool the buffer must go back to, or null when nothing should be returned (a decoder that owns its
    /// own memory and frees it another way).
    /// </param>
    /// <returns>A frame with a reference count of one, which the caller must dispose.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer" /> is null.</exception>
    /// <remarks>
    /// The frame OBJECT comes from the pool too, through
    /// <see cref="IVideoFrameBufferPool.TakeFrame" />, so a pool that recycles them makes a decode loop
    /// allocate nothing at all once it is warm. Two sessions never share recycled frames, because they
    /// never share a pool.
    /// </remarks>
    public static VideoFrame Create(VideoFrameBuffer buffer, in VideoFrameInfo info, IVideoFrameBufferPool pool)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        // A pool that answers null is breaking its contract; allocating is a better answer than throwing,
        // because the frame that would be lost is a picture somebody is waiting for.
        VideoFrame frame = pool == null ? null : pool.TakeFrame();
        if (frame == null) frame = new VideoFrame();

        frame.buffer = buffer;
        frame.info = info;
        frame.owningPool = pool;
        Volatile.Write(ref frame.references, 1);
        return frame;
    }

    /// <summary>The buffer holding this frame's samples. Valid for as long as any reference is alive.</summary>
    /// <exception cref="ObjectDisposedException">Every reference to the frame has been released.</exception>
    public VideoFrameBuffer Buffer
    {
        get
        {
            ThrowIfReleased();
            return buffer;
        }
    }

    /// <summary>The luma plane.</summary>
    public VideoFramePlane Y => Buffer.Y;

    /// <summary>The first chroma plane (Cb). Empty for monochrome content.</summary>
    public VideoFramePlane U => Buffer.U;

    /// <summary>The second chroma plane (Cr). Empty for monochrome content.</summary>
    public VideoFramePlane V => Buffer.V;

    /// <summary>The visible width in luma samples.</summary>
    public int Width => info.Width;

    /// <summary>The visible height in luma samples.</summary>
    public int Height => info.Height;

    /// <summary>The width to show the frame at once the pixel aspect ratio has been applied.</summary>
    public int DisplayWidth => info.DisplayWidth;

    /// <summary>The height to show the frame at once the pixel aspect ratio has been applied.</summary>
    public int DisplayHeight => info.DisplayHeight;

    /// <summary>The plane layout and chroma subsampling.</summary>
    public VideoPixelLayout Layout => info.Layout;

    /// <summary>Bits per sample: 8, 10 or 12.</summary>
    public int BitDepth => info.BitDepth;

    /// <summary>The largest value a sample can take at this bit depth - <c>(1 &lt;&lt; BitDepth) - 1</c>.</summary>
    public int MaxSampleValue => info.BitDepth <= 0 ? 0 : (1 << info.BitDepth) - 1;

    /// <summary>How far right a luma coordinate shifts to reach the matching chroma coordinate.</summary>
    public int ChromaShiftX =>
        info.Layout == VideoPixelLayout.I420 || info.Layout == VideoPixelLayout.I422 ? 1 : 0;

    /// <summary>How far down a luma coordinate shifts to reach the matching chroma coordinate.</summary>
    public int ChromaShiftY => info.Layout == VideoPixelLayout.I420 ? 1 : 0;

    /// <summary>
    /// The raw presentation timestamp in the producer's own units; equal to <c>Timestamp.Ticks</c> for
    /// frames produced by this package's readers, which count in 100-nanosecond ticks.
    /// </summary>
    public long PresentationTimestamp => info.PresentationTimestamp;

    /// <summary>When the frame should be shown, relative to the start of the media.</summary>
    public TimeSpan Timestamp => info.Timestamp;

    /// <summary>The frame's zero-based number in decode order, or -1 when the producer does not count them.</summary>
    public long FrameNumber => info.FrameNumber;

    /// <summary>True when the frame was decoded from a key frame.</summary>
    public bool IsKeyFrame => info.IsKeyFrame;

    /// <summary>The colour description a presenter, shader or converter needs to interpret the samples.</summary>
    public VideoColorInfo Color => info.Color;

    /// <summary>Mastering metadata for high-dynamic-range content, or null when the stream carried none.</summary>
    public HdrMetadata Hdr => info.Hdr;

    /// <summary>The frame's whole description as one value.</summary>
    public VideoFrameInfo Info => info;

    /// <summary>How many references are currently outstanding. Zero means the frame has been recycled.</summary>
    public int ReferenceCount => Volatile.Read(ref references);

    /// <summary>Takes another reference to this frame.</summary>
    /// <returns>
    /// This same frame, with its reference count raised by one. The caller owns the returned reference and
    /// must dispose it.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Every reference to the frame has already been released.</exception>
    public VideoFrame Retain()
    {
        while (true)
        {
            int current = Volatile.Read(ref references);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(VideoFrame),
                    "This video frame has already been released; retaining it would read a recycled buffer.");
            }

            if (Interlocked.CompareExchange(ref references, current + 1, current) == current) return this;
        }
    }

    /// <summary>
    /// Releases one reference. When the last one goes, the buffer returns to its pool and the frame object is
    /// recycled.
    /// </summary>
    /// <remarks>
    /// Disposing more times than you retained is a bug; the extra calls are ignored rather than corrupting
    /// somebody else's frame. Reading a frame after its last reference has gone is a bug too, and one this
    /// type cannot catch reliably: by then the object may already be describing a completely different
    /// picture.
    /// </remarks>
    public void Dispose()
    {
        while (true)
        {
            int current = Volatile.Read(ref references);
            if (current <= 0) return;

            if (Interlocked.CompareExchange(ref references, current - 1, current) != current) continue;
            if (current != 1) return;

            VideoFrameBuffer released = buffer;
            IVideoFrameBufferPool pool = owningPool;
            buffer = null;
            owningPool = null;
            info = default;

            if (pool != null)
            {
                if (released != null) pool.Return(released);

                // The object itself goes back as well, cleared. Buffer first, then object: a pool that
                // hands the object straight out again must not be able to see it before the buffer it
                // described has been accounted for.
                pool.ReturnFrame(this);
            }

            return;
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"frame {info.FrameNumber} at {info.Timestamp}, {info.Width}x{info.Height} {info.Layout} {info.BitDepth}-bit"
        + (info.IsKeyFrame ? " (key)" : string.Empty);

    private void ThrowIfReleased()
    {
        if (Volatile.Read(ref references) <= 0)
        {
            throw new ObjectDisposedException(
                nameof(VideoFrame),
                "This video frame has already been released; its buffer belongs to the pool again.");
        }
    }
}
