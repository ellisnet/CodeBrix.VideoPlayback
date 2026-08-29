using System;
using System.Threading;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Presentation;

/// <summary>
/// The hand-off between the thread that decodes and the thread that draws: a one-slot mailbox that always
/// holds the NEWEST frame and never makes either side wait for the other.
/// </summary>
/// <remarks>
/// <para>
/// A queue would be the wrong shape here. If the display is slower than the video, a queue grows until it
/// runs out of memory or the decoder stalls, and every frame it hands over is already out of date. A mailbox
/// says the useful thing instead: the drawing side always gets the most recent frame, and a frame nobody
/// collected in time is simply replaced.
/// </para>
/// <para>
/// The reference counting works out to three frames in flight at the very most - the one being decoded, the
/// one in the mailbox, and the one being drawn - which is why the pool settles at three buffers and stops
/// allocating.
/// </para>
/// <para>
/// <b>Who owns what.</b> <see cref="Post" /> takes its own reference, so the decoder keeps and disposes the
/// one it had. <see cref="TryTakeLatest" /> hands the mailbox's reference to the caller, who then owns it and
/// must dispose it. Neither call allocates.
/// </para>
/// <para>Every member is safe to call from any thread.</para>
/// </remarks>
public sealed class VideoFramePresenter : IDisposable
{
    private readonly object gate = new object();

    private VideoFrame pending;
    private long posted;
    private long presented;
    private long superseded;
    private long late;
    private bool disposed;

    /// <summary>Creates an empty presenter.</summary>
    public VideoFramePresenter()
    {
    }

    /// <summary>
    /// Raised when a new frame has arrived and the display should repaint.
    /// </summary>
    /// <remarks>
    /// It is raised on the thread that posted the frame - the decode thread - so a handler must do the least
    /// possible: mark the view dirty, request a repaint, and return. Drawing belongs on the drawing thread,
    /// which then calls <see cref="TryTakeLatest" />.
    /// </remarks>
    public event EventHandler Invalidated;

    /// <summary>True while a frame is waiting to be collected.</summary>
    public bool HasFrame
    {
        get
        {
            lock (gate) return pending != null;
        }
    }

    /// <summary>The timestamp of the frame most recently collected for display.</summary>
    public TimeSpan LastPresentedTimestamp { get; private set; }

    /// <summary>Takes a snapshot of the presenter's counters.</summary>
    /// <returns>The counters as they stood at the moment of the call.</returns>
    public VideoFramePresenterStatistics GetStatistics()
    {
        lock (gate) return new VideoFramePresenterStatistics(posted, presented, superseded, late);
    }

    /// <summary>Puts a frame in the mailbox, replacing whatever was there.</summary>
    /// <param name="frame">The frame to show. The presenter takes its own reference; the caller keeps its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    /// <remarks>
    /// A frame that was still sitting in the mailbox is released here, which is what returns its buffer to
    /// the pool, and is counted in <see cref="VideoFramePresenterStatistics.Superseded" />.
    /// </remarks>
    public void Post(VideoFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        VideoFrame replaced;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            replaced = pending;
            pending = frame.Retain();
            posted++;
            if (replaced != null) superseded++;
        }

        replaced?.Dispose();
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Collects the newest frame, if one is waiting.</summary>
    /// <param name="frame">
    /// The frame, carrying a reference the caller now owns and must dispose; null when the method returns
    /// false.
    /// </param>
    /// <returns>True when a frame was collected; false when the mailbox is empty.</returns>
    public bool TryTakeLatest(out VideoFrame frame)
    {
        lock (gate)
        {
            if (pending == null)
            {
                frame = null;
                return false;
            }

            frame = pending;
            pending = null;
            presented++;
            LastPresentedTimestamp = frame.Timestamp;
            return true;
        }
    }

    /// <summary>
    /// Records that the session dropped a frame for arriving after its moment - a decoder falling behind, not
    /// a display falling behind.
    /// </summary>
    /// <param name="count">How many frames were dropped. Defaults to one.</param>
    public void NotifyLateFrameDropped(int count = 1)
    {
        if (count <= 0) return;
        lock (gate) late += count;
    }

    /// <summary>Empties the mailbox, releasing anything in it. Used when playback stops or seeks.</summary>
    public void Clear()
    {
        VideoFrame dropped;
        lock (gate)
        {
            dropped = pending;
            pending = null;
        }

        dropped?.Dispose();
    }

    /// <summary>Sets every counter back to zero. The mailbox itself is left alone.</summary>
    public void ResetStatistics()
    {
        lock (gate)
        {
            posted = 0;
            presented = 0;
            superseded = 0;
            late = 0;
        }
    }

    /// <summary>Empties the mailbox and refuses any further frames.</summary>
    public void Dispose()
    {
        VideoFrame dropped;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            dropped = pending;
            pending = null;
        }

        dropped?.Dispose();
        Invalidated = null;
    }
}
