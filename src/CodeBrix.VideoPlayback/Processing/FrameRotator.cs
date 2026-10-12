using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Turns and/or mirrors a whole picture - every plane of a 4:2:0, 4:4:4 or luma-only frame at 8, 10 or 12
/// bits, the alpha plane with it, or an interleaved eight-bit RGBA picture. The same order and geometry as
/// <see cref="PlaneRotator" />: flips first, then the clockwise rotation.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is refused, and why.</b> A quarter turn of a 4:2:0 frame turns each chroma plane too, which only
/// keeps the chroma grid whole when BOTH dimensions are even - an odd edge has a chroma sample covering one
/// luma column on one side, and after a quarter turn that half-covered edge would land on the wrong side of
/// the picture. So a 4:2:0 frame with an odd width or height is refused for 90 and 270 degrees with an
/// <see cref="ArgumentException" /> that names the dimension; crop or pad it to even first. A quarter turn of
/// 4:2:2 would produce 4:4:0, which no consumer here carries, so it is a <see cref="NotSupportedException" />.
/// Half turns and flips keep every layout's chroma geometry and are always accepted.
/// </para>
/// </remarks>
public static class FrameRotator
{
    /// <summary>Rotates and/or flips a frame into another.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="destination">
    /// The destination frame: same layout and bit depth, sized as <see cref="PlaneRotator.GetRotatedSize" />
    /// says, not overlapping the source. A source alpha plane with no destination alpha plane is not written.
    /// </param>
    /// <param name="angle">The clockwise rotation.</param>
    /// <param name="flipHorizontal">Mirror left-right first (in the source's orientation).</param>
    /// <param name="flipVertical">Mirror top-bottom first (in the source's orientation).</param>
    /// <exception cref="ArgumentException">
    /// The frames differ in layout or bit depth, the destination is the wrong size, they overlap, or a 4:2:0
    /// frame with an odd dimension was asked for a quarter turn.
    /// </exception>
    /// <exception cref="NotSupportedException">A 4:2:2 frame was asked for a quarter turn.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not a quarter turn.</exception>
    public static void Rotate(
        in ReadOnlyPlanarFrame source,
        in PlanarFrame destination,
        RotationAngle angle,
        bool flipHorizontal = false,
        bool flipVertical = false)
    {
        PlaneRotator.CheckAngle(angle);
        PlanarFrameChecks.SameKind(source, destination);
        CheckCanRotate(source.Layout, source.Width, source.Height, angle);

        PlaneRotator.GetRotatedSize(source.Width, source.Height, angle, out int width, out int height);
        if (destination.Width != width || destination.Height != height)
        {
            throw new ArgumentException(
                $"A {source.Width}x{source.Height} frame turned {(int)angle} degrees is {width}x{height}; the destination is "
                + $"{destination.Width}x{destination.Height}.",
                nameof(destination));
        }

        if (source.Layout == ProcessingFrameLayout.Rgba)
        {
            PlaneRotator.Rotate(source.Plane0, source.Width, source.Height, source.Stride0, destination.Plane0, destination.Stride0, 1, 4, angle, flipHorizontal, flipVertical);
            return;
        }

        int bytes = source.BytesPerSample;
        PlaneRotator.Rotate(source.Plane0, source.Width, source.Height, source.Stride0, destination.Plane0, destination.Stride0, bytes, 1, angle, flipHorizontal, flipVertical);

        if (ProcessingGuard.HasChroma(source.Layout))
        {
            PlaneRotator.Rotate(source.Plane1, source.ChromaWidth, source.ChromaHeight, source.Stride1, destination.Plane1, destination.Stride1, bytes, 1, angle, flipHorizontal, flipVertical);
            PlaneRotator.Rotate(source.Plane2, source.ChromaWidth, source.ChromaHeight, source.Stride2, destination.Plane2, destination.Stride2, bytes, 1, angle, flipHorizontal, flipVertical);
        }

        if (destination.HasAlphaPlane)
        {
            PlaneRotator.Rotate(source.Alpha, source.Width, source.Height, source.AlphaStride, destination.Alpha, destination.AlphaStride, bytes, 1, angle, flipHorizontal, flipVertical);
        }
    }

    /// <summary>Says whether a frame of a layout and size can be given a rotation, without throwing.</summary>
    /// <param name="layout">The frame layout.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="angle">The rotation.</param>
    /// <returns>False for a quarter turn of 4:2:2, or of 4:2:0 with an odd dimension; true otherwise.</returns>
    public static bool CanRotate(ProcessingFrameLayout layout, int width, int height, RotationAngle angle)
    {
        bool quarter = angle == RotationAngle.Clockwise90 || angle == RotationAngle.Clockwise270;
        if (!quarter) return true;
        if (layout == ProcessingFrameLayout.I422) return false;
        if (layout == ProcessingFrameLayout.I420) return (width & 1) == 0 && (height & 1) == 0;
        return true;
    }

    private static void CheckCanRotate(ProcessingFrameLayout layout, int width, int height, RotationAngle angle)
    {
        if (CanRotate(layout, width, height, angle)) return;

        if (layout == ProcessingFrameLayout.I422)
        {
            throw new NotSupportedException(
                $"A 4:2:2 frame turned {(int)angle} degrees would be 4:4:0, which these primitives do not carry; "
                + "convert it to 4:2:0 or 4:4:4 first.");
        }

        string odd = (width & 1) != 0 ? $"width {width}" : $"height {height}";
        throw new ArgumentException(
            $"A 4:2:0 frame of {width}x{height} cannot be turned {(int)angle} degrees: its {odd} is odd, and the chroma "
            + "geometry must stay whole - an odd edge's half-covered chroma column would land on the wrong side. "
            + "Crop or pad it to an even size first.",
            "source");
    }
}
