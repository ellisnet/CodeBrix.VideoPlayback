using System;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Says where playback has reached.</summary>
public sealed class VideoPositionEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="position">Where playback has reached.</param>
    /// <param name="duration">How long the whole file is.</param>
    public VideoPositionEventArgs(TimeSpan position, TimeSpan duration)
    {
        Position = position;
        Duration = duration;
    }

    /// <summary>Where playback has reached - the audio clock, when the file carries sound.</summary>
    public TimeSpan Position { get; }

    /// <summary>How long the whole file is.</summary>
    public TimeSpan Duration { get; }
}
