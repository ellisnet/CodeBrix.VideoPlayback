using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Converts a stream of source frame timestamps to a constant output frame rate by DROPPING and DUPLICATING
/// whole frames - the timing half of a frame-rate change; it never touches a pixel. It mirrors FFmpeg's
/// <c>fps=</c> filter with its defaults (<c>round=near</c>, <c>eof_action=round</c>, no <c>start_time</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The output clock.</b> Output frame <c>n</c> has the timestamp <c>n * den / num</c> seconds exactly, for
/// the target rate <c>num/den</c> - computed from <c>n</c> each time, never accumulated, so it cannot drift.
/// </para>
/// <para>
/// <b>The rule.</b> Every source frame is given an output SLOT: its timestamp times the target rate, rounded
/// to the nearest integer (halves away from zero). The first source frame's slot is the first output slot -
/// slot 0 when the stream starts at zero. Each source frame then fills every slot from the next unfilled one
/// up to, but not including, the NEXT source frame's slot:
/// </para>
/// <list type="bullet">
///   <item><description>
///     two source frames landing in the same slot: the earlier one fills nothing and is DROPPED (the later one
///     wins, as in FFmpeg);
///   </description></item>
///   <item><description>
///     a gap of several slots before the next source frame: the frame is DUPLICATED into all of them.
///   </description></item>
/// </list>
/// <para>
/// So 60 to 30 drops every other frame, 15 to 30 emits each frame twice, 30 to 24 drops one frame in five, 24
/// to 30 duplicates one in four, and 29.97 to 30 duplicates one frame in a thousand.
/// </para>
/// <para>
/// <b>Streaming.</b> A frame's fate depends on the NEXT frame's timestamp, so <see cref="Push(TimeSpan)" />
/// returns the decision for the PREVIOUS frame pushed (the first push returns <see cref="FrameRateDecision.None" />),
/// and <see cref="Complete()" /> decides the last. The last frame fills slots up to the slot of the stream's
/// END - its known end time (the last frame's timestamp plus its duration, which is what FFmpeg uses), or, when
/// the end is not known, the last timestamp plus one source frame interval if a source rate was given, or
/// else exactly one slot.
/// </para>
/// <para>
/// <b>Exact time bases.</b> A <see cref="TimeSpan" /> is a whole number of 100-nanosecond ticks, so a
/// 29.97 frame-per-second stream's timestamps are rounded on the way in, and a timestamp that should sit
/// EXACTLY halfway between two output slots can land a tick to one side. Pushing the stream's own integer
/// timestamps and time base (<see cref="Push(long, long, long)" />) avoids that entirely; the arithmetic is
/// done in 128-bit integers.
/// </para>
/// </remarks>
public sealed class FrameRateResampler
{
    private long sourceCount;
    private long nextSlot;
    private bool hasPending;
    private TimeSpan pendingTimestamp;
    private long pendingSlot;
    private Int128 pendingNumerator;
    private Int128 pendingDenominator;

    /// <summary>Creates a resampler for a target rate, with the source rate unknown.</summary>
    /// <param name="targetNumerator">The target rate's numerator, e.g. 30000.</param>
    /// <param name="targetDenominator">The target rate's denominator, e.g. 1001.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate term is not greater than zero.</exception>
    public FrameRateResampler(int targetNumerator, int targetDenominator)
        : this(targetNumerator, targetDenominator, 0, 0)
    {
    }

    /// <summary>Creates a resampler for a target rate, knowing the source's nominal rate.</summary>
    /// <param name="targetNumerator">The target rate's numerator, e.g. 30000.</param>
    /// <param name="targetDenominator">The target rate's denominator, e.g. 1001.</param>
    /// <param name="sourceNumerator">The source rate's numerator, or 0 when unknown.</param>
    /// <param name="sourceDenominator">The source rate's denominator, or 0 when unknown.</param>
    /// <exception cref="ArgumentOutOfRangeException">A target term is not greater than zero, or a source term is negative or only one of them is zero.</exception>
    /// <remarks>The source rate only decides how long the LAST frame lasts when the stream's end is not known.</remarks>
    public FrameRateResampler(int targetNumerator, int targetDenominator, int sourceNumerator, int sourceDenominator)
    {
        ProcessingGuard.Dimension(targetNumerator, nameof(targetNumerator));
        ProcessingGuard.Dimension(targetDenominator, nameof(targetDenominator));
        if (sourceNumerator < 0 || sourceDenominator < 0 || ((sourceNumerator == 0) != (sourceDenominator == 0)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceNumerator),
                $"{sourceNumerator}/{sourceDenominator}",
                $"The source rate must be positive in both terms, or 0/0 for unknown; it is {sourceNumerator}/{sourceDenominator}.");
        }

