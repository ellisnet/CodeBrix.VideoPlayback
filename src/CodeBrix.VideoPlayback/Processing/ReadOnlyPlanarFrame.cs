using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// A read-only view of one picture's planes - the SOURCE side of the frame-level processing calls
/// (<see cref="FrameScaler" />, <see cref="FrameRotator" />, <see cref="PlanarLutGrader" />).
/// </summary>
/// <remarks>
/// <para>
/// It owns nothing: it is a set of spans plus the geometry that says how to read them, built on the stack
/// around whatever memory the caller already has - a decoded frame, a pooled array, an authoring frame's
/// planes. That is what lets one primitive serve a playback frame, an authoring frame and an export backdrop
/// without this package referencing any of their types.
/// </para>
/// <para>
/// <b>Sample conventions.</b> Eight-bit samples are one byte. Ten-bit and twelve-bit samples are two bytes,
/// little-endian, with the value in the LOW bits (ten-bit runs 0..1023). Strides are in BYTES. Chroma planes
/// of 4:2:0 and 4:2:2 are half the luma size, rounded UP. An alpha plane, when present, is full size at the
/// same bit depth. <see cref="ProcessingFrameLayout.Rgba" /> is eight-bit only and carries its alpha inside the
/// one plane.
/// </para>
/// </remarks>
public readonly ref struct ReadOnlyPlanarFrame
{
    private ReadOnlyPlanarFrame(
        ProcessingFrameLayout layout,
        int width,
        int height,
        int bitDepth,
        ReadOnlySpan<byte> plane0,
        int stride0,
        ReadOnlySpan<byte> plane1,
        int stride1,
        ReadOnlySpan<byte> plane2,
        int stride2,
        ReadOnlySpan<byte> alpha,
        int alphaStride)
    {
        PlanarFrameChecks.Validate(
            layout, width, height, bitDepth,
            plane0.Length, stride0, plane1.Length, stride1, plane2.Length, stride2,
            alpha.IsEmpty ? -1 : alpha.Length, alphaStride);

        Layout = layout;
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Plane0 = plane0;
        Stride0 = stride0;
        Plane1 = plane1;
        Stride1 = stride1;
        Plane2 = plane2;
        Stride2 = stride2;
        Alpha = alpha;
        AlphaStride = alpha.IsEmpty ? 0 : alphaStride;
    }

    /// <summary>The plane layout.</summary>
    public ProcessingFrameLayout Layout { get; }

    /// <summary>The picture width in pixels (luma samples).</summary>
    public int Width { get; }

    /// <summary>The picture height in pixels (luma rows).</summary>
    public int Height { get; }

    /// <summary>Bits per sample: 8, 10 or 12 (always 8 for <see cref="ProcessingFrameLayout.Rgba" />).</summary>
    public int BitDepth { get; }

    /// <summary>1 for eight-bit samples, 2 for ten-bit and twelve-bit samples.</summary>
    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    /// <summary>The largest sample value the bit depth allows: 255, 1023 or 4095.</summary>
    public int MaxSampleValue => (1 << BitDepth) - 1;

    /// <summary>The horizontal chroma shift: 1 for 4:2:0 and 4:2:2, otherwise 0.</summary>
    public int ChromaShiftX => ProcessingGuard.ChromaShiftX(Layout);

    /// <summary>The vertical chroma shift: 1 for 4:2:0, otherwise 0.</summary>
    public int ChromaShiftY => ProcessingGuard.ChromaShiftY(Layout);

    /// <summary>The chroma plane width in samples (0 when the layout has no chroma planes).</summary>
    public int ChromaWidth => ProcessingGuard.HasChroma(Layout) ? ProcessingGuard.ChromaSize(Width, ChromaShiftX) : 0;

    /// <summary>The chroma plane height in rows (0 when the layout has no chroma planes).</summary>
    public int ChromaHeight => ProcessingGuard.HasChroma(Layout) ? ProcessingGuard.ChromaSize(Height, ChromaShiftY) : 0;

    /// <summary>The luma plane - or, for <see cref="ProcessingFrameLayout.Rgba" />, the interleaved RGBA plane.</summary>
    public ReadOnlySpan<byte> Plane0 { get; }

    /// <summary>The stride of <see cref="Plane0" /> in bytes.</summary>
    public int Stride0 { get; }

    /// <summary>The first chroma (U, Cb) plane; empty for Gray and RGBA.</summary>
    public ReadOnlySpan<byte> Plane1 { get; }

    /// <summary>The stride of <see cref="Plane1" /> in bytes.</summary>
    public int Stride1 { get; }

    /// <summary>The second chroma (V, Cr) plane; empty for Gray and RGBA.</summary>
    public ReadOnlySpan<byte> Plane2 { get; }

    /// <summary>The stride of <see cref="Plane2" /> in bytes.</summary>
    public int Stride2 { get; }

    /// <summary>The separate alpha plane, or empty when there is none (always empty for RGBA).</summary>
    public ReadOnlySpan<byte> Alpha { get; }

    /// <summary>The stride of <see cref="Alpha" /> in bytes; 0 when there is no alpha plane.</summary>
    public int AlphaStride { get; }

    /// <summary>True when a separate alpha plane is present.</summary>
    public bool HasAlphaPlane => !Alpha.IsEmpty;

    /// <summary>Describes a 4:2:0 picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="bitDepth">8, 10 or 12.</param>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The luma stride in bytes.</param>
    /// <param name="u">The U (Cb) plane, <c>ceil(width/2)</c> by <c>ceil(height/2)</c>.</param>
    /// <param name="uStride">The U stride in bytes.</param>
    /// <param name="v">The V (Cr) plane, the same size as U.</param>
    /// <param name="vStride">The V stride in bytes.</param>
    /// <param name="alpha">An optional full-size alpha plane at the same bit depth.</param>
    /// <param name="alphaStride">The alpha stride in bytes; ignored when <paramref name="alpha" /> is empty.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, the bit depth or a stride is out of range.</exception>
    /// <exception cref="ArgumentException">A plane is too small for its geometry.</exception>
    public static ReadOnlyPlanarFrame CreateI420(
        int width,
        int height,
        int bitDepth,
        ReadOnlySpan<byte> y,
        int yStride,
        ReadOnlySpan<byte> u,
        int uStride,
        ReadOnlySpan<byte> v,
        int vStride,
        ReadOnlySpan<byte> alpha = default,
        int alphaStride = 0) =>
        new ReadOnlyPlanarFrame(ProcessingFrameLayout.I420, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a 4:2:2 picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="bitDepth">8, 10 or 12.</param>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The luma stride in bytes.</param>
    /// <param name="u">The U (Cb) plane, <c>ceil(width/2)</c> by <c>height</c>.</param>
    /// <param name="uStride">The U stride in bytes.</param>
    /// <param name="v">The V (Cr) plane, the same size as U.</param>
    /// <param name="vStride">The V stride in bytes.</param>
    /// <param name="alpha">An optional full-size alpha plane at the same bit depth.</param>
    /// <param name="alphaStride">The alpha stride in bytes; ignored when <paramref name="alpha" /> is empty.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, the bit depth or a stride is out of range.</exception>
    /// <exception cref="ArgumentException">A plane is too small for its geometry.</exception>
    public static ReadOnlyPlanarFrame CreateI422(
        int width,
        int height,
        int bitDepth,
        ReadOnlySpan<byte> y,
        int yStride,
        ReadOnlySpan<byte> u,
        int uStride,
        ReadOnlySpan<byte> v,
        int vStride,
        ReadOnlySpan<byte> alpha = default,
        int alphaStride = 0) =>
        new ReadOnlyPlanarFrame(ProcessingFrameLayout.I422, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a 4:4:4 picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="bitDepth">8, 10 or 12.</param>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The luma stride in bytes.</param>
    /// <param name="u">The U (Cb) plane, full size.</param>
    /// <param name="uStride">The U stride in bytes.</param>
    /// <param name="v">The V (Cr) plane, full size.</param>
    /// <param name="vStride">The V stride in bytes.</param>
    /// <param name="alpha">An optional full-size alpha plane at the same bit depth.</param>
    /// <param name="alphaStride">The alpha stride in bytes; ignored when <paramref name="alpha" /> is empty.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, the bit depth or a stride is out of range.</exception>
    /// <exception cref="ArgumentException">A plane is too small for its geometry.</exception>
    public static ReadOnlyPlanarFrame CreateI444(
        int width,
        int height,
        int bitDepth,
        ReadOnlySpan<byte> y,
        int yStride,
        ReadOnlySpan<byte> u,
        int uStride,
        ReadOnlySpan<byte> v,
        int vStride,
        ReadOnlySpan<byte> alpha = default,
        int alphaStride = 0) =>
        new ReadOnlyPlanarFrame(ProcessingFrameLayout.I444, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a luma-only picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="bitDepth">8, 10 or 12.</param>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The luma stride in bytes.</param>
    /// <param name="alpha">An optional full-size alpha plane at the same bit depth.</param>
    /// <param name="alphaStride">The alpha stride in bytes; ignored when <paramref name="alpha" /> is empty.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, the bit depth or a stride is out of range.</exception>
    /// <exception cref="ArgumentException">A plane is too small for its geometry.</exception>
    public static ReadOnlyPlanarFrame CreateGray(
        int width,
        int height,
        int bitDepth,
        ReadOnlySpan<byte> y,
        int yStride,
        ReadOnlySpan<byte> alpha = default,
        int alphaStride = 0) =>
        new ReadOnlyPlanarFrame(ProcessingFrameLayout.Gray, width, height, bitDepth, y, yStride, default, 0, default, 0, alpha, alphaStride);

    /// <summary>Describes an interleaved eight-bit RGBA picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="rgba">The pixels, four bytes each in R, G, B, A order.</param>
    /// <param name="stride">The row stride in bytes, at least <c>width * 4</c>.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension or the stride is out of range.</exception>
    /// <exception cref="ArgumentException">The plane is too small for its geometry.</exception>
    public static ReadOnlyPlanarFrame CreateRgba(int width, int height, ReadOnlySpan<byte> rgba, int stride) =>
        new ReadOnlyPlanarFrame(ProcessingFrameLayout.Rgba, width, height, 8, rgba, stride, default, 0, default, 0, default, 0);

    /// <summary>A short description: layout, size, depth and whether an alpha plane is present.</summary>
    /// <returns>The description.</returns>
    public override string ToString() =>
        $"{Layout} {Width}x{Height} {BitDepth}-bit{(HasAlphaPlane ? " +alpha" : string.Empty)}";
}
