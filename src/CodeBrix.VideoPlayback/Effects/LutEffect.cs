using System;
using System.Globalization;
using CodeBrix.VideoPlayback.Color.Luts;

namespace CodeBrix.VideoPlayback.Effects;

/// <summary>
/// An effect that applies a colour lookup table - either a full three-dimensional one or a set of
/// per-channel curves - at a chosen strength.
/// </summary>
/// <remarks>
/// This is the effect the chain was designed around, and for most applications it is the only one they will
/// ever need: a colour grade, a film emulation, a night-vision look, a false-colour scale and a simple
/// brightness curve are all lookup tables. Several of them in one chain compose into one table and cost one
/// texture sample, and each one carries its own <see cref="ApplyAtPercent" /> so a grade can be dialled back
/// without re-exporting it.
/// </remarks>
public sealed class LutEffect : IVideoFrameEffect
{
    private readonly LutLayer layer;

    /// <summary>Creates an effect applying a three-dimensional table in full.</summary>
    /// <param name="lut">The table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut3D lut)
        : this(lut, "lookup table")
    {
    }

    /// <summary>Creates a named effect applying a three-dimensional table in full.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut3D lut, string name)
        : this(lut, name, 100d)
    {
    }

    /// <summary>Creates a named effect applying part of a three-dimensional table.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <param name="applyAtPercent">
    /// How much of it to apply, 0 to 100. Values outside are clamped; 0 leaves the picture alone.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public LutEffect(Lut3D lut, string name, double applyAtPercent)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));

        Name = string.IsNullOrWhiteSpace(name) ? "lookup table" : name;
        layer = new LutLayer(lut, applyAtPercent, Name);
    }

    /// <summary>Creates an effect applying per-channel curves in full.</summary>
    /// <param name="lut">The curves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut1D lut)
        : this(lut, "channel curves")
    {
    }

    /// <summary>Creates a named effect applying per-channel curves in full.</summary>
    /// <param name="lut">The curves.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut1D lut, string name)
        : this(lut, name, 100d)
    {
    }

    /// <summary>Creates a named effect applying part of a set of per-channel curves.</summary>
    /// <param name="lut">The curves.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <param name="applyAtPercent">
    /// How much of them to apply, 0 to 100. Values outside are clamped; 0 leaves the picture alone.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public LutEffect(Lut1D lut, string name, double applyAtPercent)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));

        Name = string.IsNullOrWhiteSpace(name) ? "channel curves" : name;
        layer = new LutLayer(lut, applyAtPercent, Name);
    }

    /// <summary>Creates an effect from a chain layer, whatever kind of table it holds.</summary>
    /// <param name="layer">The layer, carrying its own table and its own apply-at percentage.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layer" /> is null.</exception>
    public LutEffect(LutLayer layer)
    {
        this.layer = layer ?? throw new ArgumentNullException(nameof(layer));
        Name = layer.Name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// How much of this effect's table is applied, 0 to 100. 100 by default, which replaces the colour
    /// outright.
    /// </summary>
    /// <remarks>
    /// 50 lands half way between the colour as it reached this effect and what the table makes of it; 0
    /// leaves it alone entirely and costs nothing to compose. The blend happens once, when the chain is
    /// composed - never per pixel.
    /// </remarks>
    public double ApplyAtPercent => layer.ApplyAtPercent;

    /// <summary>The chain layer this effect applies.</summary>
    public LutLayer Layer => layer;

    /// <summary>
    /// The three-dimensional table this effect applies, or null when it applies curves only.
    /// </summary>
    public Lut3D Lut3D => layer.Lut3D;

    /// <summary>
    /// The per-channel curves this effect applies, or null when it applies a full table only. In an effect
    /// made from a combined ".cube" file these are the shaper that runs before the table.
    /// </summary>
    public Lut1D Lut1D => layer.Lut1D;

    /// <summary>Reads a ".cube" file and makes an effect that applies it in full.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>The effect, named after the file (or after its TITLE).</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="System.IO.InvalidDataException">The file is not a readable ".cube" table.</exception>
    public static LutEffect FromCubeFile(string path) => FromCubeFile(path, 100d);

    /// <summary>Reads a ".cube" file and makes an effect that applies part of it.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <returns>The effect, named after the file (or after its TITLE).</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="System.IO.InvalidDataException">The file is not a readable ".cube" table.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public static LutEffect FromCubeFile(string path, double applyAtPercent) =>
        new LutEffect(LutLayer.FromCubeFile(path, applyAtPercent));

    /// <summary>Makes an effect from a table already read out of a ".cube" file.</summary>
    /// <param name="cube">What the file held.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <returns>The effect, named after the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cube" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public static LutEffect FromCube(CubeLut cube, double applyAtPercent = 100d)
    {
        if (cube == null) throw new ArgumentNullException(nameof(cube));
        return new LutEffect(cube.ToLayer(applyAtPercent));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="composer" /> is null.</exception>
    public void Compose(EffectComposer composer)
    {
        if (composer == null) throw new ArgumentNullException(nameof(composer));

        composer.ApplyLayer(layer);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string table = layer.IsShaped
            ? layer.Lut1D + " shaping " + layer.Lut3D
            : (layer.Lut3D != null ? layer.Lut3D.ToString() : layer.Lut1D.ToString());

        string strength = layer.ApplyAtPercent >= 100d
            ? string.Empty
            : " at " + layer.ApplyAtPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%";

        return $"{Name} ({table}){strength}";
    }
}
