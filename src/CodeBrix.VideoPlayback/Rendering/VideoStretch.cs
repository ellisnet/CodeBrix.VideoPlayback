namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// How a composed video frame is fitted into the rectangle it is being drawn into.
/// </summary>
/// <remarks>
/// The aspect ratio these modes preserve is the frame's DISPLAY aspect ratio - its
/// <c>DisplayWidth</c> by <c>DisplayHeight</c> - not its coded pixel count, so anamorphic content comes out
/// the shape its author intended.
/// </remarks>
public enum VideoStretch
{
    /// <summary>
    /// Draw the frame at its own display size, centred in the destination and clipped to it. Nothing is
    /// scaled, so a frame larger than the destination loses its edges and a smaller one leaves a border.
    /// </summary>
    None = 0,

    /// <summary>
    /// Stretch the frame to cover the destination exactly, ignoring its aspect ratio. Nothing is cropped and
    /// nothing is left over, at the cost of distorting the picture whenever the two shapes disagree.
    /// </summary>
    Fill = 1,

    /// <summary>
    /// Scale the frame until it just fits inside the destination, preserving its aspect ratio and centring
    /// it - the ordinary "letterbox" behaviour, and the sensible default for a video surface.
    /// </summary>
    Uniform = 2,

    /// <summary>
    /// Scale the frame until it just covers the destination, preserving its aspect ratio and centring it, so
    /// that the overflowing edges are clipped away. No border is ever shown; some of the picture is lost.
    /// </summary>
    UniformToFill = 3,
}
