using System;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>Checks the three shapes a frame size can take, and the dimensions each one refuses.</summary>
public class AuthoringFrameSizeTests
{
    [Fact]
    public void Source_scales_nothing()
    {
        //Act
        AuthoringFrameSize size = AuthoringFrameSize.Source;

        //Assert
        size.IsSourceSize.Should().BeTrue();
        size.Kind.Should().Be(AuthoringFrameSizeKind.Source);
        size.ToString().Should().Be("the source's own size");
    }

    [Fact]
    public void Exact_states_both_dimensions()
    {
        //Act
        AuthoringFrameSize size = AuthoringFrameSize.Exact(1920, 1080);

        //Assert
        size.Kind.Should().Be(AuthoringFrameSizeKind.Exact);
        size.Width.Should().Be(1920);
        size.Height.Should().Be(1080);
        size.ToString().Should().Be("1920x1080");
    }

    [Fact]
    public void LongSide_and_ShortSide_state_one_dimension()
    {
        //Act
        AuthoringFrameSize longSide = AuthoringFrameSize.LongSide(1280);
        AuthoringFrameSize shortSide = AuthoringFrameSize.ShortSide(720);

        //Assert
        longSide.Pixels.Should().Be(1280);
        longSide.ToString().Should().Be("1280 on the long side");
        shortSide.Pixels.Should().Be(720);
        shortSide.ToString().Should().Be("720 on the short side");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_dimension_that_is_not_positive_is_refused(int value)
    {
        //Act
        //Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoringFrameSize.Exact(value, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoringFrameSize.LongSide(value));
    }

    [Fact]
    public void An_odd_dimension_is_refused_because_four_two_zero_chroma_has_nowhere_to_put_it()
    {
        //Act
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => AuthoringFrameSize.Exact(1921, 1080));

        //Assert
        failure.Message.Should().Contain("EVEN");
    }
}
