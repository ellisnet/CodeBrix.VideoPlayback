using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Chapters;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the chapter file reader and writer, including the per-language title extension this library adds to
/// the format so one file can author chapters in several languages.
/// </summary>
public class FfMetadataChaptersTests
{
    [Fact]
    public void The_golden_chapter_file_reads_three_chapters_in_order()
    {
        //Arrange
        string path = TestAssets.Path("chapters.ffmeta");

        //Act
        IReadOnlyList<Chapter> chapters = FfMetadataChapters.ReadFile(path);

        //Assert
        chapters.Count.Should().Be(3);
        chapters[0].Start.Should().Be(TimeSpan.Zero);
        (chapters[1].Start > chapters[0].Start).Should().BeTrue();
        (chapters[2].Start > chapters[1].Start).Should().BeTrue();
        chapters[0].Index.Should().Be(0);
        chapters[2].Index.Should().Be(2);
    }

    [Fact]
    public void A_per_language_title_becomes_a_second_title_on_the_chapter()
    {
        //Arrange
        string path = TestAssets.Path("chapters.ffmeta");

        //Act
        IReadOnlyList<Chapter> chapters = FfMetadataChapters.ReadFile(path);

        //Assert
        chapters[0].Titles.Count.Should().BeGreaterThanOrEqualTo(2);
        chapters[0].TitleFor(new[] { "fr" }).Should().NotBe(chapters[0].Title);
    }

    [Fact]
    public void The_timebase_decides_what_the_numbers_mean()
    {
        //Arrange
        string text = ";FFMETADATA1\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Halfway\n";

        //Act
        IReadOnlyList<Chapter> chapters = FfMetadataChapters.Parse(text);

        //Assert
        chapters[0].Start.Should().Be(TimeSpan.FromSeconds(1.5));
        chapters[0].End.Should().Be(TimeSpan.FromSeconds(3));
        chapters[0].Title.Should().Be("Halfway");
    }

    [Fact]
    public void A_second_timebase_is_honoured_rather_than_assumed()
    {
        //Arrange
        string text = ";FFMETADATA1\n\n[CHAPTER]\nTIMEBASE=1/1\nSTART=2\nEND=5\ntitle=Seconds\n";

        //Act
        IReadOnlyList<Chapter> chapters = FfMetadataChapters.Parse(text);

        //Assert
        chapters[0].Start.Should().Be(TimeSpan.FromSeconds(2));
        chapters[0].End.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void What_is_written_reads_back_the_same()
    {
        //Arrange
        IReadOnlyList<Chapter> original = SyntheticMedia.MakeChapters(0, 1.5, 4);

        //Act
        string text = FfMetadataChapters.Write(original);
        IReadOnlyList<Chapter> readBack = FfMetadataChapters.Parse(text);

        //Assert
        readBack.Count.Should().Be(original.Count);
        for (int i = 0; i < original.Count; i++)
        {
            readBack[i].Start.Should().Be(original[i].Start);
            readBack[i].Title.Should().Be(original[i].Title);
            readBack[i].TitleFor(new[] { "fr" }).Should().Be(original[i].TitleFor(new[] { "fr" }));
        }
    }

    [Fact]
    public void TitleFor_falls_back_from_an_exact_tag_to_the_primary_subtag()
    {
        //Arrange
        Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-GB"] = "Colour",
            ["fr"] = "Couleur",
        };

        Chapter chapter = new Chapter(0, TimeSpan.Zero, TimeSpan.FromSeconds(1), false, titles);

        //Act
        string exact = chapter.TitleFor(new[] { "en-GB" });
        string primary = chapter.TitleFor(new[] { "en" });
        string other = chapter.TitleFor(new[] { "de", "fr" });
        string none = chapter.TitleFor(Array.Empty<string>());

        //Assert
        exact.Should().Be("Colour");
        primary.Should().Be("Colour");
        other.Should().Be("Couleur");
        none.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Text_that_is_not_a_chapter_file_produces_no_chapters()
    {
        //Arrange
        string text = "this is not metadata at all";

        //Act
        IReadOnlyList<Chapter> chapters = FfMetadataChapters.Parse(text);

        //Assert
        chapters.Count.Should().Be(0);
    }
}
