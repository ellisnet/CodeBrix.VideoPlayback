using System;

namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// Carries a failure that happened during playback, where there was no caller left to throw at.
/// </summary>
public sealed class MediaFailedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="exception">What went wrong.</param>
    public MediaFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    /// <summary>What went wrong. Its message names the piece that failed.</summary>
    public Exception Exception { get; }

    /// <summary>The failure's message, for a caller that only wants to show something.</summary>
    public string Message => Exception == null ? string.Empty : Exception.Message;
}
