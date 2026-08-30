using System;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// An axis-aligned rectangle in floating-point pixels - where a picture sits, said in a way that needs no
/// drawing library.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the geometry of presenting video - the letterbox arithmetic, the rectangle a layer is
/// told the video occupies - can live in this package, which has no drawing dependency at all. A presenter
/// built on a drawing library converts to and from its own rectangle type at the edge; the arithmetic in
/// between is the same for every one of them.
/// </para>
/// <para>
/// The edges are held as they were given. Nothing is normalised, so a rectangle whose right edge is left of
/// its left edge keeps that shape and reports itself <see cref="IsEmpty" />.
/// </para>
/// </remarks>
public readonly struct VideoRectangle : IEquatable<VideoRectangle>
{
    /// <summary>The rectangle at the origin with no width and no height.</summary>
    public static readonly VideoRectangle Empty = new VideoRectangle(0f, 0f, 0f, 0f);

    /// <summary>Creates a rectangle from its four edges.</summary>
    /// <param name="left">The left edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="right">The right edge.</param>
    /// <param name="bottom">The bottom edge.</param>
    public VideoRectangle(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>The left edge.</summary>
    public float Left { get; }

    /// <summary>The top edge.</summary>
    public float Top { get; }

    /// <summary>The right edge.</summary>
    public float Right { get; }

    /// <summary>The bottom edge.</summary>
    public float Bottom { get; }

    /// <summary>How wide the rectangle is - its right edge less its left one.</summary>
    public float Width => Right - Left;

    /// <summary>How tall the rectangle is - its bottom edge less its top one.</summary>
    public float Height => Bottom - Top;

    /// <summary>The horizontal middle of the rectangle.</summary>
    public float MidX => Left + (Width / 2f);

    /// <summary>The vertical middle of the rectangle.</summary>
    public float MidY => Top + (Height / 2f);

    /// <summary>
    /// True when the rectangle covers no area at all - its width or its height is zero or negative.
    /// </summary>
    public bool IsEmpty => !(Width > 0f) || !(Height > 0f);

    /// <summary>Creates a rectangle from a corner and a size.</summary>
    /// <param name="left">The left edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="width">How wide it is.</param>
    /// <param name="height">How tall it is.</param>
    /// <returns>The rectangle.</returns>
    public static VideoRectangle Create(float left, float top, float width, float height) =>
        new VideoRectangle(left, top, left + width, top + height);

    /// <summary>Creates a rectangle at the origin with the given size.</summary>
    /// <param name="width">How wide it is.</param>
    /// <param name="height">How tall it is.</param>
    /// <returns>The rectangle.</returns>
    public static VideoRectangle Create(float width, float height) =>
        new VideoRectangle(0f, 0f, width, height);

    /// <summary>Says whether two rectangles have the same four edges.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns>True when every edge matches.</returns>
    public static bool operator ==(VideoRectangle left, VideoRectangle right) => left.Equals(right);

    /// <summary>Says whether two rectangles differ in any edge.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns>True when any edge differs.</returns>
    public static bool operator !=(VideoRectangle left, VideoRectangle right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(VideoRectangle other) =>
        Left.Equals(other.Left)
        && Top.Equals(other.Top)
        && Right.Equals(other.Right)
        && Bottom.Equals(other.Bottom);

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is VideoRectangle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "({0}, {1}) {2}x{3}",
            Left,
            Top,
            Width,
            Height);
}
