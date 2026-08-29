namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>
/// The rules the lookup-table panel follows: when it accepts input, what it says when it does not, and
/// when the chain it holds can be baked to a file.
/// </summary>
/// <remarks>
/// These are pure functions on purpose. The panel's behaviour is a matrix - transport state against the
/// render path that is actually running - and a matrix is worth testing without a window.
/// </remarks>
public static class LutPanelPolicy
{
    /// <summary>What the panel says when the picture is composed on the processor.</summary>
    public const string CpuNote =
        "Lookup tables are applied on the GPU path only in this sample, so the panel is off while the "
        + "picture is composed on the processor.";

    /// <summary>What the panel says while the picture is running.</summary>
    public const string PlayingNote =
        "Pause or stop the video to change the tables. The chain is applied when you press Play.";

    /// <summary>Whether the tick boxes and percentage boxes accept input.</summary>
    /// <param name="backend">The path the picture is actually being composed on.</param>
    /// <param name="transport">Where the transport is.</param>
    /// <returns>
    /// True only on the graphics path, and only while the picture is NOT running. The chain is applied
    /// when Play is pressed, so a table that changed under a running picture would be a change nobody
    /// asked for at a moment nobody chose.
    /// </returns>
    public static bool IsEditable(VideoRenderBackendOption backend, VideoTransportState transport) =>
        backend == VideoRenderBackendOption.Gpu && transport != VideoTransportState.Playing;

    /// <summary>The sentence that explains a panel that is not accepting input.</summary>
    /// <param name="backend">The path the picture is actually being composed on.</param>
    /// <param name="transport">Where the transport is.</param>
    /// <returns>The note to show, or an empty string when the panel is editable.</returns>
    /// <remarks>
    /// The processor note wins when both apply: it is the more fundamental of the two, because pausing
    /// would not make the tables take effect.
    /// </remarks>
    public static string GetNote(VideoRenderBackendOption backend, VideoTransportState transport)
    {
        if (backend != VideoRenderBackendOption.Gpu) { return CpuNote; }

        return transport == VideoTransportState.Playing ? PlayingNote : string.Empty;
    }

    /// <summary>Whether the applied chain can be baked to a ".cube" file.</summary>
    /// <param name="appliedTableCount">How many tables the presenter's chain actually holds.</param>
    /// <param name="transport">Where the transport is.</param>
    /// <param name="effectsActive">Whether the chain is actually being applied to the picture.</param>
    /// <returns>
    /// True when there is a chain, it is on screen, and there is a picture for it to be on: a bake writes
    /// WHAT IS SHOWING, so a chain that has been ticked but not yet applied, or one the processor path is
    /// ignoring, has nothing to bake.
    /// </returns>
    public static bool CanBake(int appliedTableCount, VideoTransportState transport, bool effectsActive) =>
        appliedTableCount > 0
        && effectsActive
        && transport != VideoTransportState.Stopped;
}
