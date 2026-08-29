using System;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// A one-dimensional colour lookup table: three independent curves, one per channel.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape of a levels adjustment, a gamma change, a contrast curve or a per-channel colour
/// balance - anything where red depends only on red. It is a special case of <see cref="Lut3D" /> and is
/// composed into the same resultant table, so mixing the two in one chain costs nothing extra.
/// </para>
/// <para>
/// <b>Domain.</b> <see cref="DomainMinimum" /> and <see cref="DomainMaximum" /> say what range of input the
/// curves cover, one pair per channel; the default is 0 to 1. Sampling normalises the input through the
/// domain first, so a table declaring 0 to 4 that is handed an ordinary 0-to-1 picture uses the bottom
/// quarter of its curves, which is exactly what such a table means.
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
    private readonly float[] domainMinimum;
    private readonly float[] domainMaximum;
    private readonly bool defaultDomain;

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
        : this(red, green, blue, null, null)
    {
    }

    /// <summary>Creates a table from one curve per channel over a stated input domain.</summary>
    /// <param name="red">The red curve. The array is copied.</param>
    /// <param name="green">The green curve, which must be the same length as the red one. The array is copied.</param>
    /// <param name="blue">The blue curve, which must be the same length as the red one. The array is copied.</param>
    /// <param name="domainMinimum">
    /// The input value each channel's first point stands for - three numbers, or null for 0, 0, 0. The array
    /// is copied.
    /// </param>
    /// <param name="domainMaximum">
    /// The input value each channel's last point stands for - three numbers, or null for 1, 1, 1. The array
    /// is copied.
    /// </param>
    /// <exception cref="ArgumentNullException">A curve is null.</exception>
    /// <exception cref="ArgumentException">
    /// A curve has fewer than two points or more than the maximum, the three curves are not the same length,
    /// a domain array does not hold three numbers, a domain bound is not a finite number, or a channel's
    /// maximum is not above its minimum.
    /// </exception>
    public Lut1D(float[] red, float[] green, float[] blue, float[] domainMinimum, float[] domainMaximum)
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

        this.domainMinimum = LutDomain.Validate(domainMinimum, 0f, nameof(domainMinimum));
        this.domainMaximum = LutDomain.Validate(domainMaximum, 1f, nameof(domainMaximum));
        LutDomain.EnsureOrdered(this.domainMinimum, this.domainMaximum, nameof(domainMaximum));
        defaultDomain = LutDomain.IsDefault(this.domainMinimum, this.domainMaximum);
    }

    /// <summary>The number of points in each curve.</summary>
    public int Size { get; }

    /// <summary>The red curve.</summary>
    public ReadOnlySpan<float> Red => red;

    /// <summary>The green curve.</summary>
    public ReadOnlySpan<float> Green => green;

    /// <summary>The blue curve.</summary>
    public ReadOnlySpan<float> Blue => blue;

    /// <summary>The input value each channel's first point stands for - red, green, blue.</summary>
    public ReadOnlySpan<float> DomainMinimum => domainMinimum;

    /// <summary>The input value each channel's last point stands for - red, green, blue.</summary>
    public ReadOnlySpan<float> DomainMaximum => domainMaximum;

    /// <summary>True when the domain is the usual 0 to 1 on all three channels.</summary>
    public bool HasDefaultDomain => defaultDomain;

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
    /// <param name="redInput">The red component. Values outside the domain are clamped to it.</param>
    /// <param name="greenInput">The green component. Values outside the domain are clamped to it.</param>
    /// <param name="blueInput">The blue component. Values outside the domain are clamped to it.</param>
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
        outputRed = Interpolate(red, Normalise(redInput, 0));
        outputGreen = Interpolate(green, Normalise(greenInput, 1));
        outputBlue = Interpolate(blue, Normalise(blueInput, 2));
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"1-D lookup table, {Size} points"
        + (IsMonochrome ? " (one curve for all three channels)" : string.Empty)
        + (defaultDomain ? string.Empty : LutDomain.Describe(domainMinimum, domainMaximum));

    private float Normalise(float value, int channel) =>
        defaultDomain
            ? LutDomain.Clamp01(value)
            : LutDomain.Normalise(value, domainMinimum[channel], domainMaximum[channel]);

    private float Interpolate(float[] curve, float normalised)
    {
        float position = normalised * (Size - 1);

        int lower = (int)position;
        if (lower >= Size - 1) return curve[Size - 1];

        float fraction = position - lower;
        return curve[lower] + ((curve[lower + 1] - curve[lower]) * fraction);
    }
}
