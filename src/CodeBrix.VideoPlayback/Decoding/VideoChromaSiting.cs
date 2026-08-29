namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Where a subsampled chroma sample sits relative to the luma samples it covers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown" />, <see cref="Vertical" /> and <see cref="Colocated" /> are numbered exactly as AV1's
/// <c>chroma_sample_position</c> field numbers them. AV1 reserves its value 3; this library spends that slot
/// on <see cref="Interstitial" />, the JPEG / MPEG-1 centre siting, because Matroska CAN express it (through
/// its <c>ChromaSitingHorz</c> / <c>ChromaSitingVert</c> pair, which the Matroska reader folds onto these
/// values) even though an AV1 sequence header cannot.
/// </para>
/// <para>
/// Siting only affects 4:2:0 and 4:2:2 content - at 4:4:4 every chroma sample is co-sited with its luma
/// sample by construction, and monochrome has no chroma at all.
/// </para>
/// </remarks>
public enum VideoChromaSiting
{
    /// <summary>
    /// Not stated. Treated as <see cref="Vertical" />, which is the siting essentially all 4:2:0 video in
    /// these containers actually uses.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// HORIZONTALLY co-sited with the luma column and VERTICALLY halfway between the two luma rows - the
    /// MPEG-2 / "left" siting, and the one essentially all 4:2:0 video in these containers uses.
    /// </summary>
    /// <remarks>
    /// The name comes from AV1's <c>CSP_VERTICAL</c> and describes where the sample is FREE to move: it sits
    /// on the luma column and between the luma rows. Reading the name as "co-sited vertically" is the
    /// classic way to get this backwards, and inverts the chroma upsampling.
    /// </remarks>
    Vertical = 1,

    /// <summary>Co-sited with the top-left luma sample - the "top-left" siting.</summary>
    Colocated = 2,

    /// <summary>
    /// Halfway between all four luma samples - the JPEG / MPEG-1 "centre" siting. AV1 cannot signal this;
    /// Matroska can.
    /// </summary>
    Interstitial = 3,
}
