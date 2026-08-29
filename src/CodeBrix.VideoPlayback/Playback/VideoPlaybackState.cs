namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// What a playback session is doing.
/// </summary>
public enum VideoPlaybackState
{
    /// <summary>Nothing has been opened.</summary>
    Idle = 0,

    /// <summary>A file is being opened: the container is being read and the decoders are being built.</summary>
    Opening = 1,

    /// <summary>Media is open and the clock is running.</summary>
    Playing = 2,

    /// <summary>Media is open and the clock is stopped where it stands.</summary>
    Paused = 3,

    /// <summary>Media is open and the position has been put back to the beginning.</summary>
    Stopped = 4,

    /// <summary>Playback reached the end of the media.</summary>
    Ended = 5,

    /// <summary>Something failed; see the exception carried by the failure event.</summary>
    Failed = 6,
}
