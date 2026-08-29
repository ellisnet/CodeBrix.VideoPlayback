namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// Supplies the memory decoded frames are written into, and takes it back when nothing is reading it any
/// more.
/// </summary>
/// <remarks>
/// <para>
/// A pool exists so that playback allocates nothing per frame. In the steady state - a stream whose frame
/// size does not change - <see cref="Rent" /> hands back memory a previous frame finished with, and the
/// managed heap sees no activity at all.
/// </para>
/// <para>
/// It also exists so that a decoder can write STRAIGHT into the memory a presenter will upload from. A
/// decoder that reports
/// <see cref="CodeBrix.VideoPlayback.Decoding.IVideoDecoder.SupportsExternalBuffers" /> installs the pool as
/// its own allocator, so there is no copy anywhere between the decoder's output and the graphics upload.
/// </para>
/// <para>
/// Every implementation must honour the layout promises documented on <see cref="VideoFrameBuffer" />, and
/// <see cref="Return" /> must be safe to call from ANY thread: a frame-threaded decoder releases pictures
/// from its own worker threads, and a presenter may drop the last reference on the render thread.
/// </para>
/// <para>
/// <see cref="TakeFrame" /> and <see cref="ReturnFrame" /> recycle the small <see cref="VideoFrame" />
/// OBJECT that describes a buffer, as distinct from the buffer itself. They have default implementations
/// that allocate one and throw it away, so an existing implementation keeps working untouched; a pool that
/// wants playback to allocate nothing at all overrides them.
/// </para>
/// </remarks>
public interface IVideoFrameBufferPool
{
    /// <summary>Takes a buffer of the requested shape out of the pool, allocating one only if it has none.</summary>
    /// <param name="descriptor">The frame shape the buffer must hold.</param>
    /// <returns>A buffer whose planes match the descriptor and whose <see cref="VideoFrameBuffer.Tag" /> is null.</returns>
    VideoFrameBuffer Rent(VideoFrameBufferDescriptor descriptor);

    /// <summary>
    /// Gives a buffer back once nothing is reading it. Called when the LAST <see cref="VideoFrame" />
    /// reference to it drops.
    /// </summary>
    /// <param name="buffer">The buffer to return. A null reference is ignored.</param>
    /// <remarks>
    /// Safe to call from any thread. If the buffer's <see cref="VideoFrameBuffer.Tag" /> holds an
    /// <see cref="IVideoFrameFence" /> that is not yet signalled, the implementation must hold the buffer
    /// back rather than reuse it.
    /// </remarks>
    void Return(VideoFrameBuffer buffer);

    /// <summary>
    /// Hands out a <see cref="VideoFrame" /> object for <see cref="VideoFrame.Create" /> to fill in - a
    /// recycled one where the implementation keeps them, a fresh one otherwise.
    /// </summary>
    /// <returns>
    /// A frame object carrying no state. Never null: a null return is a contract violation, and
    /// <see cref="VideoFrame.Create" /> allocates one rather than failing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A frame object is a small thing - about 128 bytes - but there is one per decoded picture, so a pool
    /// that does not recycle them turns smooth playback into a steady trickle of short-lived garbage. The
    /// buffers are pooled precisely so that does not happen; this is the other half of the same promise.
    /// </para>
    /// <para>
    /// An implementation may assume that a frame object it hands out has been RESET: every field is already
    /// null or zero when it arrives back through <see cref="ReturnFrame" />, so nothing needs clearing
    /// before it is given out again.
    /// </para>
    /// <para>
    /// It may also assume that each object it hands out comes back AT MOST ONCE. A frame that is never
    /// disposed never comes back at all, and a frame disposed more often than it was retained still comes
    /// back only once, so an implementation may keep a plain free list without guarding against duplicates.
    /// </para>
    /// <para>
    /// Called on whichever thread publishes a frame - the decode thread, in practice. The default
    /// implementation allocates a new object every time, which is exactly what happened before this member
    /// existed.
    /// </para>
    /// </remarks>
    VideoFrame TakeFrame() => VideoFrame.CreateUninitialized();

    /// <summary>
    /// Takes a frame object back once its last reference has gone, so it can be handed out again.
    /// </summary>
    /// <param name="frame">
    /// The frame object, already reset: it holds no buffer, no pool and no description by the time it gets
    /// here.
    /// </param>
    /// <remarks>
    /// <para>
    /// Safe to call from ANY thread, for the same reason <see cref="Return" /> is: the last reference to a
    /// frame may be dropped by a decoder's worker thread or by a presenter on the render thread.
    /// </para>
    /// <para>
    /// An implementation is free to ignore the object - keeping a bounded free list and dropping the rest is
    /// a perfectly good policy - and the default implementation does nothing at all.
    /// </para>
    /// </remarks>
    void ReturnFrame(VideoFrame frame)
    {
    }
}
