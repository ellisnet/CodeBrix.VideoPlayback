using System;
using CodeBrix.VideoPlayback.Skia.Effects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>Checks the per-channel curve table.</summary>
public class Lut1DTests
{
    [Fact]
    public void An_identity_curve_returns_exactly_what_it_is_given()
    {
        //Arrange
        Lut1D lut = Lut1D.CreateIdentity(256);

        //Act & Assert
        for (int step = 0; step <= 255; step += 5)
        {
            float value = step / 255f;
            lut.Sample(value, value, value, out float red, out float green, out float blue);
            red.Should().BeApproximately(value, 1e-5f);
            green.Should().BeApproximately(value, 1e-5f);
            blue.Should().BeApproximately(value, 1e-5f);
        }
    }

    [Fact]
    public void Each_channel_follows_its_own_curve()
    {
        //Arrange
        Lut1D lut = new Lut1D(
            new[] { 0f, 1f },
            new[] { 1f, 0f },
            new[] { 0.5f, 0.5f });

        //Act
        lut.Sample(0.25f, 0.25f, 0.25f, out float red, out float green, out float blue);

        //Assert
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.75f, 1e-5f);
        blue.Should().BeApproximately(0.5f, 1e-5f);
    }

    [Fact]
    public void One_curve_given_for_all_three_channels_reads_as_monochrome()
    {
        //Arrange
        Lut1D shared = new Lut1D(new[] { 0f, 0.5f, 1f });
        Lut1D split = new Lut1D(new[] { 0f, 1f }, new[] { 0f, 0.5f }, new[] { 0f, 1f });

        //Act & Assert
        shared.IsMonochrome.Should().BeTrue();
        split.IsMonochrome.Should().BeFalse();
        shared.Size.Should().Be(3);
    }

    [Fact]
    public void Curves_of_different_lengths_are_refused()
    {
        //Arrange
        float[] two = { 0f, 1f };
        float[] three = { 0f, 0.5f, 1f };

        //Act
        Action mismatched = () => new Lut1D(two, three, two);

        //Assert
        mismatched.Should().Throw<ArgumentException>().WithMessage("*same length*");
    }
}
