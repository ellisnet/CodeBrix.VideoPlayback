using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the two ways a colour between the nodes of a cube is worked out, against properties that hold
/// whatever the table is.
/// </summary>
/// <remarks>
/// Both methods are weighted averages of corners whose weights are never negative and always sum to one, so
/// a table of one constant returns that constant and a table that is linear along each axis is reproduced
/// exactly. Where the table bends they part company, and by how much is measured in
/// <see cref="LutComposerTests" />.
/// </remarks>
public class Lut3DInterpolationTests
{
    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void An_identity_cube_returns_what_it_is_given_by_either_method(LutInterpolation interpolation)
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(17);

        //Act & Assert
        for (int step = 0; step <= 100; step += 7)
        {
            float value = step / 100f;
            lut.Sample(value, 1f - value, 0.5f, interpolation, out float red, out float green, out float blue);

            red.Should().BeApproximately(value, 1e-5f);
            green.Should().BeApproximately(1f - value, 1e-5f);
            blue.Should().BeApproximately(0.5f, 1e-5f);
        }
    }

    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void A_cube_of_one_colour_returns_that_colour_because_the_weights_sum_to_one(
        LutInterpolation interpolation)
    {
        //Arrange - every node is the same colour, so any weighted average of corners must be it
        float[] values = new float[4 * 4 * 4 * 3];
        for (int index = 0; index < values.Length; index += 3)
        {
            values[index] = 0.125f;
            values[index + 1] = 0.625f;
            values[index + 2] = 0.875f;
        }

        Lut3D lut = new Lut3D(4, values);

        //Act
        lut.Sample(0.31f, 0.77f, 0.02f, interpolation, out float red, out float green, out float blue);

        //Assert
        red.Should().BeApproximately(0.125f, 1e-6f);
        green.Should().BeApproximately(0.625f, 1e-6f);
        blue.Should().BeApproximately(0.875f, 1e-6f);
    }

    [Fact]
    public void The_two_methods_agree_exactly_on_a_table_that_is_linear_along_each_axis()
    {
        //Arrange - out = 1 - in, which is linear in each channel, so neither method can bend it
        Lut3D lut = Invert(5);

        //Act & Assert
        for (int step = 0; step <= 100; step += 3)
        {
            float value = step / 100f;
            lut.Sample(value, value * 0.5f, 1f - value, LutInterpolation.Tetrahedral, out float tr, out float tg, out float tb);
            lut.Sample(value, value * 0.5f, 1f - value, LutInterpolation.Trilinear, out float lr, out float lg, out float lb);

            tr.Should().BeApproximately(lr, 1e-6f);
            tg.Should().BeApproximately(lg, 1e-6f);
            tb.Should().BeApproximately(lb, 1e-6f);
        }
    }

    [Fact]
    public void The_two_methods_part_company_where_a_table_is_not_linear()
    {
        //Arrange - a two-node table whose only bend is that one corner is pulled away
        float[] values = new float[2 * 2 * 2 * 3];
        for (int blue = 0; blue < 2; blue++)
        {
            for (int green = 0; green < 2; green++)
            {
                for (int red = 0; red < 2; red++)
                {
                    int index = (((blue * 2) + green) * 2 + red) * 3;
                    values[index] = red;
                    values[index + 1] = green;
                    values[index + 2] = blue;
                }
            }
        }

        // Pull the (r=0, g=1, b=1) corner's RED up to 1, which no affine function of r, g and b can do.
        values[((((1 * 2) + 1) * 2) + 0) * 3] = 1f;

        Lut3D lut = new Lut3D(2, values);

        //Act
        lut.Sample(0.5f, 0.5f, 0.5f, LutInterpolation.Tetrahedral, out float tetra, out _, out _);
        lut.Sample(0.5f, 0.5f, 0.5f, LutInterpolation.Trilinear, out float tri, out _, out _);

        //Assert - trilinear averages all eight corners (5 of the 8 hold 1, so 0.625); tetrahedral walks
        //the green-red-blue wedge and lands on 0.5
        tri.Should().BeApproximately(0.625f, 1e-6f);
        tetra.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void A_declared_domain_is_applied_before_the_lookup_and_not_after()
    {
        //Arrange - an identity cube declared over 0 to 4, so 0 to 1 uses its bottom quarter
        Lut3D lut = new Lut3D(
            9,
            Lut3D.CreateIdentity(9).Values.ToArray(),
            new[] { 0f, 0f, 0f },
            new[] { 4f, 4f, 4f });

        //Act
        lut.Sample(1f, 2f, 4f, LutInterpolation.Tetrahedral, out float red, out float green, out float blue);

        //Assert
        lut.HasDefaultDomain.Should().BeFalse();
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void A_domain_that_does_not_rise_is_refused_by_the_table_itself()
    {
        //Act
        System.Action flat = () => new Lut3D(
            2,
            new float[24],
            new[] { 1f, 0f, 0f },
            new[] { 1f, 1f, 1f });

        //Assert
        flat.Should().Throw<System.ArgumentException>().WithMessage("*must rise*");
    }

    internal static Lut3D Invert(int size)
    {
        float[] values = new float[size * size * size * 3];
        float last = size - 1;
        int index = 0;

        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    values[index++] = 1f - (red / last);
                    values[index++] = 1f - (green / last);
                    values[index++] = 1f - (blue / last);
                }
            }
        }

        return new Lut3D(size, values);
    }
}
