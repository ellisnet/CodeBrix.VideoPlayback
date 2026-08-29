namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Which render path the application asks the presenter for.</summary>
/// <remarks>
/// This mirrors the presenter's own choice so that the application - its view models, its pages and any
/// other host framework this library is dropped into - never has to name a type from the video packages.
/// </remarks>
public enum VideoRenderPathOption
{
    /// <summary>Take the graphics device when there is one, and the processor when there is not.</summary>
    GpuAuto = 0,

    /// <summary>Insist on the graphics device, and report a failure instead of degrading quietly.</summary>
    GpuNoFallback = 1,

    /// <summary>Compose on the processor whether or not a graphics device is available.</summary>
    Cpu = 2,
}
