using System;

namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// Says that a new frame has reached the presenter and the display should repaint.
/// </summary>
/// <remarks>
/// The frame itself is not carried here on purpose. It is waiting in
/// <see cref="CodeBrix.VideoPlayback.Presentation.VideoFramePresenter.TryTakeLatest" />, and taking it from
/// there - on the thread that draws, at the moment it draws - is what keeps the newest frame on screen
/// instead of whichever one happened to be current when the event fired.
/// </remarks>
public sealed class VideoFrameReadyEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="timestamp">The new frame's timestamp.</param>
    /// <param name="frameNumber">The new frame's number in decode order, or -1 when unknown.</param>
    public VideoFrameReadyEventArgs(TimeSpan timestamp, long frameNumber)
    {
        Timestamp = timestamp;
        FrameNumber = frameNumber;
    }

    /// <summary>The new frame's timestamp.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>The new frame's number in decode order, or -1 when the producer does not count them.</summary>
    public long FrameNumber { get; }
}
