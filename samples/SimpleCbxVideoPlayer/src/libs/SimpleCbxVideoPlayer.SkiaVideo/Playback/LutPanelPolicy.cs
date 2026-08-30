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

    /// <summary>Whether the panel's chain can be baked to a ".cube" file.</summary>
    /// <param name="tickedTableCount">How many tables are TICKED IN THE PANEL, applied or not.</param>
    /// <param name="backend">The path the picture is actually being composed on.</param>
    /// <param name="transport">Where the transport is.</param>
    /// <returns>
    /// True when the panel is accepting input at all and something is ticked in it.
    /// </returns>
    /// <remarks>
    /// The Bake button is part of the panel and follows the panel: it is the panel's export, not the
    /// picture's. So it counts what is TICKED rather than what is applied - a chain that has never been
    /// played bakes perfectly well, because composing a chain is arithmetic on the tables and owes nothing
    /// to the frame on screen - and it is disabled exactly when the rest of the panel is: while the
    /// picture is running, and on the processor path.
    /// </remarks>
    public static bool CanBake(
        int tickedTableCount,
        VideoRenderBackendOption backend,
        VideoTransportState transport) =>
        tickedTableCount > 0 && IsEditable(backend, transport);
}
