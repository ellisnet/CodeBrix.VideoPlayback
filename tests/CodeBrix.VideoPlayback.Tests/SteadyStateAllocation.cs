using System;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Measures what a warm loop really costs the managed heap, without being at the mercy of the just-in-time
/// compiler's timing.
/// </summary>
/// <remarks>
/// <para>
/// Several tests in this repository assert that a loop allocates EXACTLY nothing - not "a little", none -
/// because that is the guarantee the frame pool, the frame object and the presenter are designed around. The
/// measurement is <see cref="GC.GetAllocatedBytesForCurrentThread" /> before and after, and the trouble with
/// it is that it counts everything the THREAD allocated, including the runtime's own work.
/// </para>
/// <para>
/// TIERED COMPILATION IS THE PROBLEM. A loop that has run a few dozen times is still running tier-0 code;
/// somewhere around the thirtieth call the runtime promotes it, and the promotion - and the on-stack
/// replacement that goes with it - charges a few kilobytes to the measuring thread. A warm-up loop makes that
/// unlikely rather than impossible, so a suite that asserts zero fails perhaps one run in eight, on a busy
/// machine, in a different test each time. Measured here on 2026-08-29; the failure was
/// "Expected value to be 0L, but found 3256L".
/// </para>
/// <para>
/// THE FIX IS TO MEASURE THE STEADY STATE, WHICH IS WHAT THE GUARANTEE IS ABOUT. Run the loop several times
/// and keep the SMALLEST measurement. A tier-up happens once and spoils one pass; something that really
/// allocates per frame spoils every pass, so the smallest is still non-zero and the test still fails. The
/// assertion stays exactly as strict as it was.
/// </para>
/// </remarks>
public static class SteadyStateAllocation
{
    /// <summary>How many times a loop is measured before the smallest measurement is taken.</summary>
    public const int DefaultAttempts = 5;

    /// <summary>Runs a loop several times and returns the fewest bytes any one run allocated.</summary>
    /// <param name="loop">The loop to measure. It is run <paramref name="attempts" /> times.</param>
    /// <param name="attempts">How many measurements to take. At least one.</param>
    /// <returns>
    /// The smallest number of bytes allocated on this thread by one run of the loop. The loop is always run
    /// exactly <paramref name="attempts" /> times, so a caller may assert on counters the loop advances.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempts" /> is less than one.</exception>
    public static long MeasureSmallest(Action loop, int attempts = DefaultAttempts)
    {
        if (loop == null) throw new ArgumentNullException(nameof(loop));
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "Measure at least once.");

        long smallest = long.MaxValue;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            loop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // Every attempt is run, always: a caller that also asserts a rent count or a frame count needs
            // the number of passes to be a fixed number rather than "however many it took".
            if (allocated < smallest) smallest = allocated;
        }

        return smallest;
    }
}
