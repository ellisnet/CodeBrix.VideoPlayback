using System;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// Says which render path a presenter has settled on, and why.
/// </summary>
/// <remarks>
/// Raised once when the path is first resolved and again whenever it changes - which happens when the
/// application changes a presenter's <c>RenderPath</c>, supplies it a graphics context, or asks for the
/// graphics path on a machine that cannot give it one.
/// </remarks>
public sealed class VideoRenderPathChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="backend">The path that is now running.</param>
    /// <param name="reason">A short sentence saying why, suitable for a log line.</param>
    public VideoRenderPathChangedEventArgs(VideoRenderBackend backend, string reason)
    {
        Backend = backend;
        Reason = reason;
    }

    /// <summary>The path that is now running.</summary>
    public VideoRenderBackend Backend { get; }

    /// <summary>A short sentence saying why the presenter is on that path.</summary>
    public string Reason { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Backend}: {Reason}";
}
