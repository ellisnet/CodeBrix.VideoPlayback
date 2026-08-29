namespace CodeBrix.VideoPlayback.Skia.Rendering;

/// <summary>
/// A snapshot of what a <see cref="SkiaVideoPresenter" /> has done since it was created or last reset.
/// </summary>
/// <remarks>
/// The two numbers worth watching are <see cref="SurfaceAllocations" />, which should stop rising once
/// playback is warm and rises again only when the frame size changes, and
/// <see cref="EffectCompositions" />, which should rise only when the effect chain is edited.
/// </remarks>
public readonly struct SkiaVideoPresenterStatistics
{
    /// <summary>Creates a snapshot.</summary>
    /// <param name="framesComposed">How many frames have been composed onto the off-screen surface.</param>
    /// <param name="framesDrawn">How many times the composed surface has been blitted to a canvas.</param>
    /// <param name="surfaceAllocations">How many off-screen surfaces have been allocated.</param>
    /// <param name="effectCompositions">How many times the effect chain has been composed into one LUT.</param>
    public SkiaVideoPresenterStatistics(
        long framesComposed,
        long framesDrawn,
        long surfaceAllocations,
        long effectCompositions)
    {
        FramesComposed = framesComposed;
        FramesDrawn = framesDrawn;
        SurfaceAllocations = surfaceAllocations;
        EffectCompositions = effectCompositions;
    }

    /// <summary>How many frames have been composed onto the off-screen surface.</summary>
    public long FramesComposed { get; }

    /// <summary>How many times the composed surface has been blitted to a canvas.</summary>
    public long FramesDrawn { get; }

    /// <summary>
    /// How many off-screen surfaces have been allocated. One per frame size, and no more; a number that keeps
    /// rising during steady playback is a defect.
    /// </summary>
    public long SurfaceAllocations { get; }

    /// <summary>
    /// How many times the effect chain has been composed into one resultant lookup table. It rises when the
    /// chain is edited, never per frame.
    /// </summary>
    public long EffectCompositions { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"composed {FramesComposed}, drawn {FramesDrawn}, surfaces {SurfaceAllocations}, "
        + $"effect compositions {EffectCompositions}";
}
