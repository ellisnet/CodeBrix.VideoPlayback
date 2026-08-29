namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Where the transport is, as far as the user interface needs to know.</summary>
/// <remarks>
/// The playback session has more states than this - opening, ended, failed - but a panel only has to know
/// whether the picture is RUNNING, because that is what decides whether its controls accept input.
/// </remarks>
public enum VideoTransportState
{
    /// <summary>Nothing is playing: no file is open, or it was stopped, or it reached its end.</summary>
    Stopped = 0,

    /// <summary>The picture is running.</summary>
    Playing = 1,

    /// <summary>A file is open and part-way through, with the last frame left on screen.</summary>
    Paused = 2,
}
