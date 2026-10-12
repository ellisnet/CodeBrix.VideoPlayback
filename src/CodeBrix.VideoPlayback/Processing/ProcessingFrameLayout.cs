namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// The plane layouts the managed frame-processing primitives understand.
/// </summary>
/// <remarks>
/// The planar values match <see cref="CodeBrix.VideoPlayback.Decoding.VideoPixelLayout" /> number for number,
/// so a decoded frame's layout casts straight across; <see cref="Rgba" /> is the one addition, for interleaved
/// eight-bit pictures such as still images and export backdrops.
/// </remarks>
public enum ProcessingFrameLayout
{
    /// <summary>Luma only: one plane, no chroma. An optional alpha plane may ride along.</summary>
    Gray = 1,

    /// <summary>4:2:0 - three planes, chroma half width and half height (each rounded up).</summary>
    I420 = 2,

    /// <summary>4:2:2 - three planes, chroma half width (rounded up) and full height.</summary>
    I422 = 3,

    /// <summary>4:4:4 - three planes, all full size.</summary>
    I444 = 4,

    /// <summary>One interleaved plane of eight-bit R, G, B, A bytes, four per pixel.</summary>
    Rgba = 5,
}
