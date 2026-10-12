using System;
using System.Buffers;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Applies a three-dimensional lookup table - a colour grade - to a planar Y'CbCr frame (4:2:0, 4:2:2 or
/// 4:4:4 at 8, 10 or 12 bits) and writes the result back in the SAME layout, matrix, range and bit depth; or
/// to an interleaved eight-bit RGBA picture. The managed counterpart of FFmpeg's <c>lut3d</c> filter for
/// engines that have no FFmpeg.
/// </summary>
/// <remarks>
/// <para>
/// <b>The path, per pixel.</b> Y'CbCr code values become normalised R'G'B' through the stated matrix (BT.709,
/// or BT.601 as <see cref="VideoMatrixCoefficients.Bt470Bg" /> / <see cref="VideoMatrixCoefficients.Smpte170M" />)
/// and range, in floating point with no intermediate rounding; the table is read with
/// <see cref="Lut3D.Sample(float, float, float, LutInterpolation, out float, out float, out float)" /> (so its
/// declared domain is honoured and inputs outside it are clamped, exactly as everywhere else in this
/// library); the graded colour goes back through the same matrix and range and is rounded half up and
/// clamped to the bit depth. The constants are the ones <see cref="CodeBrix.VideoPlayback.Color.VideoFrameConverter" />
/// uses. Transfer curves are not touched - the table is applied to the gamma-encoded values, as a .cube file
/// for video expects and as FFmpeg's filter does.
/// </para>
/// <para>
/// <b>Subsampled chroma.</b> For 4:2:0 and 4:2:2 the chroma is UPSAMPLED to every luma position for the grade
/// (bilinear, centre-sited: the 3:1 blend of the two nearest chroma samples on each subsampled axis) and
/// DOWNSAMPLED back by a BOX AVERAGE over the luma positions each chroma sample covers. What is averaged is
/// the grade's CHANGE to the chroma (graded minus upsampled), which is then added to the original chroma
/// sample - so an identity table returns the chroma planes exactly, rather than softened by the
/// up-then-down round trip, and fine chroma detail the grade does not touch survives.
/// </para>
/// <para>
/// <b>Not supported.</b> Luma-only frames (<see cref="ProcessingFrameLayout.Gray" />): a three-dimensional
/// table needs colour, so they are refused with <see cref="NotSupportedException" />. Matrices other than
/// BT.709 and BT.601 are refused the same way; an <see cref="VideoMatrixCoefficients.Unspecified" /> matrix
/// is an <see cref="ArgumentException" /> - say which one the frame uses. An unspecified range is studio range.
/// </para>
/// <para>
/// An alpha plane is copied unchanged when the destination has one; RGBA keeps its A byte. Source and
/// destination must not overlap. A grader holds no per-call state, so one instance may serve several threads.
/// </para>
/// </remarks>
public sealed class PlanarLutGrader
{
    /// <summary>Creates a grader for one table.</summary>
    /// <param name="lut">The table to apply.</param>
    /// <param name="interpolation">
    /// How the table is read between nodes. Tetrahedral by default - what grading tools and FFmpeg's
    /// <c>lut3d</c> (<c>interp=tetrahedral</c>, its default) mean by a lookup.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public PlanarLutGrader(Lut3D lut, LutInterpolation interpolation = LutInterpolation.Tetrahedral)
    {
        Lut = lut ?? throw new ArgumentNullException(nameof(lut));
        Interpolation = interpolation;
    }

    /// <summary>The table.</summary>
    public Lut3D Lut { get; }

    /// <summary>How the table is read between nodes.</summary>
    public LutInterpolation Interpolation { get; }

    /// <summary>Grades one frame with a table, without keeping a grader.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="source">The source frame.</param>
    /// <param name="destination">The destination: same layout, size and bit depth, not overlapping the source.</param>
    /// <param name="matrix">The Y'CbCr matrix of both frames (ignored for RGBA).</param>
    /// <param name="range">The sample range of both frames (ignored for RGBA).</param>
    /// <param name="interpolation">How the table is read between nodes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentException">The frames do not match, they overlap, or the matrix is unspecified.</exception>
    /// <exception cref="NotSupportedException">The frame is luma-only, or the matrix is not BT.709 or BT.601.</exception>
    public static void Apply(
        Lut3D lut,
        in ReadOnlyPlanarFrame source,
        in PlanarFrame destination,
        VideoMatrixCoefficients matrix,
        VideoColorRange range,
        LutInterpolation interpolation = LutInterpolation.Tetrahedral) =>
        new PlanarLutGrader(lut, interpolation).Apply(source, destination, matrix, range);

