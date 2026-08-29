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
}
