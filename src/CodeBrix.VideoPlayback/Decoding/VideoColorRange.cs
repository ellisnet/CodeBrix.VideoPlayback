namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Whether a frame's samples use the full numeric range or the studio ("limited") range.
/// </summary>
public enum VideoColorRange
{
    /// <summary>Not stated by the stream; treated as <see cref="Limited" />, which is what almost all video uses.</summary>
    Unspecified = 0,

    /// <summary>
    /// Studio range: for 8-bit, luma occupies 16-235 and chroma 16-240, with the values outside that reserved
    /// for footroom and headroom. Scales with bit depth.
    /// </summary>
    Limited = 1,

    /// <summary>Full range: samples occupy 0 to (1 &lt;&lt; bit depth) - 1 with nothing reserved.</summary>
    Full = 2,
}
