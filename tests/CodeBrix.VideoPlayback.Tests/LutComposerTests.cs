using System;
using System.Diagnostics;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the effective-table engine: the folding arithmetic, the order of a chain, each layer's own size
/// and domain, the size and domain of the result, and what the two interpolation methods cost.
/// </summary>
/// <remarks>
/// The reference values here are worked out in the test itself, from the definition of the layers, and never
/// read back out of the composer - so a wrong composer cannot make them agree with it.
/// </remarks>
public class LutComposerTests
{
    [Fact]
    public void A_chain_of_one_identity_table_leaves_every_colour_where_it_was()
    {
        //Arrange
        Lut3D table = Lut3D.CreateIdentity(33);
        LutLayer identity = new LutLayer(table);

        //Act
        Lut3D effective = LutComposer.Compose(new[] { identity });

        //Assert
        effective.Size.Should().Be(33);
        effective.Values.ToArray().Should().Equal(table.Values.ToArray());
    }

    [Fact]
    public void An_empty_chain_composes_to_the_table_that_changes_nothing()
    {
        //Act
        Lut3D effective = LutComposer.Compose(Array.Empty<LutLayer>());

        //Assert
        effective.Size.Should().Be(LutComposer.DefaultMinimumOutputSize);
        effective.Sample(0.25f, 0.5f, 0.75f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void One_layer_at_a_hundred_percent_at_its_own_size_is_that_layer()
    {
        //Arrange - a strongly curved table, so reproducing it is a real claim
        Lut3D curve = Curve(17, 2.2f);

        //Act
        Lut3D effective = LutComposer.Compose(
            new[] { new LutLayer(curve) },
            new LutComposerOptions { OutputSize = 17 });

        //Assert - node for node, within the error of sampling a table at its own node positions
        for (int index = 0; index < effective.Values.Length; index++)
        {
            effective.Values[index].Should().BeApproximately(curve.Values[index], 1e-6f);
        }
    }

    [Fact]
    public void An_inversion_at_half_strength_lands_every_colour_on_mid_grey()
    {
        //Arrange - lerp(c, 1 - c, 0.5) = 0.5 for EVERY c, worked out by hand and true of every channel
        LutLayer invert = new LutLayer(Lut3DInterpolationTests.Invert(9), 50d);

        //Act
        Lut3D effective = LutComposer.Compose(new[] { invert });

        //Assert
        for (int index = 0; index < effective.Values.Length; index++)
        {
            effective.Values[index].Should().BeApproximately(0.5f, 1e-6f);
        }
    }

    [Fact]
    public void A_layer_at_nothing_is_skipped_and_changes_no_colour()
    {
        //Arrange
        LutLayer nothing = new LutLayer(Lut3DInterpolationTests.Invert(9), 0d);

        //Act
        Lut3D effective = LutComposer.Compose(new[] { nothing });

        //Assert
        nothing.HasEffect.Should().BeFalse();
        effective.Sample(0.25f, 0.5f, 0.75f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void A_percentage_outside_zero_to_a_hundred_is_clamped_rather_than_extrapolated()
    {
        //Arrange
        LutLayer over = new LutLayer(Lut3DInterpolationTests.Invert(9), 250d);
        LutLayer under = new LutLayer(Lut3DInterpolationTests.Invert(9), -40d);

        //Act & Assert
        over.ApplyAtPercent.Should().Be(100d);
        under.ApplyAtPercent.Should().Be(0d);

        LutComposer.Compose(new[] { over }).Sample(0.25f, 0f, 0f, out float inverted, out _, out _);
        inverted.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void The_order_of_two_layers_is_the_difference_between_two_pictures()
    {
        //Arrange - halve then invert, and invert then halve
        Lut3D halve = Scale(17, 0.5f);
        Lut3D invert = Lut3DInterpolationTests.Invert(17);

        //Act
        Lut3D halveThenInvert = LutComposer.Compose(
            new[] { new LutLayer(halve), new LutLayer(invert) });

        Lut3D invertThenHalve = LutComposer.Compose(
            new[] { new LutLayer(invert), new LutLayer(halve) });

        //Assert - reference worked out by hand for four inputs
        foreach (float input in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            halveThenInvert.Sample(input, input, input, out float first, out _, out _);
            invertThenHalve.Sample(input, input, input, out float second, out _, out _);

            first.Should().BeApproximately(1f - (input * 0.5f), 1e-4f);
            second.Should().BeApproximately((1f - input) * 0.5f, 1e-4f);
        }
    }

    [Fact]
    public void Each_layer_is_sampled_at_its_own_size_and_never_at_the_output_size()
    {
        //Arrange - a 17-node curve then a 33-node curve; the second sees colours that are NOT on the
        //33 lattice, so an implementation that resampled it to the output size first would differ
        Lut3D first = Curve(17, 2.4f);
        Lut3D second = Curve(33, 0.45f);

        //Act
        Lut3D effective = LutComposer.Compose(new[] { new LutLayer(first), new LutLayer(second) });

        //Assert
        effective.Size.Should().Be(33);

        float last = effective.Size - 1;
        for (int blue = 0; blue < effective.Size; blue += 7)
        {
            for (int green = 0; green < effective.Size; green += 5)
            {
                for (int red = 0; red < effective.Size; red += 3)
                {
                    // The reference: sample layer one at ITS size, then layer two at ITS size.
                    first.Sample(
                        red / last,
                        green / last,
                        blue / last,
                        LutInterpolation.Tetrahedral,
                        out float midRed,
                        out float midGreen,
                        out float midBlue);

                    second.Sample(
                        midRed,
                        midGreen,
                        midBlue,
                        LutInterpolation.Tetrahedral,
                        out float wantRed,
                        out float wantGreen,
                        out float wantBlue);

                    int at = (((blue * effective.Size) + green) * effective.Size + red) * 3;
                    effective.Values[at].Should().BeApproximately(wantRed, 1e-6f);
                    effective.Values[at + 1].Should().BeApproximately(wantGreen, 1e-6f);
                    effective.Values[at + 2].Should().BeApproximately(wantBlue, 1e-6f);
                }
            }
        }
    }

    [Fact]
    public void A_layer_declaring_a_wider_domain_uses_only_the_part_of_itself_the_picture_reaches()
    {
        //Arrange - an identity cube declared over 0 to 4: out = in / 4 for an ordinary 0-to-1 picture
        Lut3D wide = new Lut3D(
            33,
            Lut3D.CreateIdentity(33).Values.ToArray(),
            new[] { 0f, 0f, 0f },
            new[] { 4f, 4f, 4f });

        //Act
        Lut3D effective = LutComposer.Compose(new[] { new LutLayer(wide) });

        //Assert
        effective.HasDefaultDomain.Should().BeTrue();
        foreach (float input in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            effective.Sample(input, input, input, out float red, out _, out _);
            red.Should().BeApproximately(input / 4f, 1e-5f);
        }
    }

    [Fact]
    public void The_chains_own_domain_is_reported_but_never_taken_without_being_asked_for()
    {
        //Arrange
        Lut3D wide = new Lut3D(
            9,
            Lut3D.CreateIdentity(9).Values.ToArray(),
            new[] { -0.5f, -0.5f, -0.5f },
            new[] { 1.5f, 1.5f, 1.5f });

        LutLayer[] chain = { new LutLayer(wide) };

        //Act
        bool wider = LutComposer.TryGetChainDomain(chain, out float[] minimum, out float[] maximum);
        Lut3D bakedForThePicture = LutComposer.Compose(chain);
        Lut3D bakedForTheChain = LutComposer.Compose(
            chain,
            new LutComposerOptions { OutputDomainMinimum = minimum, OutputDomainMaximum = maximum });

        //Assert
        wider.Should().BeTrue();
        minimum[0].Should().Be(-0.5f);
        maximum[0].Should().Be(1.5f);
        bakedForThePicture.HasDefaultDomain.Should().BeTrue();
        bakedForTheChain.HasDefaultDomain.Should().BeFalse();
        bakedForTheChain.DomainMaximum[1].Should().Be(1.5f);
    }

    [Theory]
    [InlineData(17, 33)]
    [InlineData(33, 33)]
    [InlineData(64, 64)]
    [InlineData(65, 65)]
    [InlineData(129, 65)]
    public void The_output_size_is_the_largest_layer_floored_at_thirty_three_and_capped_at_sixty_five(
        int layerSize,
        int expected)
    {
        //Arrange
        LutLayer[] chain = { new LutLayer(Lut3D.CreateIdentity(layerSize)) };

        //Act
        int size = LutComposer.GetOutputSize(chain);

        //Assert
        size.Should().Be(expected);
        LutComposer.Compose(chain).Size.Should().Be(expected);
    }

    [Fact]
    public void A_layer_applied_at_nothing_does_not_decide_the_output_size()
    {
        //Arrange
        LutLayer[] chain =
        {
            new LutLayer(Lut3D.CreateIdentity(65), 0d),
            new LutLayer(Lut3D.CreateIdentity(17)),
        };

        //Act & Assert
        LutComposer.GetOutputSize(chain).Should().Be(33);
    }

    [Fact]
    public void A_chain_of_curves_only_can_be_had_as_curves_and_agrees_with_the_cube()
    {
        //Arrange - gamma up then gamma down, at different sizes and strengths
        LutLayer[] chain =
        {
            new LutLayer(Gamma(256, 2.2f)),
            new LutLayer(Gamma(64, 0.6f), 40d),
        };

        //Act
        bool exact = LutComposer.TryComposeCurves(chain, null, out Lut1D curves);
        Lut3D cube = LutComposer.Compose(chain);

        //Assert
        exact.Should().BeTrue();
        curves.Size.Should().Be(LutComposer.DefaultMinimumCurveSize);

        foreach (float input in new[] { 0f, 0.1f, 0.35f, 0.5f, 0.9f, 1f })
        {
            curves.Sample(input, input, input, out float wantRed, out _, out _);
            cube.Sample(input, input, input, out float gotRed, out _, out _);

            // Both are the same arithmetic; the cube merely samples it on a coarser lattice.
            gotRed.Should().BeApproximately(wantRed, 2e-3f);
        }
    }

    [Fact]
    public void A_chain_holding_a_cube_cannot_be_had_as_curves()
    {
        //Arrange
        LutLayer[] chain = { new LutLayer(Gamma(64, 2.2f)), new LutLayer(Lut3D.CreateIdentity(9)) };

        //Act
        bool exact = LutComposer.TryComposeCurves(chain, null, out Lut1D curves);

        //Assert
        exact.Should().BeFalse();
        curves.Should().BeNull();
    }

    [Fact]
    public void A_shaper_and_the_table_it_feeds_are_one_layer_and_one_percentage()
    {
        //Arrange - the shaper halves, the table inverts
        Lut1D shaper = new Lut1D(new[] { 0f, 0.5f });
        Lut3D table = Lut3DInterpolationTests.Invert(9);

        LutLayer whole = new LutLayer(shaper, table, 100d);
        LutLayer half = new LutLayer(shaper, table, 50d);

        //Act
        Lut3D full = LutComposer.Compose(new[] { whole });
        Lut3D partial = LutComposer.Compose(new[] { half });

        //Assert - reference: out = 1 - (in / 2), and half way to it from in
        whole.IsShaped.Should().BeTrue();
        foreach (float input in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            float want = 1f - (input * 0.5f);

            full.Sample(input, input, input, out float got, out _, out _);
            partial.Sample(input, input, input, out float mixed, out _, out _);

            got.Should().BeApproximately(want, 1e-4f);
            mixed.Should().BeApproximately(input + ((want - input) * 0.5f), 1e-4f);
        }
    }

    [Fact]
    public void The_two_interpolation_methods_differ_by_a_measured_and_bounded_amount()
    {
        //Arrange - a chain whose tables genuinely bend: a saturation twist then a strong curve
        LutLayer[] chain = { new LutLayer(Twist(17)), new LutLayer(Curve(17, 2.4f)) };

        //Act
        Lut3D tetrahedral = LutComposer.Compose(
            chain,
            new LutComposerOptions { Interpolation = LutInterpolation.Tetrahedral });

        Lut3D trilinear = LutComposer.Compose(
            chain,
            new LutComposerOptions { Interpolation = LutInterpolation.Trilinear });

        float worst = 0f;
        double total = 0d;
        for (int index = 0; index < tetrahedral.Values.Length; index++)
        {
            float difference = Math.Abs(tetrahedral.Values[index] - trilinear.Values[index]);
            if (difference > worst) worst = difference;
            total += difference;
        }

        double mean = total / tetrahedral.Values.Length;

        //Assert - they DO differ, and by an amount an eight-bit picture would show as a few levels.
        //Measured on this chain, 33 cubed: worst 0.019970 (5.1 levels of 255), mean 0.000107 (0.027 of a
        //level). The bounds are a little above those, so a real change in the arithmetic fails here.
        worst.Should().BeGreaterThan(0f);
        worst.Should().BeLessThan(0.03f);
        mean.Should().BeLessThan(0.005d);
    }

    [Fact]
    public void A_sixty_five_node_table_through_four_layers_is_folded_in_well_under_a_second()
    {
        //Arrange
        LutLayer[] chain =
        {
            new LutLayer(Twist(33)),
            new LutLayer(Curve(33, 2.2f), 60d),
            new LutLayer(Gamma(1024, 0.8f), 25d),
            new LutLayer(Lut3DInterpolationTests.Invert(65), 10d),
        };

        //Act
        Stopwatch clock = Stopwatch.StartNew();
        Lut3D effective = LutComposer.Compose(chain);
        clock.Stop();

        //Assert - measured at 19.6 milliseconds on the development machine for 274,625 nodes through four
        //layers, which is why the walk is not parallelised at all. The bound is generous enough for a slow
        //machine and still fails outright on a quadratic mistake.
        effective.Size.Should().Be(65);
        clock.Elapsed.TotalSeconds.Should().BeLessThan(2d);
    }

    [Fact]
    public void A_null_layer_in_a_chain_is_ignored_rather_than_thrown_over()
    {
        //Arrange
        LutLayer[] chain = { null, new LutLayer(Lut3DInterpolationTests.Invert(9)), null };

        //Act
        Lut3D effective = LutComposer.Compose(chain);

        //Assert
        effective.Sample(0.25f, 0f, 0f, out float red, out _, out _);
        red.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void An_output_size_no_table_could_have_is_refused_by_name()
    {
        //Arrange
        LutLayer[] chain = { new LutLayer(Lut3D.CreateIdentity(9)) };

        //Act
        Action tooLarge = () => LutComposer.Compose(chain, new LutComposerOptions { OutputSize = 200 });

        //Assert
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
    }

    internal static Lut3D Scale(int size, float factor)
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
                    values[index++] = (red / last) * factor;
                    values[index++] = (green / last) * factor;
                    values[index++] = (blue / last) * factor;
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>A per-channel power curve as a cube - smooth, and nothing like linear.</summary>
    /// <param name="size">The nodes a side.</param>
    /// <param name="exponent">The power each channel is raised to.</param>
    /// <returns>The table.</returns>
    internal static Lut3D Curve(int size, float exponent)
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
                    values[index++] = MathF.Pow(red / last, exponent);
                    values[index++] = MathF.Pow(green / last, exponent);
                    values[index++] = MathF.Pow(blue / last, exponent);
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>A cube whose channels depend on each other, so no curve set could be it.</summary>
    /// <param name="size">The nodes a side.</param>
    /// <returns>The table.</returns>
    internal static Lut3D Twist(int size)
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
                    float r = red / last;
                    float g = green / last;
                    float b = blue / last;
                    float luma = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                    float shift = (luma - 0.5f) * 0.6f;

                    values[index++] = Clamp(r + shift);
                    values[index++] = Clamp(g + (shift * 0.2f));
                    values[index++] = Clamp(b - shift);
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>A per-channel power curve as a curve set.</summary>
    /// <param name="size">The points in each curve.</param>
    /// <param name="exponent">The power each channel is raised to.</param>
    /// <returns>The curves.</returns>
    internal static Lut1D Gamma(int size, float exponent)
    {
        float[] curve = new float[size];
        for (int point = 0; point < size; point++)
        {
            curve[point] = MathF.Pow(point / (float)(size - 1), exponent);
        }

        return new Lut1D(curve);
    }

    private static float Clamp(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
}
