using System;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Everything a presenter, shader or colour converter needs to interpret a frame's samples: the primaries,
/// the transfer characteristic, the YCbCr matrix, the sample range and the chroma siting.
/// </summary>
/// <remarks>
/// <para>
/// A value of this type is always present on a <see cref="CodeBrix.VideoPlayback.Frames.VideoFrame" /> - it
/// is never absent and never has to be guessed at. When a stream says nothing, the fields read
/// <c>Unspecified</c> / <c>Unknown</c> and <see cref="Resolve" /> turns them into the concrete choices this
/// library makes.
/// </para>
/// <para>This is an immutable value type; every "change" produces a new value.</para>
/// </remarks>
public readonly struct VideoColorInfo : IEquatable<VideoColorInfo>
{
    /// <summary>
    /// The value used when a stream states nothing at all: unspecified primaries, transfer and matrix,
    /// unspecified range and unknown chroma siting.
    /// </summary>
    public static readonly VideoColorInfo Unspecified = new VideoColorInfo(
        VideoColorPrimaries.Unspecified,
        VideoTransferCharacteristics.Unspecified,
        VideoMatrixCoefficients.Unspecified,
        VideoColorRange.Unspecified,
        VideoChromaSiting.Unknown);

    /// <summary>Creates a colour description.</summary>
    /// <param name="primaries">The colour primaries.</param>
    /// <param name="transfer">The transfer characteristic.</param>
    /// <param name="matrix">The YCbCr matrix coefficients.</param>
    /// <param name="range">Whether the samples are full or studio range.</param>
    /// <param name="chromaSiting">Where subsampled chroma samples sit relative to luma.</param>
    public VideoColorInfo(
        VideoColorPrimaries primaries,
        VideoTransferCharacteristics transfer,
        VideoMatrixCoefficients matrix,
        VideoColorRange range,
        VideoChromaSiting chromaSiting)
    {
        Primaries = primaries;
        Transfer = transfer;
        Matrix = matrix;
        Range = range;
        ChromaSiting = chromaSiting;
    }

    /// <summary>The colour primaries the stream declares.</summary>
    public VideoColorPrimaries Primaries { get; }

    /// <summary>The transfer characteristic ("gamma curve") the stream declares.</summary>
    public VideoTransferCharacteristics Transfer { get; }

    /// <summary>The YCbCr matrix the stream's chroma planes were formed with.</summary>
    public VideoMatrixCoefficients Matrix { get; }

    /// <summary>Whether the samples cover the full numeric range or the studio range.</summary>
    public VideoColorRange Range { get; }

    /// <summary>Where a subsampled chroma sample sits relative to the luma samples it covers.</summary>
    public VideoChromaSiting ChromaSiting { get; }

    /// <summary>
    /// True when <see cref="Transfer" /> is one of the high-dynamic-range curves - SMPTE ST 2084 ("PQ") or
    /// ARIB STD-B67 ("HLG").
    /// </summary>
    /// <remarks>
    /// This library decodes such frames faithfully but does not tone-map them: the CPU converter treats them
    /// as BT.709 and says so once. Use this flag to decide whether to warn, or to run a tone-mapping shader
    /// of your own.
    /// </remarks>
    public bool IsHighDynamicRange =>
        Transfer == VideoTransferCharacteristics.SmpteSt2084 || Transfer == VideoTransferCharacteristics.AribStdB67;

    /// <summary>
    /// Returns a copy with every unspecified field replaced by the concrete choice this library makes for a
    /// frame of the given height.
    /// </summary>
    /// <param name="frameHeight">
    /// The frame's visible height in pixels. It decides the fallback matrix: standard-definition content
    /// (480 lines or fewer) falls back to BT.601, anything taller to BT.709 - the long-standing convention.
    /// </param>
    /// <returns>A value in which no field reads <c>Unspecified</c> or <c>Unknown</c>.</returns>
    public VideoColorInfo Resolve(int frameHeight)
    {
        VideoColorPrimaries primaries = Primaries;
        if (primaries == VideoColorPrimaries.Unspecified)
        {
            primaries = frameHeight > 0 && frameHeight <= 480
                ? VideoColorPrimaries.Smpte170M
                : VideoColorPrimaries.Bt709;
        }

        VideoTransferCharacteristics transfer = Transfer;
        if (transfer == VideoTransferCharacteristics.Unspecified) transfer = VideoTransferCharacteristics.Bt709;

        VideoMatrixCoefficients matrix = Matrix;
        if (matrix == VideoMatrixCoefficients.Unspecified)
        {
            matrix = frameHeight > 0 && frameHeight <= 480
                ? VideoMatrixCoefficients.Smpte170M
                : VideoMatrixCoefficients.Bt709;
        }

        VideoColorRange range = Range == VideoColorRange.Unspecified ? VideoColorRange.Limited : Range;
        VideoChromaSiting siting = ChromaSiting == VideoChromaSiting.Unknown ? VideoChromaSiting.Vertical : ChromaSiting;

        return new VideoColorInfo(primaries, transfer, matrix, range, siting);
    }

    /// <inheritdoc />
    public bool Equals(VideoColorInfo other) =>
        Primaries == other.Primaries
        && Transfer == other.Transfer
        && Matrix == other.Matrix
        && Range == other.Range
        && ChromaSiting == other.ChromaSiting;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is VideoColorInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine((int)Primaries, (int)Transfer, (int)Matrix, (int)Range, (int)ChromaSiting);

    /// <summary>Compares two colour descriptions for equality.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when every field matches.</returns>
    public static bool operator ==(VideoColorInfo left, VideoColorInfo right) => left.Equals(right);

    /// <summary>Compares two colour descriptions for inequality.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when any field differs.</returns>
    public static bool operator !=(VideoColorInfo left, VideoColorInfo right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Primaries}/{Transfer}/{Matrix}, {Range} range, chroma {ChromaSiting}";
}
