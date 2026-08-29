using System;
using System.IO;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the caption file readers against the golden WebVTT and SubRip files, and against the shapes that
/// trip a lenient parser up.
/// </summary>
public class CaptionFilesTests
{
    [Fact]
    public void The_golden_webvtt_file_reads_every_cue_with_its_settings_and_identifier()
    {
        //Arrange
        string path = TestAssets.Path("captions-en.vtt");

        //Act
        CaptionTrack track = CaptionFiles.ReadWebVttFile(path, 0, "en", "English", CaptionTrackFlags.Default);

        //Assert
        track.CueCount.Should().Be(4);
        track.AreCuesComplete.Should().BeTrue();
        track.Format.Should().Be(CaptionFormat.WebVtt);
        track.Cues[0].Identifier.Should().Be("intro");
        track.Cues[0].Settings.Should().Be("line:90% align:center");
        track.Cues[1].Identifier.Should().Be(string.Empty);
        track.Cues[1].Settings.Should().Be(string.Empty);
        track.Cues[2].Identifier.Should().Be("third-cue");
    }

    [Fact]
    public void The_golden_subrip_file_reads_every_cue()
    {
        //Arrange
        string path = TestAssets.Path("srt-captions.srt");

        //Act
        CaptionTrack track = CaptionFiles.ReadSubRipFile(path, 0, "en");

        //Assert
        track.CueCount.Should().Be(4);
        track.Format.Should().Be(CaptionFormat.SubRip);
        track.Cues[0].Start.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        track.Cues[0].Text.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReadFile_picks_the_parser_from_the_extension()
    {
        //Arrange & Act
        CaptionTrack vtt = CaptionFiles.ReadFile(TestAssets.Path("captions-en.vtt"), 0, "en");
        CaptionTrack srt = CaptionFiles.ReadFile(TestAssets.Path("srt-captions.srt"), 1, "en");

        //Assert
        vtt.Format.Should().Be(CaptionFormat.WebVtt);
        srt.Format.Should().Be(CaptionFormat.SubRip);
    }

    [Fact]
    public void ReadFile_refuses_an_extension_it_does_not_know()
    {
        //Arrange
        string path = Path.Combine(TestAssets.CreateTemporaryDirectory("captions"), "captions.sub");
        File.WriteAllText(path, "not a caption format this library reads");

        //Act
        Action act = () => CaptionFiles.ReadFile(path, 0, "en");

        //Assert
        act.Should().Throw<ArgumentException>().WithMessage("*.vtt*");
    }

    [Fact]
    public void A_webvtt_file_without_the_signature_is_refused()
    {
        //Arrange
        string text = "00:00:00.000 --> 00:00:01.000\nno signature\n";

        //Act
        Action act = () => CaptionFiles.ParseWebVtt(text, 0, "en");

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*WEBVTT*");
    }

    [Fact]
    public void Notes_and_style_blocks_are_stepped_over()
    {
        //Arrange
        string text = "WEBVTT\n\nNOTE this is a comment\nspanning two lines\n\n"
            + "STYLE\n::cue { color: yellow }\n\n"
            + "00:00:01.000 --> 00:00:02.000\nThe only cue\n";

        //Act
        CaptionTrack track = CaptionFiles.ParseWebVtt(text, 0, "en");

        //Assert
        track.CueCount.Should().Be(1);
        track.Cues[0].Text.Should().Be("The only cue");
    }

    [Fact]
    public void A_cue_spanning_several_lines_keeps_its_line_breaks()
    {
        //Arrange
        string text = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nfirst line\nsecond line\n";

        //Act
        CaptionTrack track = CaptionFiles.ParseWebVtt(text, 0, "en");

        //Assert
        track.Cues[0].Text.Should().Be("first line\nsecond line");
    }

    [Theory]
    [InlineData("00:00:01.500", 1.5)]
    [InlineData("01:02:03.004", 3723.004)]
    [InlineData("02:03.500", 123.5)]
    [InlineData("00:00:01,250", 1.25)]
    public void Times_are_parsed_in_both_the_webvtt_and_subrip_spellings(string text, double expectedSeconds)
    {
        //Arrange & Act
        bool parsed = CaptionFiles.TryParseWebVttTime(text, out TimeSpan value);

        //Assert
        parsed.Should().BeTrue();
        (value - TimeSpan.FromSeconds(expectedSeconds)).Duration()
            .Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void FormatWebVttTime_writes_the_form_the_parser_reads()
    {
        //Arrange
        TimeSpan value = TimeSpan.FromSeconds(3723.004);

        //Act
        string text = CaptionFiles.FormatWebVttTime(value);
        CaptionFiles.TryParseWebVttTime(text, out TimeSpan parsed);

        //Assert
        text.Should().Be("01:02:03.004");
        (parsed - value).Duration().Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ExtractAssText_takes_the_dialogue_and_leaves_the_styling_behind()
    {
        //Arrange
        string line = "0,0,Default,,0,0,0,,{\\i1}Hello{\\i0}\\Nthere";

        //Act
        string text = CaptionFiles.ExtractAssText(line);

        //Assert
        text.Should().Be("Hello\nthere");
    }

    [Fact]
    public void GetActiveCues_finds_the_cues_that_should_be_on_screen()
    {
        //Arrange
        CaptionTrack track = SyntheticMedia.MakeCaptionTrack(
            0,
            "en",
            CaptionTrackFlags.None,
            (0.0, 1.0, "first"),
            (0.5, 1.5, "overlapping"),
            (2.0, 3.0, "later"));

        System.Collections.Generic.List<CaptionCue> active = new System.Collections.Generic.List<CaptionCue>();

        //Act
        track.GetActiveCues(TimeSpan.FromSeconds(0.75), active);

        //Assert
        active.Count.Should().Be(2);
        active[0].Text.Should().Be("first");
        active[1].Text.Should().Be("overlapping");
    }
}
