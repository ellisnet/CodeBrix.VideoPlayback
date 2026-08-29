using System;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// One lookup table in a chain, together with how much of it to apply.
/// </summary>
/// <remarks>
/// <para>
/// A chain is an ordered list of these. <see cref="LutComposer" /> folds the list into ONE effective table:
/// each layer in turn is sampled at ITS OWN size and over ITS OWN domain, and its answer is mixed into the
/// colour so far by <see cref="ApplyAtPercent" /> - 100 replaces the colour outright, 50 lands half way, 0
/// leaves it alone and the layer is skipped altogether.
/// </para>
/// <para>Instances are immutable and safe to share between threads.</para>
/// </remarks>
public sealed class LutLayer
{
    private readonly Lut3D table;
    private readonly Lut1D curves;
    private readonly bool shaped;

    /// <summary>Puts a three-dimensional table in a chain.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <param name="name">A short name for diagnostics, or null to take one from the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public LutLayer(Lut3D lut, double applyAtPercent = 100d, string name = null)
    {
        table = lut ?? throw new ArgumentNullException(nameof(lut));
        ApplyAtPercent = ClampPercent(applyAtPercent);
        Name = string.IsNullOrWhiteSpace(name) ? lut.ToString() : name;
    }

    /// <summary>Puts a set of per-channel curves in a chain.</summary>
    /// <param name="lut">The curves.</param>
    /// <param name="applyAtPercent">How much of them to apply, 0 to 100. Values outside are clamped.</param>
    /// <param name="name">A short name for diagnostics, or null to take one from the curves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public LutLayer(Lut1D lut, double applyAtPercent = 100d, string name = null)
    {
        curves = lut ?? throw new ArgumentNullException(nameof(lut));
        ApplyAtPercent = ClampPercent(applyAtPercent);
        Name = string.IsNullOrWhiteSpace(name) ? lut.ToString() : name;
    }

    /// <summary>Puts a shaper curve set and the table it feeds in a chain, as ONE layer.</summary>
    /// <param name="shaper">The curves that run first, mapping the colour into the table's domain.</param>
    /// <param name="lut">The table that runs on what the curves produced.</param>
    /// <param name="applyAtPercent">
    /// How much of the PAIR to apply, 0 to 100. Values outside are clamped.
    /// </param>
    /// <param name="name">A short name for diagnostics, or null to take one from the pair.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaper" /> or <paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    /// <remarks>
    /// This is what a combined ".cube" file holds. The two tables are one artistic step, so the percentage
    /// mixes in what the PAIR makes of a colour - the shaper is never applied on its own.
    /// </remarks>
    public LutLayer(Lut1D shaper, Lut3D lut, double applyAtPercent = 100d, string name = null)
    {
        curves = shaper ?? throw new ArgumentNullException(nameof(shaper));
        table = lut ?? throw new ArgumentNullException(nameof(lut));
        shaped = true;
        ApplyAtPercent = ClampPercent(applyAtPercent);
        Name = string.IsNullOrWhiteSpace(name) ? shaper + " shaping " + lut : name;
    }

    /// <summary>How much of this layer to apply, 0 to 100.</summary>
    public double ApplyAtPercent { get; }

    /// <summary>A short name for diagnostics and for a user interface listing the chain.</summary>
    public string Name { get; }

    /// <summary>The three-dimensional table this layer holds, or null when it holds curves only.</summary>
    public Lut3D Lut3D => table;

    /// <summary>
    /// The per-channel curves this layer holds, or null when it holds a table only. In a shaped layer these
    /// are the shaper that runs before the table.
    /// </summary>
    public Lut1D Lut1D => curves;

    /// <summary>True when this layer holds a three-dimensional table, shaped or not.</summary>
    public bool IsThreeDimensional => table != null;

    /// <summary>True when this layer is a shaper and the table it feeds, applied as one step.</summary>
    public bool IsShaped => shaped;

    /// <summary>The layer's own size - nodes a side for a table, points for curves.</summary>
    public int Size => table != null ? table.Size : curves.Size;

    /// <summary>False when the layer is applied at nothing and composing may skip it entirely.</summary>
    public bool HasEffect => ApplyAtPercent > 0d;

    /// <summary>Puts a table read from a ".cube" file in a chain.</summary>
    /// <param name="cube">What the file held.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <returns>The layer, named after the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cube" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public static LutLayer FromCube(CubeLut cube, double applyAtPercent = 100d)
    {
        if (cube == null) throw new ArgumentNullException(nameof(cube));

        return cube.ToLayer(applyAtPercent);
    }

    /// <summary>Reads a ".cube" file and puts what it held in a chain.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <returns>The layer, named after the file.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="System.IO.InvalidDataException">The file is not a readable ".cube" table.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public static LutLayer FromCubeFile(string path, double applyAtPercent = 100d) =>
        FromCube(CubeLutFile.ReadFile(path), applyAtPercent);

    /// <inheritdoc />
    public override string ToString() =>
        Name + " at " + ApplyAtPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%";

    private static double ClampPercent(double percent)
    {
        if (double.IsNaN(percent))
        {
            throw new ArgumentOutOfRangeException(
                "applyAtPercent",
                percent,
                "A layer's apply-at percentage is a number between 0 and 100.");
        }

        if (percent <= 0d) return 0d;
        return percent >= 100d ? 100d : percent;
    }
}
