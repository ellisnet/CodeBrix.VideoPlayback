using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// The facts an AV1 sequence header states about a whole stream: its profile and level, its frame size, its
/// sample layout and bit depth, and its colour description.
/// </summary>
/// <remarks>
/// This is what an authoring tool needs in order to write a codec configuration record for a stream it did
/// not encode itself, and what a container writer needs in order to fill in a track's video fields. It is not
/// a decoder: only the parts of the header that a container cares about are read.
/// </remarks>
public sealed class Av1SequenceHeader
{
    /// <summary>The coding profile: 0 (Main), 1 (High) or 2 (Professional).</summary>
    public int SeqProfile { get; internal set; }

    /// <summary>True when the stream codes a single still picture.</summary>
    public bool StillPicture { get; internal set; }

    /// <summary>True when the header uses the short form that a still picture is allowed to use.</summary>
    public bool ReducedStillPictureHeader { get; internal set; }

    /// <summary>The level of the first operating point.</summary>
    public int SeqLevelIdx0 { get; internal set; }

    /// <summary>The tier of the first operating point: 0 for Main, 1 for High.</summary>
    public int SeqTier0 { get; internal set; }

    /// <summary>True when samples are more than eight bits.</summary>
    public bool HighBitDepth { get; internal set; }

    /// <summary>True when samples are twelve bits rather than ten. Only meaningful in profile 2.</summary>
    public bool TwelveBit { get; internal set; }

    /// <summary>True when the stream has no chroma planes.</summary>
    public bool Monochrome { get; internal set; }

    /// <summary>1 when chroma is subsampled horizontally.</summary>
    public int SubsamplingX { get; internal set; }

    /// <summary>1 when chroma is subsampled vertically.</summary>
    public int SubsamplingY { get; internal set; }

    /// <summary>Where a chroma sample sits relative to luma, as the AV1 field numbers it.</summary>
    public int ChromaSamplePosition { get; internal set; }

    /// <summary>Bits per sample: 8, 10 or 12.</summary>
    public int BitDepth { get; internal set; }

    /// <summary>The largest frame width the stream will use, in pixels.</summary>
    public int MaxFrameWidth { get; internal set; }

    /// <summary>The largest frame height the stream will use, in pixels.</summary>
    public int MaxFrameHeight { get; internal set; }

    /// <summary>True when frames carry identifying numbers.</summary>
    public bool FrameIdNumbersPresent { get; internal set; }

    /// <summary>True when the stream may ask a decoder to synthesise film grain.</summary>
    public bool FilmGrainParamsPresent { get; internal set; }

    /// <summary>True when the header stated its colour description rather than leaving it unspecified.</summary>
    public bool ColorDescriptionPresent { get; internal set; }

    /// <summary>The colour description the header states, resolved from its primaries, transfer and matrix fields.</summary>
    public VideoColorInfo Color { get; internal set; }

    /// <summary>The plane layout the header describes.</summary>
    public VideoPixelLayout Layout =>
        Monochrome
            ? VideoPixelLayout.Gray
            : SubsamplingX == 1
                ? SubsamplingY == 1 ? VideoPixelLayout.I420 : VideoPixelLayout.I422
                : VideoPixelLayout.I444;

    /// <inheritdoc />
    public override string ToString() =>
        $"AV1 profile {SeqProfile} level {SeqLevelIdx0} tier {SeqTier0}, {MaxFrameWidth}x{MaxFrameHeight}, "
        + $"{Layout} {BitDepth}-bit";
}