        TargetNumerator = targetNumerator;
        TargetDenominator = targetDenominator;
        SourceNumerator = sourceNumerator;
        SourceDenominator = sourceDenominator;
    }

    /// <summary>The target rate's numerator.</summary>
    public int TargetNumerator { get; }

    /// <summary>The target rate's denominator.</summary>
    public int TargetDenominator { get; }

    /// <summary>The source rate's numerator; 0 when unknown.</summary>
    public int SourceNumerator { get; }

    /// <summary>The source rate's denominator; 0 when unknown.</summary>
    public int SourceDenominator { get; }

    /// <summary>True when a source rate was given.</summary>
    public bool HasSourceRate => SourceNumerator > 0;

    /// <summary>How many source frames have been pushed since construction or <see cref="Reset" />.</summary>
    public long SourceFramesPushed => sourceCount;

    /// <summary>The next output slot not yet filled (the first source frame's slot until the first decision).</summary>
    public long NextOutputIndex => nextSlot;

    /// <summary>Plans a whole stream at once.</summary>
    /// <param name="timestamps">The source timestamps, in order (non-decreasing).</param>
    /// <param name="streamEnd">The stream's end time (last timestamp plus its duration), or null when unknown.</param>
    /// <param name="targetNumerator">The target rate's numerator.</param>
    /// <param name="targetDenominator">The target rate's denominator.</param>
    /// <param name="sourceNumerator">The source rate's numerator, or 0 when unknown.</param>
    /// <param name="sourceDenominator">The source rate's denominator, or 0 when unknown.</param>
    /// <returns>One decision per source frame, in order.</returns>
    /// <exception cref="ArgumentException">A timestamp goes backwards, or the end is before the last timestamp.</exception>
    public static FrameRateDecision[] Plan(
        ReadOnlySpan<TimeSpan> timestamps,
        TimeSpan? streamEnd,
        int targetNumerator,
        int targetDenominator,
        int sourceNumerator = 0,
        int sourceDenominator = 0)
    {
        FrameRateResampler resampler = new FrameRateResampler(targetNumerator, targetDenominator, sourceNumerator, sourceDenominator);
        FrameRateDecision[] decisions = new FrameRateDecision[timestamps.Length];
        for (int i = 0; i < timestamps.Length; i++)
        {
            FrameRateDecision decided = resampler.Push(timestamps[i]);
            if (decided.HasFrame) decisions[i - 1] = decided;
        }

        if (timestamps.Length > 0)
        {
            decisions[timestamps.Length - 1] = streamEnd.HasValue ? resampler.Complete(streamEnd.Value) : resampler.Complete();
        }

        return decisions;
    }

    /// <summary>The exact timestamp of output slot <paramref name="outputIndex" />, rounded to the nearest tick.</summary>
    /// <param name="outputIndex">The output slot.</param>
    /// <returns><c>outputIndex * den / num</c> seconds, to the nearest 100-nanosecond tick (halves away from zero).</returns>
    public TimeSpan GetOutputTimestamp(long outputIndex) =>
        new TimeSpan((long)RoundDivide((Int128)outputIndex * TargetDenominator * TimeSpan.TicksPerSecond, TargetNumerator));

    /// <summary>The output slot a timestamp falls in: its time times the target rate, rounded to nearest (halves away from zero).</summary>
    /// <param name="timestamp">The timestamp.</param>
    /// <returns>The slot.</returns>
    public long GetOutputIndex(TimeSpan timestamp) => SlotOfFraction(timestamp.Ticks, TimeSpan.TicksPerSecond);

    /// <summary>Pushes the next source frame's timestamp and returns the decision for the PREVIOUS one.</summary>
    /// <param name="timestamp">The timestamp; not earlier than the previous one.</param>
    /// <returns>The previous frame's decision, or <see cref="FrameRateDecision.None" /> on the first push.</returns>
    /// <exception cref="ArgumentException">The timestamp is earlier than the previous one.</exception>
    public FrameRateDecision Push(TimeSpan timestamp) => PushCore(timestamp.Ticks, TimeSpan.TicksPerSecond, timestamp);

    /// <summary>
    /// Pushes the next source frame's timestamp in its own time base - exact, with no tick rounding - and returns
    /// the decision for the PREVIOUS one.
    /// </summary>
    /// <param name="timestamp">The timestamp in time-base units.</param>
    /// <param name="timeBaseNumerator">The time base's numerator (e.g. 1001 for 1001/30000).</param>
    /// <param name="timeBaseDenominator">The time base's denominator (e.g. 30000).</param>
    /// <returns>The previous frame's decision, or <see cref="FrameRateDecision.None" /> on the first push.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A time-base term is not greater than zero.</exception>
    /// <exception cref="ArgumentException">The timestamp is earlier than the previous one.</exception>
    public FrameRateDecision Push(long timestamp, long timeBaseNumerator, long timeBaseDenominator)
    {
        CheckTimeBase(timeBaseNumerator, timeBaseDenominator);
        Int128 numerator = (Int128)timestamp * timeBaseNumerator;
        TimeSpan asTicks = new TimeSpan((long)RoundDivide(numerator * TimeSpan.TicksPerSecond, timeBaseDenominator));
        return PushCore(numerator, timeBaseDenominator, asTicks);
    }

    /// <summary>Decides the last frame with the stream's end unknown (see the class remarks for how long it then lasts).</summary>
    /// <returns>The last frame's decision, or <see cref="FrameRateDecision.None" /> when nothing is pending.</returns>
    public FrameRateDecision Complete()
    {
        if (!hasPending) return FrameRateDecision.None;

        long endSlot;
        if (HasSourceRate)
        {
            // last + den/num seconds, as one fraction: (n * num + den * d) / (d * num).
            Int128 numerator = (pendingNumerator * SourceNumerator) + ((Int128)SourceDenominator * pendingDenominator);
            Int128 denominator = pendingDenominator * SourceNumerator;
            endSlot = SlotOfFraction(numerator, denominator);
        }
        else
        {
            endSlot = pendingSlot + 1;
        }

        return Finish(endSlot);
    }

    /// <summary>Decides the last frame, given the stream's end time.</summary>
    /// <param name="streamEnd">The end of the stream: the last frame's timestamp plus its duration.</param>
    /// <returns>The last frame's decision, or <see cref="FrameRateDecision.None" /> when nothing is pending.</returns>
    /// <exception cref="ArgumentException">The end is earlier than the last timestamp.</exception>
    public FrameRateDecision Complete(TimeSpan streamEnd) => CompleteCore(streamEnd.Ticks, TimeSpan.TicksPerSecond);

    /// <summary>Decides the last frame, given the stream's end time in a time base - exact, with no tick rounding.</summary>
    /// <param name="streamEnd">The end of the stream in time-base units.</param>
    /// <param name="timeBaseNumerator">The time base's numerator.</param>
    /// <param name="timeBaseDenominator">The time base's denominator.</param>
    /// <returns>The last frame's decision, or <see cref="FrameRateDecision.None" /> when nothing is pending.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A time-base term is not greater than zero.</exception>
    /// <exception cref="ArgumentException">The end is earlier than the last timestamp.</exception>
    public FrameRateDecision Complete(long streamEnd, long timeBaseNumerator, long timeBaseDenominator)
    {
        CheckTimeBase(timeBaseNumerator, timeBaseDenominator);
        return CompleteCore((Int128)streamEnd * timeBaseNumerator, timeBaseDenominator);
    }

    /// <summary>Forgets every frame pushed, ready for a new stream with the same rates.</summary>
    public void Reset()
    {
        sourceCount = 0;
        nextSlot = 0;
        hasPending = false;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"fps {TargetNumerator}/{TargetDenominator}" + (HasSourceRate ? $" from {SourceNumerator}/{SourceDenominator}" : string.Empty);

    private FrameRateDecision PushCore(Int128 numerator, Int128 denominator, TimeSpan asTicks)
    {
        if (hasPending && numerator * pendingDenominator < pendingNumerator * denominator)
        {
            throw new ArgumentException(
                $"timestamp {asTicks} is earlier than the previous one ({pendingTimestamp}); source timestamps must not go backwards.",
                "timestamp");
        }

        long slot = SlotOfFraction(numerator, denominator);
        FrameRateDecision decided = FrameRateDecision.None;

        if (!hasPending)
        {
            nextSlot = slot;
        }
        else
        {
            decided = Decide(slot);
        }

        hasPending = true;
        pendingTimestamp = asTicks;
        pendingSlot = slot;
        pendingNumerator = numerator;
        pendingDenominator = denominator;
        sourceCount++;
        return decided;
    }

    private FrameRateDecision CompleteCore(Int128 numerator, Int128 denominator)
    {
        if (!hasPending) return FrameRateDecision.None;

        if (numerator * pendingDenominator < pendingNumerator * denominator)
        {
            throw new ArgumentException(
                $"The stream end is earlier than the last timestamp ({pendingTimestamp}).",
                "streamEnd");
        }

        return Finish(SlotOfFraction(numerator, denominator));
    }

    private FrameRateDecision Finish(long endSlot)
    {
        FrameRateDecision decided = Decide(endSlot);
        hasPending = false;
        return decided;
    }

    /// <summary>The pending frame fills every slot from the next unfilled one up to (not including) <paramref name="followingSlot" />.</summary>
    private FrameRateDecision Decide(long followingSlot)
    {
        long first = nextSlot;
        long count = followingSlot > first ? followingSlot - first : 0;
        if (followingSlot > nextSlot) nextSlot = followingSlot;
        return new FrameRateDecision(sourceCount - 1, pendingTimestamp, first, count);
    }

    /// <summary>round(seconds * num / den) where seconds = numerator / denominator.</summary>
    private long SlotOfFraction(Int128 numerator, Int128 denominator) =>
        (long)RoundDivide(numerator * TargetNumerator, denominator * TargetDenominator);

    private static Int128 RoundDivide(Int128 numerator, Int128 denominator)
    {
        // Halves away from zero, as FFmpeg's AV_ROUND_NEAR_INF.
        Int128 doubled = (numerator * 2) + (numerator >= 0 ? denominator : -denominator);
        return doubled / (denominator * 2);
    }

    private static void CheckTimeBase(long numerator, long denominator)
    {
        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException("timeBaseNumerator", numerator, $"timeBaseNumerator must be greater than zero; it is {numerator}.");
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException("timeBaseDenominator", denominator, $"timeBaseDenominator must be greater than zero; it is {denominator}.");
        }
    }
}
