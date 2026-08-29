using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class SmokeLutRequestTests
{
    [Fact]
    public void TryParse_reads_a_bare_name_at_the_panel_default()
    {
        //Act
        var read = SmokeLutRequest.TryParse("sepia_33.cube", out var request, out var error);

        //Assert
        read.Should().BeTrue();
        error.Should().Be(string.Empty);
        request.Name.Should().Be("sepia_33.cube");
        request.ApplyAtPercent.Should().Be(40);
    }

    [Fact]
    public void TryParse_reads_a_name_and_a_percentage_after_an_at_sign()
    {
        //Act
        var read = SmokeLutRequest.TryParse("cool_33.cube@72.5", out var request, out var error);

        //Assert
        read.Should().BeTrue();
        error.Should().Be(string.Empty);
        request.Name.Should().Be("cool_33.cube");
        request.ApplyAtPercent.Should().Be(72.5);
    }

    [Fact]
    public void TryParse_also_accepts_an_equals_sign()
    {
        //Act
        var read = SmokeLutRequest.TryParse("cool_33.cube=60", out var request, out _);

        //Assert
        read.Should().BeTrue();
        request.Name.Should().Be("cool_33.cube");
        request.ApplyAtPercent.Should().Be(60);
    }

    [Fact]
    public void TryParse_refuses_a_percentage_that_is_not_a_number()
    {
        //Act
        var read = SmokeLutRequest.TryParse("warm_33.cube@loud", out var request, out var error);

        //Assert
        read.Should().BeFalse();
        request.Should().BeNull();
        error.Should().Contain("loud");
        error.Should().Contain("warm_33.cube@loud");
    }

    [Fact]
    public void TryParse_refuses_a_percentage_with_no_name_in_front_of_it()
    {
        //Act
        var read = SmokeLutRequest.TryParse("@40", out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("names no lookup table");
    }

    [Fact]
    public void TryParse_refuses_an_empty_value()
    {
        //Act
        var read = SmokeLutRequest.TryParse("   ", out _, out var error);

        //Assert
        read.Should().BeFalse();
        error.Should().Contain("--lut");
    }

    [Fact]
    public void TryParse_clamps_a_percentage_out_of_range()
    {
        //Act
        SmokeLutRequest.TryParse("warm_33.cube@900", out var high, out _);
        SmokeLutRequest.TryParse("warm_33.cube@-5", out var low, out _);

        //Assert
        high.ApplyAtPercent.Should().Be(100);
        low.ApplyAtPercent.Should().Be(0);
    }

    [Fact]
    public void TryParse_splits_on_the_last_separator_only()
    {
        //Act
        var read = SmokeLutRequest.TryParse("odd@name.cube@25", out var request, out _);

        //Assert
        read.Should().BeTrue();
        request.Name.Should().Be("odd@name.cube");
        request.ApplyAtPercent.Should().Be(25);
    }

    [Fact]
    public void ToString_writes_the_value_that_would_produce_it()
    {
        //Arrange
        SmokeLutRequest.TryParse("sepia_33.cube@40", out var request, out _);

        //Act
        var text = request.ToString();

        //Assert
        text.Should().Be("sepia_33.cube@40");

        //And the text it wrote reads back to the same request.
        SmokeLutRequest.TryParse(text, out var again, out _).Should().BeTrue();
        again.Name.Should().Be(request.Name);
        again.ApplyAtPercent.Should().Be(request.ApplyAtPercent);
    }
}
