using System.Threading;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// The presenter's promise that it has finished reading a frame's memory on the graphics path.
/// </summary>
/// <remarks>
/// <para>
/// A texture upload is asked for on one thread and carried out by a driver on its own schedule, so returning
/// a frame's buffer to the pool the moment the upload was REQUESTED would let a decoder overwrite memory the
/// driver is still reading. The presenter therefore puts one of these in
/// <see cref="VideoFrameBuffer.Tag" /> before the upload and calls <see cref="Signal" /> once the graphics
/// commands have been flushed and submitted, at which point the host memory is no longer referenced.
/// </para>
/// <para>
/// <see cref="CodeBrix.VideoPlayback.Frames.PinnedFrameBufferPool" /> parks a returned buffer whose fence has
/// not signalled and recycles it on the next pool operation - or immediately, when somebody calls
/// <c>PumpFences()</c>.
/// </para>
/// <para>Every member is safe to call from any thread.</para>
/// </remarks>
public sealed class GpuUploadFence : IVideoFrameFence
{
    private int signaled;

    /// <summary>Creates an unsignalled fence.</summary>
    public GpuUploadFence()
    {
    }

    /// <inheritdoc />
    public bool IsSignaled => Volatile.Read(ref signaled) != 0;

    /// <summary>
    /// Declares that the work reading the buffer has been submitted and the memory may be reused.
    /// </summary>
    /// <remarks>Signalling an already-signalled fence does nothing.</remarks>
    public void Signal() => Volatile.Write(ref signaled, 1);

    /// <inheritdoc />
    public override string ToString() => IsSignaled ? "upload fence: signalled" : "upload fence: waiting";
}
