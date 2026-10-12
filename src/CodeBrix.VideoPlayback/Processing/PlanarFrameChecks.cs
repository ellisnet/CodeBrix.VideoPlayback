using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// The geometry checks behind <see cref="ReadOnlyPlanarFrame" /> and <see cref="PlanarFrame" />, kept in one
/// place so both views refuse the same things in the same words.
/// </summary>
internal static class PlanarFrameChecks
{
    /// <summary>Validates a frame description. An alpha length of -1 means "no alpha plane".</summary>
    public static void Validate(
        ProcessingFrameLayout layout,
        int width,
        int height,
        int bitDepth,
        int length0,
        int stride0,
        int length1,
        int stride1,
        int length2,
        int stride2,
        int alphaLength,
        int alphaStride)
    {
        ProcessingGuard.Layout(layout, nameof(layout));
        ProcessingGuard.Dimension(width, nameof(width));
        ProcessingGuard.Dimension(height, nameof(height));
        ProcessingGuard.BitDepth(bitDepth, nameof(bitDepth));

        int bytes = bitDepth > 8 ? 2 : 1;

        if (layout == ProcessingFrameLayout.Rgba)
        {
            if (bitDepth != 8)
            {
                throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, $"An RGBA frame is 8-bit; bitDepth is {bitDepth}.");
            }

            ProcessingGuard.Plane(length0, width, height, stride0, 1, 4, "rgba", "stride");
            return;
        }

        ProcessingGuard.Plane(length0, width, height, stride0, bytes, 1, "y", "yStride");

        if (ProcessingGuard.HasChroma(layout))
        {
            int chromaWidth = ProcessingGuard.ChromaSize(width, ProcessingGuard.ChromaShiftX(layout));
            int chromaHeight = ProcessingGuard.ChromaSize(height, ProcessingGuard.ChromaShiftY(layout));
            ProcessingGuard.Plane(length1, chromaWidth, chromaHeight, stride1, bytes, 1, "u", "uStride");
            ProcessingGuard.Plane(length2, chromaWidth, chromaHeight, stride2, bytes, 1, "v", "vStride");
        }

        if (alphaLength >= 0)
        {
            ProcessingGuard.Plane(alphaLength, width, height, alphaStride, bytes, 1, "alpha", "alphaStride");
        }
    }

    /// <summary>Checks that two frames can be processed into one another: same layout and bit depth.</summary>
    public static void SameKind(in ReadOnlyPlanarFrame source, in PlanarFrame destination)
    {
        if (source.Layout != destination.Layout)
        {
            throw new ArgumentException(
                $"The destination is {destination.Layout} and the source {source.Layout}; these primitives keep the layout.",
                nameof(destination));
        }

        if (source.BitDepth != destination.BitDepth)
        {
            throw new ArgumentException(
                $"The destination is {destination.BitDepth}-bit and the source {source.BitDepth}-bit; these primitives keep the bit depth.",
                nameof(destination));
        }

        if (destination.HasAlphaPlane && !source.HasAlphaPlane)
        {
            throw new ArgumentException(
                "The destination has an alpha plane and the source has none to fill it from.",
                nameof(destination));
        }

        ProcessingGuard.NoOverlap(source.Plane0, destination.Plane0, nameof(destination));
        ProcessingGuard.NoOverlap(source.Plane1, destination.Plane1, nameof(destination));
        ProcessingGuard.NoOverlap(source.Plane2, destination.Plane2, nameof(destination));
        ProcessingGuard.NoOverlap(source.Alpha, destination.Alpha, nameof(destination));
    }
}
