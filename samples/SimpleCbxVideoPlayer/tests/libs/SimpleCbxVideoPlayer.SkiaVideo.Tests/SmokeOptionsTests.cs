using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using System;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class SmokeOptionsTests
{
    [Fact]
    public void TryParse_of_an_ordinary_launch_is_not_a_smoke_run()
    {
        //Act
        var read = SmokeOptions.TryParse([], out var options, out var error);

        //Assert
        read.Should().BeTrue();
        options.IsSmokeRun.Should().BeFalse();
        error.Should().Be(string.Empty);
    }

    [Fact]
    public void TryParse_reads_the_file_the_snapshot_and_the_render_path()
    {
        //Act
        var read = SmokeOptions.TryParse(
            ["--smoke", "MKV/landscape_hd.mkv", "--snapshot", "/tmp/frame.png", "--render-path", "cpu", "--exit"],
            out var options,
            out var error);

        //Assert
        read.Should().BeTrue();
        error.Should().Be(string.Empty);
        options.IsSmokeRun.Should().BeTrue();
        options.VideoName.Should().Be("MKV/landscape_hd.mkv");
        options.SnapshotPath.Should().Be("/tmp/frame.png");
        options.RenderPath.Should().Be(VideoRenderPathOption.Cpu);
    }

    [Fact]
    public void TryParse_reads_repeated_lookup_tables_in_order()
    {
        //Act
        SmokeOptions.TryParse(
            ["--smoke", "clip.mkv", "--lut", "sepia_33.cube@40", "--lut", "cool_33.cube"],
            out var options,
            out _);

        //Assert
        options.Luts.Count.Should().Be(2);
        options.Luts[0].Name.Should().Be("sepia_33.cube");
        options.Luts[0].ApplyAtPercent.Should().Be(40);
        options.Luts[1].Name.Should().Be("cool_33.cube");
        options.Luts[1].ApplyAtPercent.Should().Be(40);
    }

    [Fact]
    public void TryParse_reads_a_percentage_written_with_an_at_sign()
    {
        //Act
        SmokeOptions.TryParse(["--smoke", "clip.mkv", "--lut", "sepia_33.cube@85"], out var options, out _);

        //Assert
        options.Luts.Count.Should().Be(1);
        options.Luts[0].Name.Should().Be("sepia_33.cube");
        options.Luts[0].ApplyAtPercent.Should().Be(85);
    }

    [Fact]
    public void TryParse_refuses_a_lookup_table_whose_percentage_is_not_a_number()
    {
        //Act
        var read = SmokeOptions.TryParse(
            ["--smoke", "clip.mkv", "--lut", "sepia_33.cube@forty"],
            out var options,
            out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("forty");
        options.Luts.Count.Should().Be(0);
    }

    [Fact]
    public void TryParse_refuses_a_lut_switch_with_no_value()
    {
        //Act
        var read = SmokeOptions.TryParse(["--smoke", "clip.mkv", "--lut"], out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("--lut");
    }

    [Fact]
    public void a_command_line_that_was_understood_carries_no_parse_error()
    {
        //Act
        SmokeOptions.TryParse(["--smoke", "clip.mkv", "--lut", "sepia_33.cube@40"], out var options, out _);

        //Assert
        options.ParseError.Should().Be(string.Empty);
    }

    [Fact]
    public void TryParse_reads_the_timings_and_the_switches()
    {
        //Act
        SmokeOptions.TryParse(
            ["--smoke", "clip.mkv", "--seconds", "3.5", "--snapshot-at", "0.5", "--until-ended", "--no-audio"],
            out var options,
            out _);

        //Assert
        options.PlayDuration.Should().Be(TimeSpan.FromSeconds(3.5));
        options.SnapshotPosition.Should().Be(TimeSpan.FromSeconds(0.5));
        options.PlayUntilEnded.Should().BeTrue();
        options.PlayAudio.Should().BeFalse();
    }

    [Fact]
    public void TryParse_refuses_a_switch_with_no_value()
    {
        //Act
        var read = SmokeOptions.TryParse(["--smoke"], out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("--smoke");
    }

    [Fact]
    public void TryParse_refuses_a_render_path_it_does_not_know()
    {
        //Act
        var read = SmokeOptions.TryParse(["--smoke", "clip.mkv", "--render-path", "quantum"], out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("quantum");
    }

    [Fact]
    public void TryParse_reads_the_bake_and_compare_switches()
    {
        //Act
        var read = SmokeOptions.TryParse(
            ["--smoke", "clip.mkv", "--bake", "/tmp/chain.cube", "--compare", "/tmp/other.png",
             "--compare-tolerance", "4"],
            out var options,
            out var error);

        //Assert
        read.Should().BeTrue();
        error.Should().Be(string.Empty);
        options.BakePath.Should().Be("/tmp/chain.cube");
        options.ComparePath.Should().Be("/tmp/other.png");
        options.CompareTolerance.Should().Be(4);
    }

    [Fact]
    public void the_compare_tolerance_defaults_to_two_colour_levels()
    {
        //Act
        SmokeOptions.TryParse(["--smoke", "clip.mkv"], out var options, out _);

        //Assert
        options.CompareTolerance.Should().Be(SmokeOptions.DefaultCompareTolerance);
        options.CompareTolerance.Should().Be(2);
        options.BakePath.Should().Be(string.Empty);
        options.ComparePath.Should().Be(string.Empty);
    }

    [Fact]
    public void TryParse_refuses_a_tolerance_that_is_not_a_number_of_levels()
    {
        //Act
        var read = SmokeOptions.TryParse(
            ["--smoke", "clip.mkv", "--compare-tolerance", "loose"],
            out _,
            out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("loose");
    }

    [Fact]
    public void TryParse_refuses_a_tolerance_outside_the_colour_range()
    {
        //Assert
        SmokeOptions.TryParse(["--smoke", "clip.mkv", "--compare-tolerance", "-1"], out _, out _)
            .Should().BeFalse();
        SmokeOptions.TryParse(["--smoke", "clip.mkv", "--compare-tolerance", "999"], out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_refuses_a_switch_it_does_not_know()
    {
        //Act
        var read = SmokeOptions.TryParse(["--smoke", "clip.mkv", "--turbo"], out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("--turbo");
    }

    [Fact]
    public void the_defaults_are_two_seconds_of_play_and_a_capture_at_one_second()
    {
        //Act
        SmokeOptions.TryParse(["--smoke", "clip.mkv"], out var options, out _);

        //Assert
        options.PlayDuration.Should().Be(TimeSpan.FromSeconds(2));
        options.SnapshotPosition.Should().Be(TimeSpan.FromSeconds(1));
        options.RenderPath.Should().Be(VideoRenderPathOption.GpuAuto);
        options.PlayAudio.Should().BeTrue();
    }
}
