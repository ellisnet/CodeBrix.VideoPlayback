using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Containers;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the streamable-profile rules, which are what makes a "CodeBrix Video" file one rather than merely
/// a WebM one.
/// </summary>
/// <remarks>
/// The rules used to live inside the <c>cbvinfo</c> tool, where nothing but a shell script could reach them.
/// They are in the library now, so the tool, the authoring library and these tests all judge a file by the
/// same code - and the rendered report is asserted here byte for byte, because a pipeline records it.
/// </remarks>
public class StreamableProfileTests
{
    [Fact]
    public void A_well_laid_out_webm_passes_every_rule()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        report.Passes.Should().BeTrue();
        report.Failed.Should().Be(0);
        report.Verdict.Should().Be("passes the profile");
    }

    [Fact]
    public void A_file_whose_cues_sit_at_the_end_fails_exactly_one_rule()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus-cues-at-end.webm");

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        report.Passes.Should().BeFalse();
        report.Failed.Should().Be(1);
        report.Verdict.Should().Be("DOES NOT pass the profile");

        List<string> failures = new List<string>();
        foreach (StreamableProfileRule rule in report.FailedRules()) failures.Add(rule.Rule);
        failures.Count.Should().Be(1);
        failures[0].Should().Be("cues sit before the first cluster");
    }

    [Fact]
    public void A_bespoke_file_is_judged_by_its_own_three_layout_rules()
    {
        //Arrange
        string path = TestAssets.Path("av1-vorbis.cbv");

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        report.Passes.Should().BeTrue();

        List<string> rules = new List<string>();
        foreach (StreamableProfileRule rule in report.Rules) rules.Add(rule.Rule);

        rules.Should().Contain("the file carries an index");
        rules.Should().Contain("the index sits before the chunks");
        rules.Should().Contain("the header states a duration");
        rules.Should().NotContain("cues sit before the first cluster");
    }

    [Fact]
    public void A_file_with_a_codec_the_profile_does_not_allow_fails_on_it()
    {
        //Arrange
        string path = TestAssets.Path("raw-vorbis.cbv");

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        report.Passes.Should().BeFalse();

        List<string> failures = new List<string>();
        foreach (StreamableProfileRule rule in report.FailedRules()) failures.Add(rule.Rule);
        failures.Should().Contain("video codec is AV1");
    }

    [Fact]
    public void The_rendered_report_is_the_text_the_tool_prints()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");

        //Act
        string rendered = StreamableProfile.EvaluateFile(path).ToString();
        string[] lines = rendered.Replace("\r\n", "\n").Split('\n');

        //Assert
        lines[0].Should().Be("streamable profile");
        lines[1].Should().Be("  [pass] video codec is AV1 - 'av01'");
        lines[lines.Length - 2].Should().Be(string.Empty);
        lines[lines.Length - 1].Should().Be("result      passes the profile");
    }

    [Fact]
    public void A_rule_renders_its_tag_its_text_and_its_detail()
    {
        //Arrange
        StreamableProfileRule pass = new StreamableProfileRule("a rule", StreamableProfileOutcome.Pass);
        StreamableProfileRule warn = new StreamableProfileRule("a rule", StreamableProfileOutcome.Warn, "why");
        StreamableProfileRule fail = new StreamableProfileRule("a rule", StreamableProfileOutcome.Fail, "why not");

        //Act
        //Assert
        pass.ToString().Should().Be("[pass] a rule");
        warn.ToString().Should().Be("[warn] a rule - why");
        fail.ToString().Should().Be("[FAIL] a rule - why not");
        pass.Passed.Should().BeTrue();
        fail.Passed.Should().BeFalse();
    }

    [Fact]
    public void A_warning_does_not_cost_a_file_its_pass_but_does_change_the_verdict()
    {
        //Arrange
        List<StreamableProfileRule> rules = new List<StreamableProfileRule>
        {
            new StreamableProfileRule("something required", StreamableProfileOutcome.Pass),
            new StreamableProfileRule("something recommended", StreamableProfileOutcome.Warn, "8 bits would be better"),
        };

        //Act
        StreamableProfileReport report = new StreamableProfileReport(rules);

        //Assert
        report.Passes.Should().BeTrue();
        report.Warnings.Should().Be(1);
        report.Verdict.Should().Be("passes the profile, with warnings");
    }

    [Fact]
    public void A_rule_needs_a_name_and_a_report_needs_its_rules()
    {
        //Act
        //Assert
        Assert.Throws<ArgumentException>(() => new StreamableProfileRule("  ", StreamableProfileOutcome.Pass));
        Assert.Throws<ArgumentNullException>(() => new StreamableProfileReport(null));
    }

    [Fact]
    public void Evaluate_needs_a_reader()
    {
        //Act
        //Assert
        Assert.Throws<ArgumentNullException>(() => StreamableProfile.Evaluate(null, 0));
        Assert.Throws<ArgumentNullException>(() => StreamableProfile.CountOutOfOrderPackets(null));
    }

    [Fact]
    public void CountOutOfOrderPackets_walks_the_file_and_finds_nothing_wrong_with_a_good_one()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");
        using IMediaContainerReader reader = MediaContainers.Open(path);

        //Act
        int outOfOrder = StreamableProfile.CountOutOfOrderPackets(reader);

        //Assert
        outOfOrder.Should().Be(0);
    }

    [Fact]
    public void EvaluateFile_refuses_a_file_that_is_not_a_container_this_library_reads()
    {
        //Arrange
        string path = Path.Combine(Path.GetTempPath(), "not-a-container-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        try
        {
            //Act
            VideoPlaybackException failure = Assert.Throws<VideoPlaybackException>(
                () => StreamableProfile.EvaluateFile(path));

            //Assert
            failure.Message.Should().Contain("CBVF");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
