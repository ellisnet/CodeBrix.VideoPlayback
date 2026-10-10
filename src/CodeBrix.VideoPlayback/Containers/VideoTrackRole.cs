namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// What a video track carries: the picture itself, or the alpha channel that goes with the picture.
/// </summary>
/// <remarks>
/// Only the bespoke container's master flavour (<c>.cbvmaster</c>, format version 1) declares an alpha-plane
/// track. Every track of every other file is a <see cref="Picture" /> track, and so is every audio and caption
/// track, for which the question does not arise.
/// </remarks>
public enum VideoTrackRole
{
    /// <summary>The picture: a colour (or monochrome) video stream meant to be looked at.</summary>
    Picture = 0,

    /// <summary>
    /// The alpha channel of the file's picture track, stored as a monochrome video stream - luma only, full
    /// range, in lock step with the picture (same dimensions, bit depth, frame count, timestamps and key-frame
    /// positions). A player decodes it beside the picture and uses its samples as opacity; it is never shown
    /// on its own.
    /// </summary>
    AlphaPlane = 1,
}
