using System;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// A three-dimensional colour lookup table: a cube of sample points, each saying what one input colour
/// becomes.
/// </summary>
/// <remarks>
/// <para>
/// This is the general form of a colour change. Every input colour lands inside one cell of the cube and its
/// output is interpolated from the corners, which is enough to express a colour grade, a film emulation,
/// a channel swap, a gamma change or a false-colour mapping - anything, in short, that depends only on the
/// colour of the pixel and not on its neighbours.
/// </para>
/// <para>
/// <b>Value order.</b> <see cref="Values" /> holds <c>Size * Size * Size</c> triplets with RED changing
/// fastest and BLUE slowest - the order the ".cube" file format uses, so a parsed file's numbers go straight
/// in. The triplet for grid point <c>(r, g, b)</c> starts at <c>((b * Size + g) * Size + r) * 3</c>.
/// </para>
/// <para>
/// <b>Domain.</b> <see cref="DomainMinimum" /> and <see cref="DomainMaximum" /> say what range of input the
/// cube covers, one pair per channel; the default is 0 to 1. Sampling normalises the input through the
/// domain first, so a table declaring 0 to 4 that is handed an ordinary 0-to-1 picture uses the bottom
/// quarter of its cube, which is exactly what such a table means.
/// </para>
/// <para>Instances are immutable and safe to share between threads.</para>
/// </remarks>
public sealed class Lut3D
{
    /// <summary>The smallest grid this type accepts.</summary>
    public const int MinimumSize = 2;

    /// <summary>The largest grid this type accepts - 129 nodes a side is 2,146,689 triplets.</summary>
    public const int MaximumSize = 129;

    private readonly float[] values;
    private readonly float[] domainMinimum;
    private readonly float[] domainMaximum;
    private readonly bool defaultDomain;

    /// <summary>Creates a table from its grid values.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <param name="values">
    /// <c>size * size * size * 3</c> numbers, red fastest, each normally between 0 and 1. The array is copied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="values" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is below <see cref="MinimumSize" /> or above <see cref="MaximumSize" />.
    /// </exception>
    /// <exception cref="ArgumentException">The array is not exactly <c>size * size * size * 3</c> long.</exception>
    public Lut3D(int size, float[] values)
        : this(size, values, null, null)
    {
    }

