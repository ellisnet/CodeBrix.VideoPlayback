using CodeBrix.VideoPlayback.Authoring.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>Checks the well-formedness rule caption and audio language tags are held to.</summary>
public class BcpLanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("en-GB")]
    [InlineData("zh-Hant-TW")]
    [InlineData("de-1901")]
    [InlineData("ast")]
    [InlineData("sr-Latn-RS")]
    public void A_well_formed_tag_is_accepted(string tag)
    {
        //Act
        bool wellFormed = BcpLanguageTag.IsWellFormed(tag);

        //Assert
        wellFormed.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("en_GB")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("en--GB")]
    [InlineData("123")]
    [InlineData("english language")]
    [InlineData("abcdefghi")]
    public void A_malformed_tag_is_refused(string tag)
    {
        //Act
        bool wellFormed = BcpLanguageTag.IsWellFormed(tag);

        //Assert
        wellFormed.Should().BeFalse();
    }
}
