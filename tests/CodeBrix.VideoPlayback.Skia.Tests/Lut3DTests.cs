using System;
using CodeBrix.VideoPlayback.Skia.Effects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>Checks the three-dimensional lookup table's construction and its interpolation.</summary>
public class Lut3DTests
{
    [Fact]
    public void An_identity_table_returns_exactly_what_it_is_given()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(33);

        //Act & Assert
        for (int step = 0; step <= 255; step += 17)
        {
            float value = step / 255f;
            lut.Sample(value, 1f - value, 0.5f, out float red, out float green, out float blue);

            red.Should().BeApproximately(value, 1e-5f);
            green.Should().BeApproximately(1f - value, 1e-5f);
            blue.Should().BeApproximately(0.5f, 1e-5f);
        }
    }

    [Fact]
    public void Sampling_interpolates_between_the_eight_corners_of_a_cell()
    {
        //Arrange - a two-node table whose red output is the blue input and vice versa
        float[] values = new float[2 * 2 * 2 * 3];
        int index = 0;
        for (int blue = 0; blue < 2; blue++)
        {
            for (int green = 0; green < 2; green++)
            {
                for (int red = 0; red < 2; red++)
                {
                    values[index++] = blue;
                    values[index++] = green;
                    values[index++] = red;
                }
            }
        }

        Lut3D lut = new Lut3D(2, values);

        //Act
        lut.Sample(0.25f, 0.75f, 1f, out float outputRed, out float outputGreen, out float outputBlue);

        //Assert
        outputRed.Should().BeApproximately(1f, 1e-5f);
        outputGreen.Should().BeApproximately(0.75f, 1e-5f);
        outputBlue.Should().BeApproximately(0.25f, 1e-5f);
    }

    [Fact]
    public void Values_outside_zero_to_one_are_clamped_rather_than_extrapolated()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(9);

        //Act
        lut.Sample(-2f, 4f, 0.5f, out float red, out float green, out float blue);

        //Assert
        red.Should().Be(0f);
        green.Should().Be(1f);
        blue.Should().BeApproximately(0.5f, 1e-5f);
    }

    [Fact]
    public void The_value_order_puts_red_fastest_and_blue_slowest()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(4);

        //Act
        ReadOnlySpan<float> values = lut.Values;

        //Assert - node (1,0,0) is the second triplet; node (0,0,1) is the seventeenth
        values[3].Should().BeApproximately(1f / 3f, 1e-6f);
        values[4].Should().Be(0f);
        values[(((1 * 4) + 0) * 4 + 0) * 3 + 2].Should().BeApproximately(1f / 3f, 1e-6f);
    }

    [Fact]
    public void A_table_refuses_a_grid_it_cannot_be()
    {
        //Arrange
        float[] tooFew = new float[10];

        //Act
        Action wrongLength = () => new Lut3D(4, tooFew);
        Action wrongSize = () => new Lut3D(1, new float[3]);

        //Assert
        wrongLength.Should().Throw<ArgumentException>().WithMessage("*192 numbers*");
        wrongSize.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_values_are_copied_so_a_caller_cannot_change_a_table_afterwards()
    {
        //Arrange
        float[] values = new float[2 * 2 * 2 * 3];
        Lut3D lut = new Lut3D(2, values);

        //Act
        values[0] = 0.5f;

        //Assert
        lut.Values[0].Should().Be(0f);
    }
}
