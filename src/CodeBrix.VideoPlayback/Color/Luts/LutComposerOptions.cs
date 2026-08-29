namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// The choices <see cref="LutComposer" /> makes when it folds a chain into one effective table.
/// </summary>
/// <remarks>
/// Every one of them has a default that suits the common case; an instance with nothing set behaves exactly
/// as the <see cref="LutComposer.Compose(System.Collections.Generic.IReadOnlyList{LutLayer})" /> overload
/// that takes no options.
/// </remarks>
public sealed class LutComposerOptions
{
    /// <summary>How each layer is sampled between its own nodes. Tetrahedral by default.</summary>
    /// <remarks>
    /// Tetrahedral is what colour-grading tools and FFmpeg's <c>lut3d</c> filter do, so the effective table
    /// this library bakes agrees with what those tools would have produced from the same chain.
    /// </remarks>
    public LutInterpolation Interpolation { get; set; } = LutInterpolation.Tetrahedral;

    /// <summary>
    /// The number of nodes a side of the effective table, or 0 to work it out from the chain.
    /// </summary>
    /// <remarks>
    /// The automatic rule is in <see cref="LutComposer.GetOutputSize" />: the largest size any applied layer
    /// has, never below <see cref="LutComposer.DefaultMinimumOutputSize" /> and never above
    /// <see cref="LutComposer.DefaultMaximumOutputSize" />.
    /// </remarks>
    public int OutputSize { get; set; }

    /// <summary>
    /// The input value the first node of each axis of the effective table stands for - three numbers, or
    /// null for 0, 0, 0.
    /// </summary>
    /// <remarks>
    /// The default suits every picture this library shows, because a decoded frame's colour is normalised to
    /// 0 to 1 on both render paths and FFmpeg's raw video is the same. Set this only to bake a table for
    /// something else - and see <see cref="LutComposer.TryGetChainDomain" /> for the chain's own domain.
    /// </remarks>
    public float[] OutputDomainMinimum { get; set; }

    /// <summary>
    /// The input value the last node of each axis of the effective table stands for - three numbers, or null
    /// for 1, 1, 1.
    /// </summary>
    public float[] OutputDomainMaximum { get; set; }
}
