using System;

namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// A three-dimensional colour lookup table: a cube of sample points, each saying what one input colour
/// becomes.
/// </summary>
/// <remarks>
/// <para>
/// This is the general form of a colour change. Every input colour lands inside one cell of the cube and its
/// output is interpolated from the eight corners, which is enough to express a colour grade, a film emulation,
/// a channel swap, a gamma change or a false-colour mapping - anything, in short, that depends only on the
/// colour of the pixel and not on its neighbours.
/// </para>
/// <para>
/// <b>Value order.</b> <see cref="Values" /> holds <c>Size * Size * Size</c> triplets with RED changing
/// fastest and BLUE slowest - the order the ".cube" file format uses, so a parsed file's numbers go straight
/// in. The triplet for grid point <c>(r, g, b)</c> starts at <c>((b * Size + g) * Size + r) * 3</c>.
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
    }

    /// <summary>The number of nodes along each axis.</summary>
    public int Size { get; }

    /// <summary>
    /// The grid values, red changing fastest: the triplet for <c>(r, g, b)</c> starts at
    /// <c>((b * Size + g) * Size + r) * 3</c>.
    /// </summary>
    public ReadOnlySpan<float> Values => values;

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
        float last = size - 1;
        int index = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    grid[index++] = r / last;
                    grid[index++] = g / last;
                    grid[index++] = b / last;
                }
            }
        }

        return new Lut3D(size, grid);
    }

    /// <summary>Looks a colour up, interpolating between the eight corners of the cell it falls in.</summary>
    /// <param name="red">The red component, 0 to 1. Values outside are clamped.</param>
    /// <param name="green">The green component, 0 to 1. Values outside are clamped.</param>
    /// <param name="blue">The blue component, 0 to 1. Values outside are clamped.</param>
    /// <param name="outputRed">The transformed red component.</param>
    /// <param name="outputGreen">The transformed green component.</param>
    /// <param name="outputBlue">The transformed blue component.</param>
    public void Sample(
        float red,
        float green,
        float blue,
        out float outputRed,
        out float outputGreen,
        out float outputBlue)
    {
        float last = Size - 1;
        float fr = Clamp01(red) * last;
        float fg = Clamp01(green) * last;
        float fb = Clamp01(blue) * last;

        int r0 = (int)fr;
        int g0 = (int)fg;
        int b0 = (int)fb;
        if (r0 >= Size - 1) r0 = Size - 2;
        if (g0 >= Size - 1) g0 = Size - 2;
        if (b0 >= Size - 1) b0 = Size - 2;

        float tr = fr - r0;
        float tg = fg - g0;
        float tb = fb - b0;

        outputRed = 0f;
        outputGreen = 0f;
        outputBlue = 0f;

        for (int corner = 0; corner < 8; corner++)
        {
            int dr = corner & 1;
            int dg = (corner >> 1) & 1;
            int db = (corner >> 2) & 1;

            float weight =
                (dr == 0 ? 1f - tr : tr)
                * (dg == 0 ? 1f - tg : tg)
                * (db == 0 ? 1f - tb : tb);

            if (weight == 0f) continue;

            int offset = (((b0 + db) * Size + (g0 + dg)) * Size + (r0 + dr)) * 3;
            outputRed += values[offset] * weight;
            outputGreen += values[offset + 1] * weight;
            outputBlue += values[offset + 2] * weight;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"3-D lookup table, {Size}x{Size}x{Size} nodes";

    private static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
}
