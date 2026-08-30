using System;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// Works out where a picture goes inside a rectangle - the letterbox arithmetic, on its own, with no drawing
/// library anywhere near it.
/// </summary>
/// <remarks>
/// Every presenter in the family fits a frame into a host rectangle the same way, and a host view needs the
/// same answer in order to say "where on screen is this video pixel" for a click, a caption position or a hit
/// test. One pure function serves all of them, so a presenter and the view above it can never disagree about
/// where the picture is.
/// </remarks>
public static class VideoStretchMath
{
    /// <summary>Works out where a picture goes inside a rectangle.</summary>
    /// <param name="destination">The rectangle to fit the picture into.</param>
    /// <param name="contentWidth">The picture's display width.</param>
    /// <param name="contentHeight">The picture's display height.</param>
    /// <param name="stretch">How to fit it.</param>
    /// <returns>
    /// The rectangle the picture should be drawn into. For <see cref="VideoStretch.UniformToFill" /> and
    /// <see cref="VideoStretch.None" /> it can be larger than <paramref name="destination" />, and the caller
    /// is expected to clip.
    /// </returns>
    /// <remarks>
    /// A picture with no size - either dimension zero or negative - is given the whole destination, because
    /// there is no aspect ratio to preserve and nothing sensible to centre.
    /// </remarks>
    public static VideoRectangle ComputeDestination(
        VideoRectangle destination,
        int contentWidth,
        int contentHeight,
        VideoStretch stretch)
    {
        if (contentWidth <= 0 || contentHeight <= 0) return destination;
        if (stretch == VideoStretch.Fill) return destination;

        float scale;
        switch (stretch)
        {
            case VideoStretch.None:
                scale = 1f;
                break;

            case VideoStretch.UniformToFill:
                scale = Math.Max(destination.Width / contentWidth, destination.Height / contentHeight);
                break;

            default:
                scale = Math.Min(destination.Width / contentWidth, destination.Height / contentHeight);
                break;
        }

        float width = contentWidth * scale;
        float height = contentHeight * scale;
        float left = destination.Left + ((destination.Width - width) / 2f);
        float top = destination.Top + ((destination.Height - height) / 2f);

        return VideoRectangle.Create(left, top, width, height);
    }
}
