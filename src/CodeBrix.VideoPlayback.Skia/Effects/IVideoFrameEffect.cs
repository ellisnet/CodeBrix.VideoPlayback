namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// One step of a presenter's colour effect chain.
/// </summary>
/// <remarks>
/// <para>
/// Effects are not applied to pixels. Every effect in the chain is composed, in order, into ONE resultant
/// three-dimensional lookup table, which is what the shader samples - so a chain of ten effects costs exactly
/// what a chain of one costs: a single texture sample per pixel. The composition happens when the chain is
/// edited, never per frame.
/// </para>
/// <para>
/// That is also the limit of what an effect can be: anything expressible as "this colour becomes that colour"
/// fits, and anything that needs to see its neighbours - a blur, a sharpen, a warp - does not. Those belong
/// in a <see cref="CodeBrix.VideoPlayback.Skia.Composition.IVideoLayer" />, which gets a whole canvas.
/// </para>
/// </remarks>
public interface IVideoFrameEffect
{
    /// <summary>A short name for this effect, for diagnostics and for a user interface listing the chain.</summary>
    string Name { get; }

    /// <summary>Folds this effect into the resultant lookup table being built.</summary>
    /// <param name="composer">
    /// The table as the effects before this one have left it. Applying something to it composes it AFTER
    /// them.
    /// </param>
    void Compose(EffectComposer composer);
}
