using CodeBrix.VideoPlayback.Rendering;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.Skia;

/// <summary>
/// Converts between the playback library's drawing-free <see cref="VideoRectangle" /> and SkiaSharp's
/// <see cref="SKRect" />.
/// </summary>
/// <remarks>
/// The playback library says where a picture goes without naming a drawing library, so that the same
/// geometry serves every presenter. This is the two-line bridge at the edge of this one - useful to an
/// application as well, which meets a <see cref="VideoRectangle" /> in a composition context and draws in
/// <see cref="SKRect" />s.
/// </remarks>
public static class SkiaRectangles
{
    /// <summary>Turns a playback rectangle into a SkiaSharp one.</summary>
    /// <param name="rectangle">The rectangle to convert.</param>
    /// <returns>The same four edges, as an <see cref="SKRect" />.</returns>
    public static SKRect ToSKRect(this VideoRectangle rectangle) =>
        new SKRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    /// <summary>Turns a SkiaSharp rectangle into a playback one.</summary>
    /// <param name="rectangle">The rectangle to convert.</param>
    /// <returns>The same four edges, as a <see cref="VideoRectangle" />.</returns>
    public static VideoRectangle FromSKRect(SKRect rectangle) =>
        new VideoRectangle(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    /// <summary>Turns a SkiaSharp rectangle into a playback one.</summary>
    /// <param name="rectangle">The rectangle to convert.</param>
    /// <returns>The same four edges, as a <see cref="VideoRectangle" />.</returns>
    /// <remarks>The extension-method spelling of <see cref="FromSKRect" />, for fluent code.</remarks>
    public static VideoRectangle ToVideoRectangle(this SKRect rectangle) => FromSKRect(rectangle);
}
