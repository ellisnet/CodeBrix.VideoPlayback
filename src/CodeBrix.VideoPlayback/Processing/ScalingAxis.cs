using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// The precomputed weight table for ONE axis of a scale: for every output position, a fixed number of source
/// indices (already clamped to the edge) and their integer weights, which sum to exactly
/// <c>1 &lt;&lt; WeightBits</c>.
/// </summary>
/// <remarks>
/// <para>
/// The geometry is centre-aligned - output sample <c>o</c> sits at source position
/// <c>(o + 0.5) * src / dst - 0.5</c> - which is what FFmpeg's scaler does for luma and for chroma whose
/// siting is unspecified. Taps that fall off the edge are clamped onto the edge sample, which is the same as
/// FFmpeg folding them in.
/// </para>
/// <para>
/// Weights are quantised to fourteen bits with the rounding error carried from tap to tap, so the
/// quantised set still sums exactly to one; a residue that floating point leaves over goes onto the largest
/// tap. An axis whose size does not change is a single tap of weight one, which makes it exact.
/// </para>
/// </remarks>
internal sealed class ScalingAxis
{
    /// <summary>The number of fractional bits in a weight.</summary>
    public const int WeightBits = 14;

    /// <summary>One, as a weight.</summary>
    public const int WeightOne = 1 << WeightBits;

    private ScalingAxis(int taps, int[] indices, int[] weights, bool isIdentity)
    {
        Taps = taps;
        Indices = indices;
        Weights = weights;
        IsIdentity = isIdentity;
    }

    /// <summary>The number of taps per output sample.</summary>
    public int Taps { get; }

    /// <summary>Source indices, <see cref="Taps" /> per output sample, each within the source.</summary>
    public int[] Indices { get; }

    /// <summary>Weights, <see cref="Taps" /> per output sample, summing to <see cref="WeightOne" />.</summary>
    public int[] Weights { get; }

    /// <summary>True when the axis does not change size: one tap, weight one, output index = input index.</summary>
    public bool IsIdentity { get; }

    public static ScalingAxis Build(int sourceSize, int destinationSize, ScalingFilter filter)
    {
        if (sourceSize == destinationSize)
        {
            int[] identityIndices = new int[destinationSize];
            int[] identityWeights = new int[destinationSize];
            for (int o = 0; o < destinationSize; o++)
            {
                identityIndices[o] = o;
                identityWeights[o] = WeightOne;
            }

            return new ScalingAxis(1, identityIndices, identityWeights, true);
        }

        double scale = sourceSize / (double)destinationSize;
        double stretch = Math.Max(scale, 1d);
        double lobes = filter == ScalingFilter.Lanczos3 ? 3d : 1d;
        double radius = lobes * stretch;
        int taps = (int)Math.Ceiling(2d * radius) + 1;

        int[] indices = new int[destinationSize * taps];
        int[] weights = new int[destinationSize * taps];
        double[] raw = new double[taps];

        for (int o = 0; o < destinationSize; o++)
        {
            double centre = ((o + 0.5d) * scale) - 0.5d;
            int first = (int)Math.Floor(centre - radius) + 1;

            double sum = 0d;
            for (int t = 0; t < taps; t++)
            {
                double distance = (first + t - centre) / stretch;
                double weight = Kernel(distance, filter);
                raw[t] = weight;
                sum += weight;
            }

            int at = o * taps;
            if (sum == 0d)
            {
                // Cannot happen for these kernels, but a table must never divide by zero: nearest sample.
                Array.Clear(raw);
                raw[Math.Clamp((int)Math.Round(centre) - first, 0, taps - 1)] = 1d;
                sum = 1d;
            }

            double carried = 0d;
            int total = 0;
            int largest = 0;
            for (int t = 0; t < taps; t++)
            {
                double exact = (raw[t] / sum * WeightOne) + carried;
                int quantised = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
                carried = exact - quantised;
                weights[at + t] = quantised;
                total += quantised;
                if (Math.Abs(quantised) > Math.Abs(weights[at + largest])) largest = t;

                indices[at + t] = Math.Clamp(first + t, 0, sourceSize - 1);
            }

            weights[at + largest] += WeightOne - total;
        }

        return new ScalingAxis(taps, indices, weights, false);
    }

    private static double Kernel(double x, ScalingFilter filter)
    {
        double distance = Math.Abs(x);

        if (filter == ScalingFilter.Bilinear)
        {
            return distance < 1d ? 1d - distance : 0d;
        }

        if (distance < 1e-12) return 1d;
        if (distance >= 3d) return 0d;

        double pix = Math.PI * distance;
        return 3d * Math.Sin(pix) * Math.Sin(pix / 3d) / (pix * pix);
    }
}
