namespace CodeBrix.VideoPlayback.Processing;

/// <summary>The resampling kernel a <see cref="PlaneScaler" /> uses.</summary>
/// <remarks>
/// Both kernels are widened by the scale factor when shrinking, so every source sample contributes - the same
/// thing FFmpeg's <c>scale</c> filter does for <c>flags=bilinear</c> and <c>flags=lanczos</c>. Use
/// <see cref="PlaneScaler.ChooseDefault" /> when there is no reason to prefer one.
/// </remarks>
public enum ScalingFilter
{
    /// <summary>
    /// A triangle (tent) kernel: two taps a side when enlarging, widened by the factor when shrinking. Soft,
    /// cheap and never overshoots.
    /// </summary>
    Bilinear = 0,

    /// <summary>
    /// A three-lobe Lanczos (windowed sinc) kernel: six taps when enlarging, widened by the factor when
    /// shrinking. Sharper and alias-free for downscales; it can overshoot at hard edges, so results are
    /// clamped to the sample range.
    /// </summary>
    Lanczos3 = 1,
}
