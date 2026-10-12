using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// A writable view of one picture's planes - the DESTINATION side of the frame-level processing calls. The
/// conventions are exactly those of <see cref="ReadOnlyPlanarFrame" />.
/// </summary>
/// <remarks>
/// The caller owns the memory; the processing call only writes the visible samples of each plane, so padding
/// at the end of a row is left as it was.
/// </remarks>
public readonly ref struct PlanarFrame
{
    private PlanarFrame(
        ProcessingFrameLayout layout,
        int width,
        int height,
        int bitDepth,
        Span<byte> plane0,
        int stride0,
        Span<byte> plane1,
        int stride1,
        Span<byte> plane2,
        int stride2,
        Span<byte> alpha,
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
    public Span<byte> Plane0 { get; }

    /// <summary>The stride of <see cref="Plane0" /> in bytes.</summary>
    public int Stride0 { get; }

    /// <summary>The first chroma (U, Cb) plane; empty for Gray and RGBA.</summary>
    public Span<byte> Plane1 { get; }

    /// <summary>The stride of <see cref="Plane1" /> in bytes.</summary>
    public int Stride1 { get; }

    /// <summary>The second chroma (V, Cr) plane; empty for Gray and RGBA.</summary>
    public Span<byte> Plane2 { get; }

    /// <summary>The stride of <see cref="Plane2" /> in bytes.</summary>
    public int Stride2 { get; }

    /// <summary>The separate alpha plane, or empty when there is none (always empty for RGBA).</summary>
    public Span<byte> Alpha { get; }

    /// <summary>The stride of <see cref="Alpha" /> in bytes; 0 when there is no alpha plane.</summary>
    public int AlphaStride { get; }

    /// <summary>True when a separate alpha plane is present.</summary>
    public bool HasAlphaPlane => !Alpha.IsEmpty;

    /// <summary>Describes a writable 4:2:0 picture.</summary>
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
    public static PlanarFrame CreateI420(
        int width,
        int height,
        int bitDepth,
        Span<byte> y,
        int yStride,
        Span<byte> u,
        int uStride,
        Span<byte> v,
        int vStride,
        Span<byte> alpha = default,
        int alphaStride = 0) =>
        new PlanarFrame(ProcessingFrameLayout.I420, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a writable 4:2:2 picture.</summary>
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
    public static PlanarFrame CreateI422(
        int width,
        int height,
        int bitDepth,
        Span<byte> y,
        int yStride,
        Span<byte> u,
        int uStride,
        Span<byte> v,
        int vStride,
        Span<byte> alpha = default,
        int alphaStride = 0) =>
        new PlanarFrame(ProcessingFrameLayout.I422, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a writable 4:4:4 picture.</summary>
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
    public static PlanarFrame CreateI444(
        int width,
        int height,
        int bitDepth,
        Span<byte> y,
        int yStride,
        Span<byte> u,
        int uStride,
        Span<byte> v,
        int vStride,
        Span<byte> alpha = default,
        int alphaStride = 0) =>
        new PlanarFrame(ProcessingFrameLayout.I444, width, height, bitDepth, y, yStride, u, uStride, v, vStride, alpha, alphaStride);

    /// <summary>Describes a writable luma-only picture.</summary>
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
    public static PlanarFrame CreateGray(
        int width,
        int height,
        int bitDepth,
        Span<byte> y,
        int yStride,
        Span<byte> alpha = default,
        int alphaStride = 0) =>
        new PlanarFrame(ProcessingFrameLayout.Gray, width, height, bitDepth, y, yStride, default, 0, default, 0, alpha, alphaStride);

    /// <summary>Describes a writable interleaved eight-bit RGBA picture.</summary>
    /// <param name="width">The picture width in pixels.</param>
    /// <param name="height">The picture height in pixels.</param>
    /// <param name="rgba">The pixels, four bytes each in R, G, B, A order.</param>
    /// <param name="stride">The row stride in bytes, at least <c>width * 4</c>.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension or the stride is out of range.</exception>
    /// <exception cref="ArgumentException">The plane is too small for its geometry.</exception>
    public static PlanarFrame CreateRgba(int width, int height, Span<byte> rgba, int stride) =>
        new PlanarFrame(ProcessingFrameLayout.Rgba, width, height, 8, rgba, stride, default, 0, default, 0, default, 0);

    /// <summary>The same planes, read-only - so a destination can be the next call's source.</summary>
    /// <returns>A read-only view over the same memory.</returns>
    public ReadOnlyPlanarFrame AsReadOnly() =>
        Layout switch
        {
            ProcessingFrameLayout.Gray => ReadOnlyPlanarFrame.CreateGray(Width, Height, BitDepth, Plane0, Stride0, Alpha, AlphaStride),
            ProcessingFrameLayout.I420 => ReadOnlyPlanarFrame.CreateI420(Width, Height, BitDepth, Plane0, Stride0, Plane1, Stride1, Plane2, Stride2, Alpha, AlphaStride),
            ProcessingFrameLayout.I422 => ReadOnlyPlanarFrame.CreateI422(Width, Height, BitDepth, Plane0, Stride0, Plane1, Stride1, Plane2, Stride2, Alpha, AlphaStride),
            ProcessingFrameLayout.I444 => ReadOnlyPlanarFrame.CreateI444(Width, Height, BitDepth, Plane0, Stride0, Plane1, Stride1, Plane2, Stride2, Alpha, AlphaStride),
            _ => ReadOnlyPlanarFrame.CreateRgba(Width, Height, Plane0, Stride0),
        };

    /// <summary>A short description: layout, size, depth and whether an alpha plane is present.</summary>
    /// <returns>The description.</returns>
    public override string ToString() =>
        $"{Layout} {Width}x{Height} {BitDepth}-bit{(HasAlphaPlane ? " +alpha" : string.Empty)}";
}
