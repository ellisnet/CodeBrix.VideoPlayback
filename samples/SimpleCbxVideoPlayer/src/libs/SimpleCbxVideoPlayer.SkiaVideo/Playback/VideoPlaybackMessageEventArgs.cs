using System;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Carries a message the application is meant to show a person, word for word.</summary>
/// <remarks>
/// The messages the video packages produce name what is missing and what to do about it - "video codec
/// 'av01' has no registered decoder", and the package to add. This sample shows them verbatim rather than
/// replacing them with something friendlier and less useful.
/// </remarks>
public sealed class VideoPlaybackMessageEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="message">The message to show.</param>
    public VideoPlaybackMessageEventArgs(string message) => Message = message ?? string.Empty;

    /// <summary>The message to show.</summary>
    public string Message { get; }
}
