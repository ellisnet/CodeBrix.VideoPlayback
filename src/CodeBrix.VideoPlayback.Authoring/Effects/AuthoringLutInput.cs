using System;
using System.Globalization;
using System.IO;

namespace CodeBrix.VideoPlayback.Authoring.Effects;

/// <summary>
/// One ".cube" colour lookup table in an authoring chain, together with how much of it to apply.
/// </summary>
/// <remarks>
/// This is the authoring-side twin of the presenter's LutEffect and of the core's LutLayer: the same file,
/// the same percentage, and - because the chain is folded by the same composer - the same resulting colour.
/// Instances are immutable.
/// </remarks>
public sealed class AuthoringLutInput
{
    /// <summary>Creates a lookup-table input.</summary>
    /// <param name="path">The path of a ".cube" file.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100. Values outside are clamped.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applyAtPercent" /> is not a number.</exception>
    public AuthoringLutInput(string path, double applyAtPercent = 100d)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A lookup-table file path is required.", nameof(path));
        }

        if (double.IsNaN(applyAtPercent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(applyAtPercent),
                applyAtPercent,
                "An apply-at percentage is a number between 0 and 100.");
        }

        Path = path;
        ApplyAtPercent = applyAtPercent <= 0d ? 0d : applyAtPercent >= 100d ? 100d : applyAtPercent;
    }

    /// <summary>The path of the ".cube" file.</summary>
    public string Path { get; }

    /// <summary>How much of the table to apply, 0 to 100.</summary>
    public double ApplyAtPercent { get; }

    /// <summary>False when the table is applied at nothing and the chain may skip it entirely.</summary>
    public bool HasEffect => ApplyAtPercent > 0d;

    /// <inheritdoc />
    public override string ToString() =>
        System.IO.Path.GetFileName(Path)
        + " at " + ApplyAtPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
