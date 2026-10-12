using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Measures <see cref="FrameRateResampler" /> against FFmpeg's <c>fps=</c> filter: a generated clip at the
/// source rate goes through the filter into a frame listing, and the managed planner must produce the SAME
/// number of output frames at the SAME output slots.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class FrameRateResamplerTests
{
    [Theory]
    [InlineData(30, 1, 24, 1, "2")]
    [InlineData(24, 1, 30, 1, "2")]
    [InlineData(30000, 1001, 30, 1, "2")]
    [InlineData(30, 1, 30000, 1001, "2")]
    [InlineData(60, 1, 30, 1, "2")]
    [InlineData(15, 1, 30, 1, "2")]
    [InlineData(25, 1, 24000, 1001, "1.5")]
    public void Plan_produces_the_frames_ffmpeg_fps_produces_at_the_same_slots(
        int sourceNumerator,
        int sourceDenominator,
        int targetNumerator,
        int targetDenominator,
        string duration)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        string source = $"testsrc2=size=16x16:rate={sourceNumerator}/{sourceDenominator}:duration={duration}";
        int sourceFrames = FfmpegProcessingOracle.FrameTimestamps(source, null).Count;

        //Act
        List<long> expected = FfmpegProcessingOracle.FrameTimestamps(source, $"fps={targetNumerator}/{targetDenominator}");

        FrameRateResampler resampler = new FrameRateResampler(targetNumerator, targetDenominator);
        List<FrameRateDecision> decisions = new List<FrameRateDecision>();
        for (int n = 0; n < sourceFrames; n++)
        {
            FrameRateDecision decided = resampler.Push(n, sourceDenominator, sourceNumerator);
            if (decided.HasFrame) decisions.Add(decided);
        }

        decisions.Add(resampler.Complete(sourceFrames, sourceDenominator, sourceNumerator));

        List<long> actual = new List<long>();
        foreach (FrameRateDecision decision in decisions)
        {
            for (long copy = 0; copy < decision.OutputCount; copy++) actual.Add(decision.FirstOutputIndex + copy);
        }

        //Assert
        actual.Should().Equal(expected);
        decisions.Count.Should().Be(sourceFrames);
    }

    [Fact]
    public void Plan_drops_every_other_frame_from_60_to_30()
    {
        //Arrange - the stream's own time base (1/60), because every odd frame sits EXACTLY on a half slot
        FrameRateResampler resampler = new FrameRateResampler(30, 1);
        FrameRateDecision[] decisions = new FrameRateDecision[12];

        //Act
        for (int n = 0; n < 12; n++)
        {
            FrameRateDecision decided = resampler.Push(n, 1, 60);
            if (decided.HasFrame) decisions[n - 1] = decided;
        }

        decisions[11] = resampler.Complete(12, 1, 60);

        //Assert - slot n/2 rounds halves up, so odd frame 2k-1 lands in slot k with frame 2k, which wins
        for (int n = 0; n < 12; n++)
        {
            decisions[n].OutputCount.Should().Be(n % 2 == 0 ? 1 : 0);
            decisions[n].IsDropped.Should().Be(n % 2 == 1);
        }
    }

    [Fact]
    public void Plan_duplicates_each_frame_from_15_to_30()
    {
        //Arrange
        TimeSpan[] timestamps = Timestamps(15, 1, 10);

        //Act
        FrameRateDecision[] decisions = FrameRateResampler.Plan(timestamps, TimeSpan.FromSeconds(10 / 15d), 30, 1);

        //Assert
        for (int n = 0; n < 10; n++)
        {
            decisions[n].OutputCount.Should().Be(2);
            decisions[n].FirstOutputIndex.Should().Be(2 * n);
            decisions[n].IsDuplicated.Should().BeTrue();
        }
    }

    [Fact]
    public void Plan_with_an_unknown_end_uses_the_source_rate_or_else_one_slot()
    {
        //Arrange
        TimeSpan[] timestamps = Timestamps(15, 1, 4);

        //Act
        FrameRateDecision[] withRate = FrameRateResampler.Plan(timestamps, null, 30, 1, 15, 1);
        FrameRateDecision[] without = FrameRateResampler.Plan(timestamps, null, 30, 1);

        //Assert
        withRate[3].OutputCount.Should().Be(2);
        without[3].OutputCount.Should().Be(1);
    }

    [Fact]
    public void GetOutputTimestamp_is_n_times_den_over_num_without_drift()
    {
        //Arrange
        FrameRateResampler resampler = new FrameRateResampler(30000, 1001);

        //Act
        TimeSpan first = resampler.GetOutputTimestamp(1);
        TimeSpan atThirtyThousand = resampler.GetOutputTimestamp(30000);
        TimeSpan hour = resampler.GetOutputTimestamp(107892);

        //Assert - 1001/30000 s = 333666.67 ticks; 30000 frames = exactly 1001 s; 107892 frames = 3599.9964 s,
        //computed from n each time, so the hour mark carries no accumulated rounding
        first.Ticks.Should().Be(333667);
        atThirtyThousand.Should().Be(TimeSpan.FromSeconds(1001));
        hour.Ticks.Should().Be(35999964000L);
    }

    [Fact]
    public void Push_in_a_time_base_is_exact_where_ticks_would_round_a_half_away()
    {
        //Arrange - frame 500 of 29.97 at 30 fps sits exactly on slot 500.5, which rounds to 501
        FrameRateResampler exact = new FrameRateResampler(30, 1);
        FrameRateResampler ticks = new FrameRateResampler(30, 1);

        //Act - once frame 500 is pushed, frame 499 is decided and the next slot is frame 500's slot
        for (int n = 0; n <= 500; n++)
        {
            exact.Push(n, 1001, 30000);
            ticks.Push(TimeSpan.FromTicks((long)Math.Round(n * 1001 * 1e7 / 30000)));
        }

        //Assert
        exact.NextOutputIndex.Should().Be(501);
        ticks.NextOutputIndex.Should().Be(500);
    }

    [Fact]
    public void Push_refuses_a_timestamp_that_goes_backwards()
    {
        //Arrange
        FrameRateResampler resampler = new FrameRateResampler(30, 1);
        resampler.Push(TimeSpan.FromSeconds(1));

        //Act
        Exception thrown = Record.Exception(() => resampler.Push(TimeSpan.FromSeconds(0.5)));
        Exception badRate = Record.Exception(() => new FrameRateResampler(0, 1));

        //Assert
        thrown.Should().BeOfType<ArgumentException>();
        thrown.Message.Should().Contain("backwards");
        badRate.Should().BeOfType<ArgumentOutOfRangeException>();
        badRate.Message.Should().Contain("targetNumerator");
    }

    private static TimeSpan[] Timestamps(int numerator, int denominator, int count)
    {
        TimeSpan[] timestamps = new TimeSpan[count];
        for (int n = 0; n < count; n++)
        {
            timestamps[n] = TimeSpan.FromTicks((long)Math.Round(n * denominator * 1e7 / numerator));
        }

        return timestamps;
    }
}
