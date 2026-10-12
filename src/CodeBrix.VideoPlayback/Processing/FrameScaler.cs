using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Resizes a whole picture - every plane of a 4:2:0, 4:2:2, 4:4:4 or luma-only frame at 8, 10 or 12 bits, or
/// an interleaved eight-bit RGBA picture - so a caller does not have to work out the chroma geometry itself.
/// </summary>
/// <remarks>
/// <para>
/// Luma, and the alpha plane when both frames carry one, are scaled from the source size to the destination
/// size. Chroma planes are scaled from the source CHROMA size to the destination chroma size - half of each
/// subsampled dimension, rounded up - with the same kernel, centre-aligned on the chroma grid. Every plane is
/// clamped to the bit depth's range. The layout and bit depth never change; a source alpha plane with no
/// destination alpha plane is simply not written.
/// </para>
/// <para>
/// An instance holds a <see cref="PlaneScaler" /> for the luma geometry and one for the chroma geometry, so a
/// stream of same-size frames costs no table work. The static
/// <see cref="Scale(in ReadOnlyPlanarFrame, in PlanarFrame, ScalingFilter)" /> builds the scalers it needs
/// (reusing the per-thread cache inside <see cref="PlaneScaler" />).
/// </para>
/// </remarks>
public sealed class FrameScaler
{
    private readonly PlaneScaler luma;
    private readonly PlaneScaler chroma;

    /// <summary>Builds a frame scaler, and its weight tables, for one layout and geometry.</summary>
    /// <param name="layout">The layout of the frames it will scale.</param>
    /// <param name="sourceWidth">The source picture width.</param>
    /// <param name="sourceHeight">The source picture height.</param>
    /// <param name="destinationWidth">The destination picture width.</param>
    /// <param name="destinationHeight">The destination picture height.</param>
    /// <param name="filter">The kernel.</param>
    /// <exception cref="ArgumentOutOfRangeException">The layout, a dimension or the filter is out of range.</exception>
    public FrameScaler(
        ProcessingFrameLayout layout,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        ScalingFilter filter)
    {
        ProcessingGuard.Layout(layout, nameof(layout));
        Layout = layout;
        luma = new PlaneScaler(sourceWidth, sourceHeight, destinationWidth, destinationHeight, filter);

        if (ProcessingGuard.HasChroma(layout))
        {
            int shiftX = ProcessingGuard.ChromaShiftX(layout);
            int shiftY = ProcessingGuard.ChromaShiftY(layout);
            chroma = shiftX == 0 && shiftY == 0
                ? luma
                : new PlaneScaler(
                    ProcessingGuard.ChromaSize(sourceWidth, shiftX),
                    ProcessingGuard.ChromaSize(sourceHeight, shiftY),
                    ProcessingGuard.ChromaSize(destinationWidth, shiftX),
                    ProcessingGuard.ChromaSize(destinationHeight, shiftY),
                    filter);
        }
    }

    /// <summary>The layout this scaler serves.</summary>
    public ProcessingFrameLayout Layout { get; }

    /// <summary>The source picture width.</summary>
    public int SourceWidth => luma.SourceWidth;

    /// <summary>The source picture height.</summary>
    public int SourceHeight => luma.SourceHeight;

    /// <summary>The destination picture width.</summary>
    public int DestinationWidth => luma.DestinationWidth;

    /// <summary>The destination picture height.</summary>
    public int DestinationHeight => luma.DestinationHeight;

    /// <summary>The kernel.</summary>
    public ScalingFilter Filter => luma.Filter;

    /// <summary>Scales a frame with the kernel <see cref="PlaneScaler.ChooseDefault" /> picks for its geometry.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="destination">The destination frame: same layout and bit depth, the target size, no overlap.</param>
    /// <exception cref="ArgumentException">The frames differ in layout or bit depth, or overlap.</exception>
    public static void ScaleWithDefaultFilter(in ReadOnlyPlanarFrame source, in PlanarFrame destination) =>
        Scale(source, destination, PlaneScaler.ChooseDefault(source.Width, source.Height, destination.Width, destination.Height));

