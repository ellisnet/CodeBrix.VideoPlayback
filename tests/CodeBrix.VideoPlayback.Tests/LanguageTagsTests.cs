using CodeBrix.VideoPlayback.Containers;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the language-code normalisation every track and caption goes through.
/// </summary>
public class LanguageTagsTests
{
    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("ger", "de")]
    [InlineData("jpn", "ja")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("eng-GB", "en-GB")]
    public void A_container_code_becomes_a_bcp_47_tag(string code, string expected)
    {
        //Arrange & Act
        string tag = LanguageTags.Normalize(code);

        //Assert
        tag.Should().Be(expected);
    }

    [Theory]
    [InlineData("und")]
    [InlineData("mis")]
    [InlineData("zxx")]
    [InlineData("")]
    [InlineData(null)]
    public void A_code_that_says_nothing_becomes_an_empty_tag(string code)
        => LanguageTags.Normalize(code).Should().Be(string.Empty);

    [Fact]
    public void A_three_letter_code_with_no_two_letter_equivalent_is_passed_through()
        => LanguageTags.Normalize("haw").Should().Be("haw");

    [Theory]
    [InlineData("en", "eng", true)]
    [InlineData("en-GB", "en-US", true)]
    [InlineData("en", "fr", false)]
    [InlineData("", "en", false)]
    public void SameLanguage_compares_the_primary_subtag(string first, string second, bool expected)
        => LanguageTags.SameLanguage(first, second).Should().Be(expected);
}
