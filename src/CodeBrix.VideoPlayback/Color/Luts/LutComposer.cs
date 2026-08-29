using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// Folds an ordered chain of lookup tables, each with its own apply-at percentage, into ONE effective table.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole colour-lookup engine, and it is the only place in this program where the composition
/// arithmetic lives. The playback side hands the effective table to the presenter's shader; the authoring
/// side writes it to a ".cube" file with <see cref="CubeLutFile" /> and hands the PATH to FFmpeg's
/// <c>lut3d</c> filter. Both stages therefore apply exactly the same numbers.
/// </para>
/// <para>
/// <b>How a chain is folded.</b> The walk is over the nodes of the OUTPUT table. Each node starts as the
/// colour it stands for - the identity - and every layer in turn is asked what it would make of the colour
/// SO FAR, sampling that layer at its own size and over its own domain, never at the output's. The layer's
/// answer is then mixed in by its percentage:
/// </para>
/// <code>
/// colour = colour + ((layer.Sample(colour) - colour) * (percent / 100))
/// </code>
/// <para>
/// so 100 replaces the colour, 50 lands half way between what it was and what the layer says, and 0 leaves
/// it untouched - a layer at 0 is skipped altogether. Because layer two sees what layer one produced, the
/// ORDER of the chain is meaningful, and swapping two layers is a different table.
/// </para>
/// <para>
/// <b>The size of the effective table</b> is the largest size any applied layer has, floored at
/// <see cref="DefaultMinimumOutputSize" /> and capped at <see cref="DefaultMaximumOutputSize" />: a chain of
/// 17-node tables composes at 33, a chain containing a 65-node table composes at 65, and a 129-node table
/// still composes at 65 because a bigger cube costs memory on the graphics card for detail no chain of
/// smooth tables carries. <see cref="LutComposerOptions.OutputSize" /> overrides the rule outright.
/// </para>
/// <para>
/// <b>The domain of the effective table</b> is 0 to 1 unless the caller says otherwise, because that is
/// what both render paths and FFmpeg's raw video feed it. Each LAYER's own domain is honoured where it
/// belongs - on the way in to that layer's own lookup - so a layer declaring 0 to 4 that is handed an
/// ordinary picture uses the bottom quarter of its table, which is what such a table means. Spending the
/// output's nodes on input values that cannot occur would only throw away resolution where it matters;
/// a caller that really does bake for a wider input can read the chain's own domain from
/// <see cref="TryGetChainDomain" /> and set it through <see cref="LutComposerOptions" />.
/// </para>
/// <para>
/// <b>A combined ".cube" file</b> - a shaper curve set and a table in one file - is ONE layer, not two: its
/// percentage mixes in what the PAIR makes of a colour, because the two halves are one artistic step.
/// </para>
/// <para>
/// <b>A chain of curves only</b> still composes to a <see cref="Lut3D" />, because that is what the shader
/// samples and what FFmpeg's <c>lut3d</c> filter reads - its <c>lut1d</c> filter is a different filter with
/// a different file. <see cref="TryComposeCurves" /> is there for a caller that wants the exact per-channel
/// answer instead.
/// </para>
/// </remarks>
public static class LutComposer
{
    /// <summary>The smallest table the automatic size rule will produce - the ".cube" convention.</summary>
    public const int DefaultMinimumOutputSize = 33;

    /// <summary>The largest table the automatic size rule will produce.</summary>
    public const int DefaultMaximumOutputSize = 65;

    /// <summary>The smallest curve set <see cref="TryComposeCurves" /> will produce.</summary>
    public const int DefaultMinimumCurveSize = 1024;

    /// <summary>The largest curve set <see cref="TryComposeCurves" /> will produce.</summary>
    public const int DefaultMaximumCurveSize = 4096;

    /// <summary>Folds a chain into one effective table, with every default.</summary>
    /// <param name="layers">The chain, in order. Null entries are ignored.</param>
    /// <returns>The one table the whole chain reduces to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers" /> is null.</exception>
    public static Lut3D Compose(IReadOnlyList<LutLayer> layers) => Compose(layers, null);