    /// <summary>Scales a frame with a given kernel.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="destination">The destination frame: same layout and bit depth, the target size, no overlap.</param>
    /// <param name="filter">The kernel.</param>
    /// <exception cref="ArgumentException">The frames differ in layout or bit depth, or overlap.</exception>
    public static void Scale(in ReadOnlyPlanarFrame source, in PlanarFrame destination, ScalingFilter filter)
    {
        PlanarFrameChecks.SameKind(source, destination);
        ScalePlanes(source, destination, filter, null, null);
    }

    /// <summary>Scales a frame with this instance's geometry and kernel.</summary>
    /// <param name="source">The source frame: this scaler's layout and source size.</param>
    /// <param name="destination">The destination frame: same layout and bit depth, this scaler's destination size, no overlap.</param>
    /// <exception cref="ArgumentException">A frame does not match this scaler, the frames differ in bit depth, or they overlap.</exception>
    public void Scale(in ReadOnlyPlanarFrame source, in PlanarFrame destination)
    {
        if (source.Layout != Layout)
        {
            throw new ArgumentException($"This scaler serves {Layout} frames; the source is {source.Layout}.", nameof(source));
        }

        if (source.Width != SourceWidth || source.Height != SourceHeight)
        {
            throw new ArgumentException(
                $"This scaler was built for a {SourceWidth}x{SourceHeight} source; the source is {source.Width}x{source.Height}.",
                nameof(source));
        }

        if (destination.Width != DestinationWidth || destination.Height != DestinationHeight)
        {
            throw new ArgumentException(
                $"This scaler was built for a {DestinationWidth}x{DestinationHeight} destination; the destination is "
                + $"{destination.Width}x{destination.Height}.",
                nameof(destination));
        }

        PlanarFrameChecks.SameKind(source, destination);
        ScalePlanes(source, destination, Filter, luma, chroma);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Layout} {luma}";

    private static void ScalePlanes(
        in ReadOnlyPlanarFrame source,
        in PlanarFrame destination,
        ScalingFilter filter,
        PlaneScaler luma,
        PlaneScaler chroma)
    {
        int bytes = source.BytesPerSample;
        int maximum = source.MaxSampleValue;

        if (source.Layout == ProcessingFrameLayout.Rgba)
        {
            ScaleOne(luma, source.Plane0, source.Width, source.Height, source.Stride0,
                destination.Plane0, destination.Width, destination.Height, destination.Stride0, 1, 4, filter, 255);
            return;
        }

        ScaleOne(luma, source.Plane0, source.Width, source.Height, source.Stride0,
            destination.Plane0, destination.Width, destination.Height, destination.Stride0, bytes, 1, filter, maximum);

        if (ProcessingGuard.HasChroma(source.Layout))
        {
            ScaleOne(chroma, source.Plane1, source.ChromaWidth, source.ChromaHeight, source.Stride1,
                destination.Plane1, destination.ChromaWidth, destination.ChromaHeight, destination.Stride1, bytes, 1, filter, maximum);
            ScaleOne(chroma, source.Plane2, source.ChromaWidth, source.ChromaHeight, source.Stride2,
                destination.Plane2, destination.ChromaWidth, destination.ChromaHeight, destination.Stride2, bytes, 1, filter, maximum);
        }

        if (destination.HasAlphaPlane)
        {
            ScaleOne(luma, source.Alpha, source.Width, source.Height, source.AlphaStride,
                destination.Alpha, destination.Width, destination.Height, destination.AlphaStride, bytes, 1, filter, maximum);
        }
    }

    private static void ScaleOne(
        PlaneScaler scaler,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int bytesPerSample,
        int channels,
        ScalingFilter filter,
        int maximum)
    {
        if (scaler != null)
        {
            scaler.Scale(source, sourceStride, destination, destinationStride, bytesPerSample, channels, maximum);
            return;
        }

        PlaneScaler.Scale(
            source, sourceWidth, sourceHeight, sourceStride,
            destination, destinationWidth, destinationHeight, destinationStride,
            bytesPerSample, channels, filter, maximum);
    }
}
