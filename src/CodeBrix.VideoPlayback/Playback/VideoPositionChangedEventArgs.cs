using System;

namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// Carries the playback position as it advances.
/// </summary>
public sealed class VideoPositionChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="position">Where playback has reached.</param>
    /// <param name="duration">How long the media lasts, or <see cref="TimeSpan.Zero" /> when it is not known.</param>
    public VideoPositionChangedEventArgs(TimeSpan position, TimeSpan duration)
    {
        Position = position;
        Duration = duration;
    }

    /// <summary>Where playback has reached.</summary>
    public TimeSpan Position { get; }

    /// <summary>How long the media lasts, or <see cref="TimeSpan.Zero" /> when it is not known.</summary>
    public TimeSpan Duration { get; }
}
