namespace CodeBrix.VideoPlayback.Skia.Rendering;

/// <summary>
/// Which of the two render paths is actually running - the answer to "what is this presenter doing right
/// now", as opposed to <see cref="VideoRenderPath" />, which is what it was asked to do.
/// </summary>
public enum VideoRenderBackend
{
    /// <summary>
    /// The graphics device: the three planes are uploaded as textures and one shader pass does the colour
    /// conversion and the effect chain together.
    /// </summary>
    Gpu = 0,

    /// <summary>
    /// The processor: the core's vector colour converter turns the frame into BGRA pixels, which are composed
    /// on a raster surface.
    /// </summary>
    Cpu = 1,
}