    /// <summary>Grades one frame.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="destination">The destination: same layout, size and bit depth, not overlapping the source.</param>
    /// <param name="matrix">The Y'CbCr matrix of both frames (ignored for RGBA).</param>
    /// <param name="range">The sample range of both frames (ignored for RGBA).</param>
    /// <exception cref="ArgumentException">The frames do not match, they overlap, or the matrix is unspecified.</exception>
    /// <exception cref="NotSupportedException">The frame is luma-only, or the matrix is not BT.709 or BT.601.</exception>
    public void Apply(in ReadOnlyPlanarFrame source, in PlanarFrame destination, VideoMatrixCoefficients matrix, VideoColorRange range)
    {
        PlanarFrameChecks.SameKind(source, destination);
        if (destination.Width != source.Width || destination.Height != source.Height)
        {
            throw new ArgumentException(
                $"The destination is {destination.Width}x{destination.Height} and the source {source.Width}x{source.Height}; grading keeps the size.",
                nameof(destination));
        }

        if (source.Layout == ProcessingFrameLayout.Gray)
        {
            throw new NotSupportedException(
                "A three-dimensional lookup table needs colour, and a luma-only (Gray) frame has none; grade it before it loses its chroma, or convert it to 4:4:4 first.");
        }

        if (source.Layout == ProcessingFrameLayout.Rgba)
        {
            ApplyRgba(source, destination);
            return;
        }

        YuvRgbMatrix conversion = YuvRgbMatrix.Create(matrix, range, source.BitDepth);
        ApplyPlanar(source, destination, conversion);

        if (destination.HasAlphaPlane)
        {
            int rowBytes = source.Width * source.BytesPerSample;
            for (int row = 0; row < source.Height; row++)
            {
                source.Alpha.Slice(row * source.AlphaStride, rowBytes).CopyTo(destination.Alpha.Slice(row * destination.AlphaStride, rowBytes));
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"LUT grader ({Lut}, {Interpolation})";

    private void ApplyRgba(in ReadOnlyPlanarFrame source, in PlanarFrame destination)
    {
        const float Inverse = 1f / 255f;

        for (int row = 0; row < source.Height; row++)
        {
            ReadOnlySpan<byte> input = source.Plane0.Slice(row * source.Stride0, source.Width * 4);
            Span<byte> output = destination.Plane0.Slice(row * destination.Stride0, source.Width * 4);

            for (int at = 0; at < input.Length; at += 4)
            {
                Lut.Sample(input[at] * Inverse, input[at + 1] * Inverse, input[at + 2] * Inverse, Interpolation, out float red, out float green, out float blue);
                output[at] = ToByte(red);
                output[at + 1] = ToByte(green);
                output[at + 2] = ToByte(blue);
                output[at + 3] = input[at + 3];
            }
        }
    }

    private void ApplyPlanar(in ReadOnlyPlanarFrame source, in PlanarFrame destination, YuvRgbMatrix conversion)
    {
        int width = source.Width;
        int height = source.Height;
        int bytes = source.BytesPerSample;
        int shiftX = source.ChromaShiftX;
        int shiftY = source.ChromaShiftY;
        int chromaWidth = source.ChromaWidth;
        int chromaHeight = source.ChromaHeight;

        double[] changes = ArrayPool<double>.Shared.Rent(chromaWidth * 2);
        int[] counts = ArrayPool<int>.Shared.Rent(chromaWidth);

        try
        {
            Span<double> changeU = changes.AsSpan(0, chromaWidth);
            Span<double> changeV = changes.AsSpan(chromaWidth, chromaWidth);
            Span<int> count = counts.AsSpan(0, chromaWidth);

            for (int chromaRow = 0; chromaRow < chromaHeight; chromaRow++)
            {
                changeU.Clear();
                changeV.Clear();
                count.Clear();

                int firstRow = chromaRow << shiftY;
                int endRow = Math.Min(height, (chromaRow + 1) << shiftY);

                for (int y = firstRow; y < endRow; y++)
                {
                    Taps(y, shiftY, chromaHeight, out int rowA, out int rowB, out double weightB);
                    ReadOnlySpan<byte> lumaRow = source.Plane0.Slice(y * source.Stride0, width * bytes);
                    Span<byte> outputRow = destination.Plane0.Slice(y * destination.Stride0, width * bytes);
                    ReadOnlySpan<byte> uA = source.Plane1.Slice(rowA * source.Stride1, chromaWidth * bytes);
                    ReadOnlySpan<byte> uB = source.Plane1.Slice(rowB * source.Stride1, chromaWidth * bytes);
                    ReadOnlySpan<byte> vA = source.Plane2.Slice(rowA * source.Stride2, chromaWidth * bytes);
                    ReadOnlySpan<byte> vB = source.Plane2.Slice(rowB * source.Stride2, chromaWidth * bytes);

                    for (int x = 0; x < width; x++)
                    {
                        Taps(x, shiftX, chromaWidth, out int columnA, out int columnB, out double weightRight);

                        double u = Blend(uA, uB, columnA, columnB, weightB, weightRight, bytes);
                        double v = Blend(vA, vB, columnA, columnB, weightB, weightRight, bytes);

                        conversion.ToRgb(Read(lumaRow, x, bytes), u, v, out float red, out float green, out float blue);
                        Lut.Sample(red, green, blue, Interpolation, out red, out green, out blue);
                        conversion.ToYuv(red, green, blue, out double gradedY, out double gradedU, out double gradedV);

                        Write(outputRow, x, bytes, conversion.Quantise(gradedY));

                        int cell = x >> shiftX;
                        changeU[cell] += gradedU - u;
                        changeV[cell] += gradedV - v;
                        count[cell]++;
                    }
                }

                ReadOnlySpan<byte> originalU = source.Plane1.Slice(chromaRow * source.Stride1, chromaWidth * bytes);
                ReadOnlySpan<byte> originalV = source.Plane2.Slice(chromaRow * source.Stride2, chromaWidth * bytes);
                Span<byte> outputU = destination.Plane1.Slice(chromaRow * destination.Stride1, chromaWidth * bytes);
                Span<byte> outputV = destination.Plane2.Slice(chromaRow * destination.Stride2, chromaWidth * bytes);

                for (int cell = 0; cell < chromaWidth; cell++)
                {
                    Write(outputU, cell, bytes, conversion.Quantise(Read(originalU, cell, bytes) + (changeU[cell] / count[cell])));
                    Write(outputV, cell, bytes, conversion.Quantise(Read(originalV, cell, bytes) + (changeV[cell] / count[cell])));
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(changes);
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    /// <summary>
    /// The two chroma samples a luma position blends on one axis, and the weight of the second: centre-sited,
    /// so an even position leans a quarter towards the previous sample and an odd one a quarter towards the next.
    /// </summary>
    private static void Taps(int position, int shift, int chromaCount, out int first, out int second, out double secondWeight)
    {
        if (shift == 0)
        {
            first = position;
            second = position;
            secondWeight = 0d;
            return;
        }

        int k = position >> 1;
        int neighbour = (position & 1) == 0 ? k - 1 : k + 1;
        first = Math.Min(k, chromaCount - 1);
        second = Math.Clamp(neighbour, 0, chromaCount - 1);
        secondWeight = 0.25d;
    }

    private static double Blend(
        ReadOnlySpan<byte> rowA,
        ReadOnlySpan<byte> rowB,
        int columnA,
        int columnB,
        double weightRowB,
        double weightColumnB,
        int bytes)
    {
        double top = (Read(rowA, columnA, bytes) * (1d - weightColumnB)) + (Read(rowA, columnB, bytes) * weightColumnB);
        double bottom = (Read(rowB, columnA, bytes) * (1d - weightColumnB)) + (Read(rowB, columnB, bytes) * weightColumnB);
        return (top * (1d - weightRowB)) + (bottom * weightRowB);
    }

    private static int Read(ReadOnlySpan<byte> row, int index, int bytes) =>
        bytes == 1 ? row[index] : row[2 * index] | (row[(2 * index) + 1] << 8);

    private static void Write(Span<byte> row, int index, int bytes, int value)
    {
        if (bytes == 1)
        {
            row[index] = (byte)value;
            return;
        }

        row[2 * index] = (byte)value;
        row[(2 * index) + 1] = (byte)(value >> 8);
    }

    private static byte ToByte(float value)
    {
        int scaled = (int)((value * 255f) + 0.5f);
        if (scaled < 0) return 0;
        return scaled > 255 ? (byte)255 : (byte)scaled;
    }
}
