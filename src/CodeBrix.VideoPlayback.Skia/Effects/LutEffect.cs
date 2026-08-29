using System;

namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// An effect that applies a colour lookup table - either a full three-dimensional one or a set of
/// per-channel curves.
/// </summary>
/// <remarks>
/// This is the effect the chain was designed around, and for most applications it is the only one they will
/// ever need: a colour grade, a film emulation, a night-vision look, a false-colour scale and a simple
/// brightness curve are all lookup tables. Several of them in one chain compose into one table and cost one
/// texture sample.
/// </remarks>
public sealed class LutEffect : IVideoFrameEffect
{
    private readonly Lut3D lut3D;
    private readonly Lut1D lut1D;

    /// <summary>Creates an effect applying a three-dimensional table.</summary>
    /// <param name="lut">The table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut3D lut)
        : this(lut, "lookup table")
    {
    }

    /// <summary>Creates a named effect applying a three-dimensional table.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut3D lut, string name)
    {
        lut3D = lut ?? throw new ArgumentNullException(nameof(lut));
        Name = string.IsNullOrWhiteSpace(name) ? "lookup table" : name;
    }

    /// <summary>Creates an effect applying per-channel curves.</summary>
    /// <param name="lut">The curves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut1D lut)
        : this(lut, "channel curves")
    {
    }

    /// <summary>Creates a named effect applying per-channel curves.</summary>
    /// <param name="lut">The curves.</param>
    /// <param name="name">A short name for diagnostics and for a user interface listing the chain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public LutEffect(Lut1D lut, string name)
    {
        lut1D = lut ?? throw new ArgumentNullException(nameof(lut));
        Name = string.IsNullOrWhiteSpace(name) ? "channel curves" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The three-dimensional table this effect applies, or null when it applies curves instead.</summary>
    public Lut3D Lut3D => lut3D;

    /// <summary>The per-channel curves this effect applies, or null when it applies a full table instead.</summary>
    public Lut1D Lut1D => lut1D;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="composer" /> is null.</exception>
    public void Compose(EffectComposer composer)
    {
        if (composer == null) throw new ArgumentNullException(nameof(composer));

        if (lut3D != null)
        {
            composer.ApplyLut(lut3D);
            return;
        }

        composer.ApplyLut(lut1D);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} ({(lut3D != null ? lut3D.ToString() : lut1D.ToString())})";
}
