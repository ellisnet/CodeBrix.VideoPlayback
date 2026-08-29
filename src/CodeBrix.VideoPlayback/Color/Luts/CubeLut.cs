using System;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// What one ".cube" file holds: a name, and a three-dimensional table, or a set of per-channel curves, or
/// both.
/// </summary>
/// <remarks>
/// <para>
/// A ".cube" file usually states <c>LUT_3D_SIZE</c> or <c>LUT_1D_SIZE</c>, and then exactly one of
/// <see cref="Lut3D" /> and <see cref="Lut1D" /> is set. Some files - the arrangement DaVinci Resolve's
/// dialect allows - state BOTH, and then the curves are a SHAPER that runs first and hands its answer to the
/// table; <see cref="IsCombined" /> says so, and <see cref="ToLayer" /> turns the pair into the one chain
/// layer they behave as.
/// </para>
/// <para>Instances are immutable and safe to share between threads.</para>
/// </remarks>
public sealed class CubeLut
{
    private readonly Lut3D table;
    private readonly Lut1D curves;

    /// <summary>Wraps a three-dimensional table read from a file.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="title">The file's TITLE, or null when it stated none.</param>
    /// <param name="name">The name to use in diagnostics - the title, or the file's own name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public CubeLut(Lut3D lut, string title, string name)
    {
        table = lut ?? throw new ArgumentNullException(nameof(lut));
        Title = title;
        Name = string.IsNullOrWhiteSpace(name) ? "cube lookup table" : name;
    }

    /// <summary>Wraps per-channel curves read from a file.</summary>
    /// <param name="lut">The curves.</param>
    /// <param name="title">The file's TITLE, or null when it stated none.</param>
    /// <param name="name">The name to use in diagnostics - the title, or the file's own name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public CubeLut(Lut1D lut, string title, string name)
    {
        curves = lut ?? throw new ArgumentNullException(nameof(lut));
        Title = title;
        Name = string.IsNullOrWhiteSpace(name) ? "cube lookup table" : name;
    }

    /// <summary>Wraps the shaper curves and the table of a combined file.</summary>
    /// <param name="shaper">The curves that run first.</param>
    /// <param name="lut">The table that runs on what the curves produced.</param>
    /// <param name="title">The file's TITLE, or null when it stated none.</param>
    /// <param name="name">The name to use in diagnostics - the title, or the file's own name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaper" /> or <paramref name="lut" /> is null.</exception>
    public CubeLut(Lut1D shaper, Lut3D lut, string title, string name)
    {
        curves = shaper ?? throw new ArgumentNullException(nameof(shaper));
        table = lut ?? throw new ArgumentNullException(nameof(lut));
        Title = title;
        Name = string.IsNullOrWhiteSpace(name) ? "cube lookup table" : name;
    }

    /// <summary>The file's TITLE, or null when it stated none.</summary>
    public string Title { get; }

    /// <summary>A name for diagnostics: the TITLE when there is one, otherwise the file's own name.</summary>
    public string Name { get; }

    /// <summary>The three-dimensional table the file held, or null when it held curves only.</summary>
    public Lut3D Lut3D => table;

    /// <summary>
    /// The per-channel curves the file held, or null when it held a table only. In a combined file these are
    /// the SHAPER that runs before the table.
    /// </summary>
    public Lut1D Lut1D => curves;

    /// <summary>True when the file held a three-dimensional table, combined or not.</summary>
    public bool IsThreeDimensional => table != null;

    /// <summary>True when the file held a shaper AND a table, the shaper running first.</summary>
    public bool IsCombined => table != null && curves != null;

    /// <summary>Turns what the file held into one layer of a chain.</summary>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <returns>The layer, named after the file.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    /// <remarks>
    /// A combined file becomes ONE layer, not two: its shaper and its table are a single artistic step, so
    /// applying the file at half strength means landing half way between the colour and what the PAIR makes
    /// of it - not applying each half at half strength, which would be a different picture.
    /// </remarks>
    public LutLayer ToLayer(double applyAtPercent = 100d)
    {
        if (IsCombined) return new LutLayer(curves, table, applyAtPercent, Name);
        return table != null
            ? new LutLayer(table, applyAtPercent, Name)
            : new LutLayer(curves, applyAtPercent, Name);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (IsCombined) return $"{Name} ({curves} shaping {table})";
        return $"{Name} ({(table != null ? table.ToString() : curves.ToString())})";
    }
}