    /// <summary>Folds a chain into one effective table.</summary>
    /// <param name="layers">The chain, in order. Null entries are ignored.</param>
    /// <param name="options">The choices to make, or null for every default.</param>
    /// <returns>The one table the whole chain reduces to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="LutComposerOptions.OutputSize" /> is outside what a <see cref="Lut3D" /> can be.
    /// </exception>
    /// <exception cref="ArgumentException">A stated output domain is not three finite rising numbers.</exception>
    public static Lut3D Compose(IReadOnlyList<LutLayer> layers, LutComposerOptions options)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));

        LutInterpolation interpolation =
            options != null ? options.Interpolation : LutInterpolation.Tetrahedral;

        int size = options != null && options.OutputSize > 0 ? options.OutputSize : GetOutputSize(layers);
        if (size < Lut3D.MinimumSize || size > Lut3D.MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                "options.OutputSize",
                size,
                $"A three-dimensional lookup table has between {Lut3D.MinimumSize} and {Lut3D.MaximumSize} "
                + "nodes a side.");
        }

        float[] minimum = LutDomain.Validate(
            options != null ? options.OutputDomainMinimum : null,
            0f,
            "options.OutputDomainMinimum");

        float[] maximum = LutDomain.Validate(
            options != null ? options.OutputDomainMaximum : null,
            1f,
            "options.OutputDomainMaximum");

        LutDomain.EnsureOrdered(minimum, maximum, "options.OutputDomainMaximum");

        float[] lattice = new float[size * size * size * 3];
        FillIdentity(lattice, size, minimum, maximum);

        for (int index = 0; index < layers.Count; index++)
        {
            ApplyLayer(lattice, layers[index], interpolation);
        }

        return new Lut3D(size, lattice, minimum, maximum);
    }

    /// <summary>Folds a chain of per-channel curves into one set of per-channel curves.</summary>
    /// <param name="layers">The chain, in order. Null entries are ignored.</param>
    /// <param name="options">The choices to make, or null for every default.</param>
    /// <param name="curves">The one curve set the chain reduces to, or null when it could not be one.</param>
    /// <returns>
    /// True when every applied layer was a curve set, so the chain is exactly per-channel and this is the
    /// answer with no interpolation error at all. False when a three-dimensional layer is in the chain, in
    /// which case <see cref="Compose(IReadOnlyList{LutLayer}, LutComposerOptions)" /> is the only answer.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers" /> is null.</exception>
    /// <remarks>
    /// Red output depends only on red input in every layer, so it does in the chain too, and a curve set
    /// says so exactly. The size is the largest of the applied layers', floored at
    /// <see cref="DefaultMinimumCurveSize" /> and capped at <see cref="DefaultMaximumCurveSize" />, or
    /// <see cref="LutComposerOptions.OutputSize" /> when that is set.
    /// </remarks>
    public static bool TryComposeCurves(
        IReadOnlyList<LutLayer> layers,
        LutComposerOptions options,
        out Lut1D curves)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));

        curves = null;

        int largest = 0;
        for (int index = 0; index < layers.Count; index++)
        {
            LutLayer layer = layers[index];
            if (layer == null || !layer.HasEffect) continue;
            if (layer.IsThreeDimensional) return false;
            if (layer.Size > largest) largest = layer.Size;
        }

        int size = options != null && options.OutputSize > 0
            ? options.OutputSize
            : Clamp(Math.Max(largest, DefaultMinimumCurveSize), DefaultMinimumCurveSize, DefaultMaximumCurveSize);

        if (size < Lut1D.MinimumSize || size > Lut1D.MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                "options.OutputSize",
                size,
                $"A one-dimensional lookup curve has between {Lut1D.MinimumSize} and {Lut1D.MaximumSize} "
                + "points.");
        }

        float[] minimum = LutDomain.Validate(
            options != null ? options.OutputDomainMinimum : null,
            0f,
            "options.OutputDomainMinimum");

        float[] maximum = LutDomain.Validate(
            options != null ? options.OutputDomainMaximum : null,
            1f,
            "options.OutputDomainMaximum");

        LutDomain.EnsureOrdered(minimum, maximum, "options.OutputDomainMaximum");

        float[] red = new float[size];
        float[] green = new float[size];
        float[] blue = new float[size];
        float last = size - 1;

        for (int point = 0; point < size; point++)
        {
            float position = point / last;
            red[point] = minimum[0] + (position * (maximum[0] - minimum[0]));
            green[point] = minimum[1] + (position * (maximum[1] - minimum[1]));
            blue[point] = minimum[2] + (position * (maximum[2] - minimum[2]));
        }

        for (int index = 0; index < layers.Count; index++)
        {
            LutLayer layer = layers[index];
            if (layer == null || !layer.HasEffect) continue;

            float blend = (float)(layer.ApplyAtPercent / 100d);
            Lut1D lut = layer.Lut1D;

            for (int point = 0; point < size; point++)
            {
                lut.Sample(
                    red[point],
                    green[point],
                    blue[point],
                    out float sampledRed,
                    out float sampledGreen,
                    out float sampledBlue);

                if (blend >= 1f)
                {
                    red[point] = sampledRed;
                    green[point] = sampledGreen;
                    blue[point] = sampledBlue;
                    continue;
                }

                red[point] += (sampledRed - red[point]) * blend;
                green[point] += (sampledGreen - green[point]) * blend;
                blue[point] += (sampledBlue - blue[point]) * blend;
            }
        }

        curves = new Lut1D(red, green, blue, minimum, maximum);
        return true;
    }

    /// <summary>Works out the size the automatic rule gives a chain.</summary>
    /// <param name="layers">The chain. Null entries and layers applied at nothing are ignored.</param>
    /// <returns>
    /// The largest size any applied layer has, floored at <see cref="DefaultMinimumOutputSize" /> and capped
    /// at <see cref="DefaultMaximumOutputSize" />.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers" /> is null.</exception>
    public static int GetOutputSize(IReadOnlyList<LutLayer> layers)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));

        int largest = 0;
        for (int index = 0; index < layers.Count; index++)
        {
            LutLayer layer = layers[index];
            if (layer == null || !layer.HasEffect) continue;
            if (layer.Size > largest) largest = layer.Size;
        }

        return Clamp(largest, DefaultMinimumOutputSize, DefaultMaximumOutputSize);
    }

    /// <summary>Reports the input domain the chain itself expects - the first applied layer's.</summary>
    /// <param name="layers">The chain. Null entries and layers applied at nothing are ignored.</param>
    /// <param name="minimum">The first applied layer's domain minimum, or 0, 0, 0 when there is none.</param>
    /// <param name="maximum">The first applied layer's domain maximum, or 1, 1, 1 when there is none.</param>
    /// <returns>True when a layer was applied AND its domain is not the usual 0 to 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers" /> is null.</exception>
    /// <remarks>
    /// The chain's input is the picture and the first layer is what sees it, so this is the domain to bake
    /// for when the picture is not an ordinary 0-to-1 one. Nothing here uses it by default; it is here so a
    /// caller can put it into <see cref="LutComposerOptions.OutputDomainMinimum" /> deliberately.
    /// </remarks>
    public static bool TryGetChainDomain(
        IReadOnlyList<LutLayer> layers,
        out float[] minimum,
        out float[] maximum)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));

        minimum = new[] { 0f, 0f, 0f };
        maximum = new[] { 1f, 1f, 1f };

        for (int index = 0; index < layers.Count; index++)
        {
            LutLayer layer = layers[index];
            if (layer == null || !layer.HasEffect) continue;

            // A shaped layer's shaper is what sees the picture, so its domain is the chain's.
            bool fromTable = layer.IsThreeDimensional && !layer.IsShaped;

            ReadOnlySpan<float> layerMinimum =
                fromTable ? layer.Lut3D.DomainMinimum : layer.Lut1D.DomainMinimum;

            ReadOnlySpan<float> layerMaximum =
                fromTable ? layer.Lut3D.DomainMaximum : layer.Lut1D.DomainMaximum;

            bool isDefault = true;
            for (int channel = 0; channel < LutDomain.Channels; channel++)
            {
                minimum[channel] = layerMinimum[channel];
                maximum[channel] = layerMaximum[channel];
                if (layerMinimum[channel] != 0f || layerMaximum[channel] != 1f) isDefault = false;
            }

            return !isDefault;
        }

        return false;
    }

    /// <summary>Fills a lattice with the colour each of its nodes stands for, over 0 to 1.</summary>
    /// <param name="lattice">
    /// <c>size * size * size * 3</c> numbers to fill, red changing fastest.
    /// </param>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <exception cref="ArgumentException">The span is not the right length for the size.</exception>
    public static void FillIdentity(Span<float> lattice, int size) =>
        FillIdentity(lattice, size, Zeroes, Ones);

    /// <summary>Fills a lattice with the colour each of its nodes stands for, over a stated domain.</summary>
    /// <param name="lattice">
    /// <c>size * size * size * 3</c> numbers to fill, red changing fastest.
    /// </param>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <param name="domainMinimum">The colour the first node of each axis stands for - three numbers.</param>
    /// <param name="domainMaximum">The colour the last node of each axis stands for - three numbers.</param>
    /// <exception cref="ArgumentException">
    /// The span is not the right length for the size, or a domain bound is not three numbers.
    /// </exception>
    public static void FillIdentity(
        Span<float> lattice,
        int size,
        ReadOnlySpan<float> domainMinimum,
        ReadOnlySpan<float> domainMaximum)
    {
        if (size < Lut3D.MinimumSize || size > Lut3D.MaximumSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"A three-dimensional lookup lattice has between {Lut3D.MinimumSize} and {Lut3D.MaximumSize} "
                + "nodes a side.");
        }

        int required = size * size * size * 3;
        if (lattice.Length != required)
        {
            throw new ArgumentException(
                $"A {size}-node lattice is exactly {required} numbers; this span holds {lattice.Length}.",
                nameof(lattice));
        }

        if (domainMinimum.Length != LutDomain.Channels || domainMaximum.Length != LutDomain.Channels)
        {
            throw new ArgumentException(
                "A lattice's domain states one number per channel - three of them.",
                nameof(domainMinimum));
        }

        float last = size - 1;
        float redSpan = domainMaximum[0] - domainMinimum[0];
        float greenSpan = domainMaximum[1] - domainMinimum[1];
        float blueSpan = domainMaximum[2] - domainMinimum[2];

        int index = 0;
        for (int blue = 0; blue < size; blue++)
        {
            float blueValue = domainMinimum[2] + ((blue / last) * blueSpan);
            for (int green = 0; green < size; green++)
            {
                float greenValue = domainMinimum[1] + ((green / last) * greenSpan);
                for (int red = 0; red < size; red++)
                {
                    lattice[index++] = domainMinimum[0] + ((red / last) * redSpan);
                    lattice[index++] = greenValue;
                    lattice[index++] = blueValue;
                }
            }
        }
    }

    /// <summary>Mixes one layer into a lattice, in place.</summary>
    /// <param name="lattice">The colours so far, three numbers a node.</param>
    /// <param name="layer">The layer. Null, or a layer applied at nothing, changes nothing.</param>
    /// <param name="interpolation">How to sample the layer between its own nodes.</param>
    public static void ApplyLayer(Span<float> lattice, LutLayer layer, LutInterpolation interpolation)
    {
        if (layer == null || !layer.HasEffect) return;

        if (layer.IsShaped)
        {
            ApplyShapedLayer(
                lattice,
                layer.Lut1D,
                layer.Lut3D,
                layer.ApplyAtPercent,
                interpolation);

            return;
        }

        if (layer.IsThreeDimensional)
        {
            ApplyLayer(lattice, layer.Lut3D, layer.ApplyAtPercent, interpolation);
            return;
        }

        ApplyLayer(lattice, layer.Lut1D, layer.ApplyAtPercent, interpolation);
    }

    /// <summary>Mixes a shaper and the table it feeds into a lattice as one step, in place.</summary>
    /// <param name="lattice">The colours so far, three numbers a node.</param>
    /// <param name="shaper">The curves that run first, sampled at their own size and over their own domain.</param>
    /// <param name="lut">The table that runs on what the curves produced, likewise at its own size.</param>
    /// <param name="applyAtPercent">
    /// How much of the PAIR to mix in, 0 to 100. Values outside are clamped.
    /// </param>
    /// <param name="interpolation">How to sample the table between its own nodes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaper" /> or <paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentException">The span is not a whole number of triplets.</exception>
    public static void ApplyShapedLayer(
        Span<float> lattice,
        Lut1D shaper,
        Lut3D lut,
        double applyAtPercent,
        LutInterpolation interpolation)
    {
        if (shaper == null) throw new ArgumentNullException(nameof(shaper));
        if (lut == null) throw new ArgumentNullException(nameof(lut));
        EnsureTriplets(lattice);

        float blend = ToBlend(applyAtPercent);
        if (blend <= 0f) return;

        for (int index = 0; index < lattice.Length; index += 3)
        {
            shaper.Sample(
                lattice[index],
                lattice[index + 1],
                lattice[index + 2],
                out float shapedRed,
                out float shapedGreen,
                out float shapedBlue);

            lut.Sample(
                shapedRed,
                shapedGreen,
                shapedBlue,
                interpolation,
                out float red,
                out float green,
                out float blue);

            if (blend >= 1f)
            {
                lattice[index] = red;
                lattice[index + 1] = green;
                lattice[index + 2] = blue;
                continue;
            }

            lattice[index] += (red - lattice[index]) * blend;
            lattice[index + 1] += (green - lattice[index + 1]) * blend;
            lattice[index + 2] += (blue - lattice[index + 2]) * blend;
        }
    }

    /// <summary>Mixes a three-dimensional table into a lattice, in place.</summary>
    /// <param name="lattice">The colours so far, three numbers a node.</param>
    /// <param name="lut">The table to sample, at its own size and over its own domain.</param>
    /// <param name="applyAtPercent">How much of it to mix in, 0 to 100. Values outside are clamped.</param>
    /// <param name="interpolation">How to sample the table between its own nodes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentException">The span is not a whole number of triplets.</exception>
    public static void ApplyLayer(
        Span<float> lattice,
        Lut3D lut,
        double applyAtPercent,
        LutInterpolation interpolation)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));
        EnsureTriplets(lattice);

        float blend = ToBlend(applyAtPercent);
        if (blend <= 0f) return;

        for (int index = 0; index < lattice.Length; index += 3)
        {
            lut.Sample(
                lattice[index],
                lattice[index + 1],
                lattice[index + 2],
                interpolation,
                out float red,
                out float green,
                out float blue);

            if (blend >= 1f)
            {
                lattice[index] = red;
                lattice[index + 1] = green;
                lattice[index + 2] = blue;
                continue;
            }

            lattice[index] += (red - lattice[index]) * blend;
            lattice[index + 1] += (green - lattice[index + 1]) * blend;
            lattice[index + 2] += (blue - lattice[index + 2]) * blend;
        }
    }

    /// <summary>Mixes a set of per-channel curves into a lattice, in place.</summary>
    /// <param name="lattice">The colours so far, three numbers a node.</param>
    /// <param name="lut">The curves to sample, at their own size and over their own domain.</param>
    /// <param name="applyAtPercent">How much of them to mix in, 0 to 100. Values outside are clamped.</param>
    /// <param name="interpolation">
    /// Accepted for symmetry and ignored: a curve is interpolated along one axis, where the two methods are
    /// the same thing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentException">The span is not a whole number of triplets.</exception>
    public static void ApplyLayer(
        Span<float> lattice,
        Lut1D lut,
        double applyAtPercent,
        LutInterpolation interpolation)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));
        EnsureTriplets(lattice);

        _ = interpolation;

        float blend = ToBlend(applyAtPercent);
        if (blend <= 0f) return;

        for (int index = 0; index < lattice.Length; index += 3)
        {
            lut.Sample(
                lattice[index],
                lattice[index + 1],
                lattice[index + 2],
                out float red,
                out float green,
                out float blue);

            if (blend >= 1f)
            {
                lattice[index] = red;
                lattice[index + 1] = green;
                lattice[index + 2] = blue;
                continue;
            }

            lattice[index] += (red - lattice[index]) * blend;
            lattice[index + 1] += (green - lattice[index + 1]) * blend;
            lattice[index + 2] += (blue - lattice[index + 2]) * blend;
        }
    }

    private static ReadOnlySpan<float> Zeroes => new float[] { 0f, 0f, 0f };

    private static ReadOnlySpan<float> Ones => new float[] { 1f, 1f, 1f };

    private static void EnsureTriplets(Span<float> lattice)
    {
        if (lattice.Length % 3 == 0) return;

        throw new ArgumentException(
            $"A lattice holds three numbers a node; this span holds {lattice.Length}, which is not a whole "
            + "number of them.",
            nameof(lattice));
    }

    private static float ToBlend(double applyAtPercent)
    {
        if (double.IsNaN(applyAtPercent) || applyAtPercent <= 0d) return 0f;
        return applyAtPercent >= 100d ? 1f : (float)(applyAtPercent / 100d);
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (value < minimum) return minimum;
        return value > maximum ? maximum : value;
    }
}
