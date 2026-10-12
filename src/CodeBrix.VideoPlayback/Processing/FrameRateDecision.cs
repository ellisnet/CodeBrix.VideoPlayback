using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// What a <see cref="FrameRateResampler" /> decided for one source frame: how many output frames it becomes
/// (0 = dropped, 1 = kept, more = duplicated) and which output slot the first of them fills.
/// </summary>
public readonly struct FrameRateDecision : IEquatable<FrameRateDecision>
{
    internal FrameRateDecision(long sourceIndex, TimeSpan sourceTimestamp, long firstOutputIndex, long outputCount)
    {
        SourceIndex = sourceIndex;
        SourceTimestamp = sourceTimestamp;
        FirstOutputIndex = firstOutputIndex;
        OutputCount = outputCount;
    }

    /// <summary>"No decision yet" - what the first <see cref="FrameRateResampler.Push(TimeSpan)" /> returns.</summary>
    public static FrameRateDecision None => new FrameRateDecision(-1, TimeSpan.Zero, 0, 0);

    /// <summary>True when this decision is about a frame; false for <see cref="None" />.</summary>
    public bool HasFrame => SourceIndex >= 0;

    /// <summary>The zero-based index of the source frame, in push order; -1 for <see cref="None" />.</summary>
    public long SourceIndex { get; }

    /// <summary>The source frame's timestamp as it was pushed (rounded to a tick when it came in another time base).</summary>
    public TimeSpan SourceTimestamp { get; }

    /// <summary>
    /// The output slot the first emitted copy fills; the copies fill consecutive slots from here. Output slot
    /// <c>n</c> has the timestamp <see cref="FrameRateResampler.GetOutputTimestamp" /> gives for <c>n</c>.
    /// Meaningful only when <see cref="OutputCount" /> is greater than zero.
    /// </summary>
    public long FirstOutputIndex { get; }

    /// <summary>How many output frames this source frame becomes: 0 when dropped, 1 normally, more when duplicated.</summary>
    public long OutputCount { get; }

    /// <summary>True when the frame is dropped (another source frame landed in its only slot first).</summary>
    public bool IsDropped => HasFrame && OutputCount == 0;

    /// <summary>True when the frame is emitted more than once to fill slots no source frame reached.</summary>
    public bool IsDuplicated => OutputCount > 1;

    /// <inheritdoc />
    public bool Equals(FrameRateDecision other) =>
        SourceIndex == other.SourceIndex
        && SourceTimestamp == other.SourceTimestamp
        && FirstOutputIndex == other.FirstOutputIndex
        && OutputCount == other.OutputCount;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is FrameRateDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SourceIndex, SourceTimestamp, FirstOutputIndex, OutputCount);

    /// <summary>Compares two decisions for equality.</summary>
    /// <param name="left">The first decision.</param>
    /// <param name="right">The second decision.</param>
    /// <returns>True when every field is equal.</returns>
    public static bool operator ==(FrameRateDecision left, FrameRateDecision right) => left.Equals(right);

    /// <summary>Compares two decisions for inequality.</summary>
    /// <param name="left">The first decision.</param>
    /// <param name="right">The second decision.</param>
    /// <returns>True when any field differs.</returns>
    public static bool operator !=(FrameRateDecision left, FrameRateDecision right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        !HasFrame
            ? "(no decision)"
            : OutputCount == 0
                ? $"source #{SourceIndex} @ {SourceTimestamp}: dropped"
                : $"source #{SourceIndex} @ {SourceTimestamp}: {OutputCount} output(s) from slot {FirstOutputIndex}";
}
