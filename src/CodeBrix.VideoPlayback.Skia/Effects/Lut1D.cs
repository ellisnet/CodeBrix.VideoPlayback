using System;

namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// A one-dimensional colour lookup table: three independent curves, one per channel.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape of a levels adjustment, a gamma change, a contrast curve or a per-channel colour
/// balance - anything where red depends only on red. It is a special case of <see cref="Lut3D" /> and is
/// composed into the same resultant table, so mixing the two in one chain costs nothing extra.
/// </para>
/// <para>Instances are immutable and safe to share between threads.</para>
/// </remarks>
public sealed class Lut1D
{
    /// <summary>The smallest curve this type accepts.</summary>
    public const int MinimumSize = 2;

    /// <summary>The largest curve this type accepts.</summary>
    public const int MaximumSize = 65536;

    private readonly float[] red;
    private readonly float[] green;
    private readonly float[] blue;

    /// <summary>Creates a table from one curve applied to all three channels.</summary>
    /// <param name="curve">The curve, normally running from 0 to 1. The array is copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve" /> is null.</exception>
    /// <exception cref="ArgumentException">The curve has fewer than two points, or more than the maximum.</exception>
    public Lut1D(float[] curve)
        : this(curve, curve, curve)
    {
    }

    /// <summary>Creates a table from one curve per channel.</summary>
    /// <param name="red">The red curve. The array is copied.</param>
    /// <param name="green">The green curve, which must be the same length as the red one. The array is copied.</param>
    /// <param name="blue">The blue curve, which must be the same length as the red one. The array is copied.</param>
    /// <exception cref="ArgumentNullException">A curve is null.</exception>
    /// <exception cref="ArgumentException">
    /// A curve has fewer than two points or more than the maximum, or the three curves are not the same
    /// length.
    /// </exception>
    public Lut1D(float[] red, float[] green, float[] blue)
    {
        if (red == null) throw new ArgumentNullException(nameof(red));
        if (green == null) throw new ArgumentNullException(nameof(green));
        if (blue == null) throw new ArgumentNullException(nameof(blue));

        if (red.Length < MinimumSize || red.Length > MaximumSize)
        {
            throw new ArgumentException(
                $"A one-dimensional lookup curve has between {MinimumSize} and {MaximumSize} points; this one "
                + $"has {red.Length}.",
                nameof(red));
        }

        if (green.Length != red.Length || blue.Length != red.Length)
        {
            throw new ArgumentException(
                $"The three curves must be the same length; they are {red.Length}, {green.Length} and "
                + $"{blue.Length} points long.",
                nameof(green));
        }

        this.red = (float[])red.Clone();
        this.green = (float[])green.Clone();
        this.blue = (float[])blue.Clone();
        Size = red.Length;
    }

    /// <summary>The number of points in each curve.</summary>
    public int Size { get; }

    /// <summary>The red curve.</summary>
    public ReadOnlySpan<float> Red => red;

    /// <summary>The green curve.</summary>
    public ReadOnlySpan<float> Green => green;

    /// <summary>The blue curve.</summary>
    public ReadOnlySpan<float> Blue => blue;

    /// <summary>True when the three channels share one curve, point for point.</summary>
    public bool IsMonochrome
    {
        get
        {
            for (int i = 0; i < Size; i++)
            {
                if (red[i] != green[i] || red[i] != blue[i]) return false;
            }

            return true;
        }
    }

    /// <summary>Builds the curve set that changes nothing, at the requested length.</summary>
    /// <param name="size">The number of points in each curve.</param>
    /// <returns>Three curves whose output equals their input at every point.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside the accepted range.</exception>
    public static Lut1D CreateIdentity(int size)
    {
        if (size < MinimumSize || size > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"A one-dimensional lookup curve has between {MinimumSize} and {MaximumSize} points.");
        }

        float[] curve = new float[size];
        for (int i = 0; i < size; i++) curve[i] = i / (float)(size - 1);
        return new Lut1D(curve);
    }

    /// <summary>Looks a colour up, interpolating along each curve independently.</summary>
    /// <param name="redInput">The red component, 0 to 1. Values outside are clamped.</param>
    /// <param name="greenInput">The green component, 0 to 1. Values outside are clamped.</param>
    /// <param name="blueInput">The blue component, 0 to 1. Values outside are clamped.</param>
    /// <param name="outputRed">The transformed red component.</param>
    /// <param name="outputGreen">The transformed green component.</param>
    /// <param name="outputBlue">The transformed blue component.</param>
    public void Sample(
        float redInput,
        float greenInput,
        float blueInput,
        out float outputRed,
        out float outputGreen,
        out float outputBlue)
    {
        outputRed = Interpolate(red, redInput);
        outputGreen = Interpolate(green, greenInput);
        outputBlue = Interpolate(blue, blueInput);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"1-D lookup table, {Size} points" + (IsMonochrome ? " (one curve for all three channels)" : string.Empty);

    private float Interpolate(float[] curve, float value)
    {
        float last = Size - 1;
        float position = (value < 0f ? 0f : (value > 1f ? 1f : value)) * last;

        int lower = (int)position;
        if (lower >= Size - 1) return curve[Size - 1];

        float fraction = position - lower;
        return curve[lower] + ((curve[lower + 1] - curve[lower]) * fraction);
    }
}
