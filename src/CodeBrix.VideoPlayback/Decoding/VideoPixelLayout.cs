namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The plane layout and chroma subsampling of a decoded video frame.
/// </summary>
/// <remarks>
/// The values match the AV1 sequence header's monochrome / subsampling_x / subsampling_y triple, which is
/// also what Matroska records in its video colour elements, so a container or a decoder can map onto this
/// enumeration without a lookup table.
/// </remarks>
public enum VideoPixelLayout
{
    /// <summary>The layout is not known yet - a decoder reports this before it has parsed a sequence header.</summary>
    Unknown = 0,

    /// <summary>Luma only: one plane, no chroma. AV1's <c>monochrome</c> flag.</summary>
    Gray = 1,

    /// <summary>4:2:0 - chroma is half width and half height (subsampling_x = 1, subsampling_y = 1).</summary>
    I420 = 2,

    /// <summary>4:2:2 - chroma is half width and full height (subsampling_x = 1, subsampling_y = 0).</summary>
    I422 = 3,

    /// <summary>4:4:4 - chroma is full width and full height (subsampling_x = 0, subsampling_y = 0).</summary>
    I444 = 4,
}
