namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Which render path the presenter is actually running on.</summary>
public enum VideoRenderBackendOption
{
    /// <summary>The processor: the core's vector converter writes the picture.</summary>
    Cpu = 0,

    /// <summary>The graphics device: one shader converts the picture and applies the effect chain.</summary>
    Gpu = 1,
}