    /// <summary>Creates a table from its grid values over a stated input domain.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <param name="values">
    /// <c>size * size * size * 3</c> numbers, red fastest, each normally between 0 and 1. The array is copied.
    /// </param>
    /// <param name="domainMinimum">
    /// The input value the first node of each axis stands for - three numbers, or null for 0, 0, 0. The array
    /// is copied.
    /// </param>
    /// <param name="domainMaximum">
    /// The input value the last node of each axis stands for - three numbers, or null for 1, 1, 1. The array
    /// is copied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="values" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is below <see cref="MinimumSize" /> or above <see cref="MaximumSize" />.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The array is not exactly <c>size * size * size * 3</c> long, a domain array does not hold three
    /// numbers, a domain bound is not a finite number, or a channel's maximum is not above its minimum.
    /// </exception>
    public Lut3D(int size, float[] values, float[] domainMinimum, float[] domainMaximum)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        if (size < MinimumSize || size > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"A three-dimensional lookup table has between {MinimumSize} and {MaximumSize} nodes a side.");
        }

        int expected = size * size * size * 3;
        if (values.Length != expected)
        {
            throw new ArgumentException(
                $"A {size}-node table needs exactly {expected} numbers ({size}x{size}x{size} triplets, red "
                + $"changing fastest); the array given holds {values.Length}.",
                nameof(values));
        }

        Size = size;
        this.values = (float[])values.Clone();

        this.domainMinimum = LutDomain.Validate(domainMinimum, 0f, nameof(domainMinimum));
        this.domainMaximum = LutDomain.Validate(domainMaximum, 1f, nameof(domainMaximum));
        LutDomain.EnsureOrdered(this.domainMinimum, this.domainMaximum, nameof(domainMaximum));
        defaultDomain = LutDomain.IsDefault(this.domainMinimum, this.domainMaximum);
    }

    /// <summary>The number of nodes along each axis.</summary>
    public int Size { get; }

    /// <summary>
    /// The grid values, red changing fastest: the triplet for <c>(r, g, b)</c> starts at
    /// <c>((b * Size + g) * Size + r) * 3</c>.
    /// </summary>
    public ReadOnlySpan<float> Values => values;

    /// <summary>The input value the first node of each axis stands for - red, green, blue.</summary>
    public ReadOnlySpan<float> DomainMinimum => domainMinimum;

    /// <summary>The input value the last node of each axis stands for - red, green, blue.</summary>
    public ReadOnlySpan<float> DomainMaximum => domainMaximum;

    /// <summary>True when the domain is the usual 0 to 1 on all three channels.</summary>
    public bool HasDefaultDomain => defaultDomain;

    /// <summary>Builds the table that changes nothing, at the requested grid size.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <returns>A table whose output equals its input at every node.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is below <see cref="MinimumSize" /> or above <see cref="MaximumSize" />.
    /// </exception>
    public static Lut3D CreateIdentity(int size)
    {
        if (size < MinimumSize || size > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"A three-dimensional lookup table has between {MinimumSize} and {MaximumSize} nodes a side.");
        }

        float[] grid = new float[size * size * size * 3];
        LutComposer.FillIdentity(grid, size);
        return new Lut3D(size, grid);
    }

    /// <summary>Looks a colour up, blending all eight corners of the cell it falls in.</summary>
    /// <param name="red">The red component. Values outside the domain are clamped to it.</param>
    /// <param name="green">The green component. Values outside the domain are clamped to it.</param>
    /// <param name="blue">The blue component. Values outside the domain are clamped to it.</param>
    /// <param name="outputRed">The transformed red component.</param>
    /// <param name="outputGreen">The transformed green component.</param>
    /// <param name="outputBlue">The transformed blue component.</param>
    /// <remarks>
    /// This is <see cref="LutInterpolation.Trilinear" />, which is what a graphics card's texture filter
    /// does and therefore what the shader path produces. Ask for
    /// <see cref="LutInterpolation.Tetrahedral" /> through the other overload to match what colour-grading
    /// tools and FFmpeg do.
    /// </remarks>
    public void Sample(
        float red,
        float green,
        float blue,
        out float outputRed,
        out float outputGreen,
        out float outputBlue) =>
        Sample(red, green, blue, LutInterpolation.Trilinear, out outputRed, out outputGreen, out outputBlue);

    /// <summary>Looks a colour up by the requested method.</summary>
    /// <param name="red">The red component. Values outside the domain are clamped to it.</param>
    /// <param name="green">The green component. Values outside the domain are clamped to it.</param>
    /// <param name="blue">The blue component. Values outside the domain are clamped to it.</param>
    /// <param name="interpolation">How to work out a colour that falls between nodes.</param>
    /// <param name="outputRed">The transformed red component.</param>
    /// <param name="outputGreen">The transformed green component.</param>
    /// <param name="outputBlue">The transformed blue component.</param>
    public void Sample(
        float red,
        float green,
        float blue,
        LutInterpolation interpolation,
        out float outputRed,
        out float outputGreen,
        out float outputBlue)
    {
        float last = Size - 1;
        float fr = Normalise(red, 0) * last;
        float fg = Normalise(green, 1) * last;
        float fb = Normalise(blue, 2) * last;

        int r0 = (int)fr;
        int g0 = (int)fg;
        int b0 = (int)fb;
        if (r0 > Size - 2) r0 = Size - 2;
        if (g0 > Size - 2) g0 = Size - 2;
        if (b0 > Size - 2) b0 = Size - 2;

        float tr = fr - r0;
        float tg = fg - g0;
        float tb = fb - b0;

        int stride = Size * 3;
        int slice = Size * stride;
        int origin = (b0 * slice) + (g0 * stride) + (r0 * 3);

        if (interpolation == LutInterpolation.Trilinear)
        {
            InterpolateTrilinear(
                values,
                origin,
                stride,
                slice,
                tr,
                tg,
                tb,
                out outputRed,
                out outputGreen,
                out outputBlue);

            return;
        }

        InterpolateTetrahedral(
            values,
            origin,
            stride,
            slice,
            tr,
            tg,
            tb,
            out outputRed,
            out outputGreen,
            out outputBlue);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"3-D lookup table, {Size}x{Size}x{Size} nodes"
        + (defaultDomain ? string.Empty : LutDomain.Describe(domainMinimum, domainMaximum));

    /// <summary>Blends all eight corners of one cell, weighted by distance along each axis.</summary>
    /// <param name="grid">The grid values, red changing fastest.</param>
    /// <param name="origin">The index of the cell's (0, 0, 0) corner's red value.</param>
    /// <param name="stride">The distance in numbers from one green index to the next.</param>
    /// <param name="slice">The distance in numbers from one blue index to the next.</param>
    /// <param name="tr">How far into the cell the colour lies along red, 0 to 1.</param>
    /// <param name="tg">How far into the cell the colour lies along green, 0 to 1.</param>
    /// <param name="tb">How far into the cell the colour lies along blue, 0 to 1.</param>
    /// <param name="red">The blended red value.</param>
    /// <param name="green">The blended green value.</param>
    /// <param name="blue">The blended blue value.</param>
    internal static void InterpolateTrilinear(
        ReadOnlySpan<float> grid,
        int origin,
        int stride,
        int slice,
        float tr,
        float tg,
        float tb,
        out float red,
        out float green,
        out float blue)
    {
        float w0 = (1f - tr) * (1f - tg) * (1f - tb);
        float w1 = tr * (1f - tg) * (1f - tb);
        float w2 = (1f - tr) * tg * (1f - tb);
        float w3 = tr * tg * (1f - tb);
        float w4 = (1f - tr) * (1f - tg) * tb;
        float w5 = tr * (1f - tg) * tb;
        float w6 = (1f - tr) * tg * tb;
        float w7 = tr * tg * tb;

        int c000 = origin;
        int c100 = origin + 3;
        int c010 = origin + stride;
        int c110 = c010 + 3;
        int c001 = origin + slice;
        int c101 = c001 + 3;
        int c011 = c001 + stride;
        int c111 = c011 + 3;

        red =
            (grid[c000] * w0) + (grid[c100] * w1) + (grid[c010] * w2) + (grid[c110] * w3)
            + (grid[c001] * w4) + (grid[c101] * w5) + (grid[c011] * w6) + (grid[c111] * w7);

        green =
            (grid[c000 + 1] * w0) + (grid[c100 + 1] * w1) + (grid[c010 + 1] * w2) + (grid[c110 + 1] * w3)
            + (grid[c001 + 1] * w4) + (grid[c101 + 1] * w5) + (grid[c011 + 1] * w6) + (grid[c111 + 1] * w7);

        blue =
            (grid[c000 + 2] * w0) + (grid[c100 + 2] * w1) + (grid[c010 + 2] * w2) + (grid[c110 + 2] * w3)
            + (grid[c001 + 2] * w4) + (grid[c101 + 2] * w5) + (grid[c011 + 2] * w6) + (grid[c111 + 2] * w7);
    }

    /// <summary>
    /// Interpolates inside one of the six tetrahedra the cell splits into around its black-to-white diagonal.
    /// </summary>
    /// <param name="grid">The grid values, red changing fastest.</param>
    /// <param name="origin">The index of the cell's (0, 0, 0) corner's red value.</param>
    /// <param name="stride">The distance in numbers from one green index to the next.</param>
    /// <param name="slice">The distance in numbers from one blue index to the next.</param>
    /// <param name="tr">How far into the cell the colour lies along red, 0 to 1.</param>
    /// <param name="tg">How far into the cell the colour lies along green, 0 to 1.</param>
    /// <param name="tb">How far into the cell the colour lies along blue, 0 to 1.</param>
    /// <param name="red">The interpolated red value.</param>
    /// <param name="green">The interpolated green value.</param>
    /// <param name="blue">The interpolated blue value.</param>
    /// <remarks>
    /// The corner the colour is furthest from and the corner it is nearest are always the cell's black and
    /// white corners; which of the six wedges between them the colour is in is decided by the ORDER of the
    /// three fractions, and the answer is the black corner plus three steps taken in that order. Every
    /// weight is non-negative and they sum to one, so a colour on the neutral axis of an identity cell comes
    /// back exactly where it went in.
    /// </remarks>
    internal static void InterpolateTetrahedral(
        ReadOnlySpan<float> grid,
        int origin,
        int stride,
        int slice,
        float tr,
        float tg,
        float tb,
        out float red,
        out float green,
        out float blue)
    {
        int c000 = origin;
        int c111 = origin + slice + stride + 3;

        int first;
        int second;
        float firstWeight;
        float secondWeight;
        float thirdWeight;

        if (tr > tg)
        {
            if (tg > tb)
            {
                // red, then green, then blue
                first = origin + 3;
                second = origin + stride + 3;
                firstWeight = tr;
                secondWeight = tg;
                thirdWeight = tb;
            }
            else if (tr > tb)
            {
                // red, then blue, then green
                first = origin + 3;
                second = origin + slice + 3;
                firstWeight = tr;
                secondWeight = tb;
                thirdWeight = tg;
            }
            else
            {
                // blue, then red, then green
                first = origin + slice;
                second = origin + slice + 3;
                firstWeight = tb;
                secondWeight = tr;
                thirdWeight = tg;
            }
        }
        else
        {
            if (tb > tg)
            {
                // blue, then green, then red
                first = origin + slice;
                second = origin + slice + stride;
                firstWeight = tb;
                secondWeight = tg;
                thirdWeight = tr;
            }
            else if (tb > tr)
            {
                // green, then blue, then red
                first = origin + stride;
                second = origin + slice + stride;
                firstWeight = tg;
                secondWeight = tb;
                thirdWeight = tr;
            }
            else
            {
                // green, then red, then blue
                first = origin + stride;
                second = origin + stride + 3;
                firstWeight = tg;
                secondWeight = tr;
                thirdWeight = tb;
            }
        }

        red =
            grid[c000]
            + (firstWeight * (grid[first] - grid[c000]))
            + (secondWeight * (grid[second] - grid[first]))
            + (thirdWeight * (grid[c111] - grid[second]));

        green =
            grid[c000 + 1]
            + (firstWeight * (grid[first + 1] - grid[c000 + 1]))
            + (secondWeight * (grid[second + 1] - grid[first + 1]))
            + (thirdWeight * (grid[c111 + 1] - grid[second + 1]));

        blue =
            grid[c000 + 2]
            + (firstWeight * (grid[first + 2] - grid[c000 + 2]))
            + (secondWeight * (grid[second + 2] - grid[first + 2]))
            + (thirdWeight * (grid[c111 + 2] - grid[second + 2]));
    }

    private float Normalise(float value, int channel) =>
        defaultDomain
            ? LutDomain.Clamp01(value)
            : LutDomain.Normalise(value, domainMinimum[channel], domainMaximum[channel]);
}
