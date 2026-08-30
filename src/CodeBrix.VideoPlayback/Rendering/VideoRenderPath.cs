namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// Which of the two first-class render paths a presenter should use, and what it should do when the graphics
/// one is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// The value states the developer's INTENT, which is why there is no separate "allow fallback" flag: an
/// application that would rather see the video than see an error asks for <see cref="GpuAuto" />, and one
/// whose output is meaningless without its effect chain asks for <see cref="GpuNoFallback" /> and gets a
/// clear failure instead of a silently different picture.
/// </para>
/// <para>
/// What is actually running is always readable from a presenter's <c>ActiveRenderPath</c>, and a change is
/// announced by its <c>RenderPathChanged</c> event.
/// </para>
/// </remarks>
public enum VideoRenderPath
{
    /// <summary>
    /// Use the graphics device when one is available, and fall back to the processor when it is not - with no
    /// exception and no warning. The default.
    /// </summary>
    /// <remarks>
    /// In the fallback the configured effects are silently ignored unless the presenter's
    /// <c>AllowEffectsOnCpu</c> is set. The fallback itself is announced through the presenter's
    /// <c>RenderPathChanged</c> event and written once to <see cref="System.Diagnostics.Trace" /> at
    /// information level.
    /// </remarks>
    GpuAuto = 0,

    /// <summary>
    /// Use the graphics device, and fail with a clear error when no usable one has been supplied.
    /// </summary>
    /// <remarks>
    /// For an application whose picture is wrong rather than merely slower without the graphics path - one
    /// whose effect chain is the point of the display. The failure arrives as a
    /// <see cref="CodeBrix.VideoPlayback.VideoPlaybackException" /> naming what is missing.
    /// </remarks>
    GpuNoFallback = 1,

    /// <summary>
    /// Use the processor even where a graphics device exists: deterministic output, no driver in the picture,
    /// and the only choice on a machine with no usable graphics device at all.
    /// </summary>
    /// <remarks>This is a supported, tested configuration in its own right, not a degraded mode.</remarks>
    Cpu = 2,
}
