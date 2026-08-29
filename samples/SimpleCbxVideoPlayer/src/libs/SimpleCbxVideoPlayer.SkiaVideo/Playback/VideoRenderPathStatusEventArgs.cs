using System;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Says which render path is running, and whether the effect chain is being applied.</summary>
public sealed class VideoRenderPathStatusEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="backend">The path that is running.</param>
    /// <param name="effectsActive">Whether the effect chain is actually applied to the picture.</param>
    /// <param name="reason">A short sentence saying why the presenter is on that path.</param>
    public VideoRenderPathStatusEventArgs(VideoRenderBackendOption backend, bool effectsActive, string reason)
    {
        Backend = backend;
        EffectsActive = effectsActive;
        Reason = reason ?? string.Empty;
    }

    /// <summary>The path that is running.</summary>
    public VideoRenderBackendOption Backend { get; }

    /// <summary>
    /// Whether the effect chain is actually applied. False on the processor path - colour lookup tables are
    /// a graphics-path feature in this application - and false when the chain is empty.
    /// </summary>
    public bool EffectsActive { get; }

    /// <summary>A short sentence saying why the presenter is on that path.</summary>
    public string Reason { get; }
}
