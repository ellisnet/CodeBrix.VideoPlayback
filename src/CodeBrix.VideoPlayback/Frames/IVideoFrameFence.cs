namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// A presenter's promise that it has finished reading a frame buffer.
/// </summary>
/// <remarks>
/// <para>
/// A graphics device does its work later than the code that asked for it. When a presenter uploads a frame's
/// planes to a texture, the upload has not necessarily HAPPENED by the time the call returns - so returning
/// the buffer to the pool at that moment would let a decoder overwrite memory the device is still reading.
/// </para>
/// <para>
/// The presenter therefore puts a fence in <see cref="VideoFrameBuffer.Tag" /> before it starts the upload.
/// The pool refuses to reuse a buffer whose fence is not yet signalled: the buffer is parked and re-examined
/// on the next pool operation, and by
/// <see cref="PinnedFrameBufferPool.PumpFences" /> if the caller wants to force the question. A buffer with
/// no fence in its tag returns immediately, which is what the GPU-free path does.
/// </para>
/// <para>
/// A <c>System.Func&lt;bool&gt;</c> works in the same place and means the same thing, for a presenter that
/// would rather write a lambda than a type.
/// </para>
/// <para>
/// <see cref="IsSignaled" /> is polled from whichever thread returns the buffer, which is not necessarily the
/// thread that created the fence, so an implementation must be thread-safe and must not block.
/// </para>
/// </remarks>
public interface IVideoFrameFence
{
    /// <summary>
    /// True once the work that was reading the buffer has completed and the memory may be reused.
    /// </summary>
    /// <remarks>Polled from any thread. Must return immediately and must never block.</remarks>
    bool IsSignaled { get; }
}
