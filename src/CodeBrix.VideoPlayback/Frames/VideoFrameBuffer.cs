using System;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// The memory a decoded frame lives in: three planes, where they came from, and a slot for the presenter's
/// bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// A buffer is owned by the <see cref="IVideoFrameBufferPool" /> that produced it and borrowed by whatever
/// holds a <see cref="VideoFrame" /> over it. It goes back to the pool when the last frame reference drops
/// AND any fence left in <see cref="Tag" /> reports itself signalled.
/// </para>
/// <para>
/// The layout every pool in this family promises is the one a software AV1 decoder wants, which is also the
/// one a graphics upload wants:
/// </para>
/// <list type="bullet">
///   <item><description>every plane pointer aligned to 64 bytes;</description></item>
///   <item><description>every stride a multiple of 64 bytes;</description></item>
///   <item><description>both dimensions rounded up to a multiple of 128 samples;</description></item>
///   <item><description>64 bytes of slack after the last row of each plane;</description></item>
///   <item><description>the U and V planes sharing one stride;</description></item>
///   <item><description>
///     8-bit samples stored as bytes, 10-bit and 12-bit samples as little-endian 16-bit words justified
///     towards the least significant bit.
///   </description></item>
/// </list>
/// </remarks>
public abstract class VideoFrameBuffer
{
    /// <summary>Initialises the base class.</summary>
    /// <param name="descriptor">The descriptor this buffer was allocated for.</param>
    /// <param name="storage">Where the samples live.</param>
    protected VideoFrameBuffer(VideoFrameBufferDescriptor descriptor, VideoFrameStorage storage)
    {
        Descriptor = descriptor;
        Storage = storage;
    }

    /// <summary>The descriptor this buffer was allocated for.</summary>
    public VideoFrameBufferDescriptor Descriptor { get; }

    /// <summary>Where the samples live.</summary>
    public VideoFrameStorage Storage { get; }

    /// <summary>The luma plane.</summary>
    public VideoFramePlane Y { get; protected set; }

    /// <summary>The first chroma plane (Cb). Empty for a monochrome layout.</summary>
    public VideoFramePlane U { get; protected set; }

    /// <summary>The second chroma plane (Cr). Empty for a monochrome layout. Shares its stride with <see cref="U" />.</summary>
    public VideoFramePlane V { get; protected set; }

    /// <summary>
    /// A handle to the graphics-device allocation behind a persistently-mapped upload buffer, or
    /// <see cref="IntPtr.Zero" /> for ordinary host memory.
    /// </summary>
    /// <remarks>
    /// Reserved for the day a decoder writes straight into memory the graphics device can read without a
    /// copy. Nothing in this package produces a non-zero value yet.
    /// </remarks>
    public IntPtr MappedGpuHandle { get; protected set; }

    /// <summary>
    /// A slot the presenter owns, opaque to everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool reads it for exactly one purpose: if it holds an <see cref="IVideoFrameFence" /> or a
    /// <c>System.Func&lt;bool&gt;</c>, the buffer is not reused until that fence says the presenter has
    /// finished with the memory. Anything else in the slot is ignored by the pool and simply carried along
    /// with the buffer.
    /// </para>
    /// <para>
    /// A pool clears the tag when it hands a buffer out, so a rented buffer always starts with a null tag.
    /// </para>
    /// </remarks>
    public object Tag { get; set; }

    /// <summary>
    /// The pool generation this buffer belongs to; a pool increments it whenever the allocation size for a
    /// descriptor changes, so a stale buffer can be recognised and discarded rather than reused.
    /// </summary>
    public int Generation { get; protected set; }

    /// <summary>Gets the plane at the given index: 0 for Y, 1 for U, 2 for V.</summary>
    /// <param name="index">The plane index.</param>
    /// <returns>The requested plane.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is not 0, 1 or 2.</exception>
    public VideoFramePlane GetPlane(int index) =>
        index switch
        {
            0 => Y,
            1 => U,
            2 => V,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A frame buffer has three planes: 0, 1 and 2."),
        };

    /// <summary>
    /// Reports whether the fence in <see cref="Tag" />, if there is one, says the memory may be reused.
    /// </summary>
    /// <returns>
    /// True when there is no fence, when the tag holds something that is not a fence, or when the fence is
    /// signalled; false while a fence is still outstanding.
    /// </returns>
    public bool IsFenceSignaled()
    {
        object tag = Tag;
        if (tag == null) return true;
        if (tag is IVideoFrameFence fence) return fence.IsSignaled;
        if (tag is Func<bool> predicate) return predicate();
        return true;
    }
}
