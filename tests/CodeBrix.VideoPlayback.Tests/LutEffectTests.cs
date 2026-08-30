using System;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Effects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the lookup effect: the strength it is applied at, where it can be read from, and that composing it
/// gives what the core's own engine gives.
/// </summary>
public class LutEffectTests
{
    [Fact]
    public void An_effect_applies_its_whole_table_unless_it_is_told_otherwise()
    {
        //Arrange & Act
        LutEffect whole = new LutEffect(TestLuts.Invert(9));
        LutEffect half = new LutEffect(TestLuts.Invert(9), "half an inversion", 50d);

        //Assert
        whole.ApplyAtPercent.Should().Be(100d);
        half.ApplyAtPercent.Should().Be(50d);
        half.Name.Should().Be("half an inversion");
    }

    [Fact]
    public void An_inversion_at_half_strength_puts_every_colour_on_mid_grey()
    {
        //Arrange - lerp(c, 1 - c, 0.5) = 0.5 for every c
        EffectComposer composer = new EffectComposer(9);
        LutEffect half = new LutEffect(TestLuts.Invert(9), "half", 50d);

        //Act
        half.Compose(composer);

        //Assert
        composer.GetNode(0, 8, 4, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.5f, 1e-6f);
        green.Should().BeApproximately(0.5f, 1e-6f);
        blue.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void An_effect_at_nothing_leaves_the_grid_exactly_as_it_found_it()
    {
        //Arrange
        EffectComposer composer = new EffectComposer(5);
        LutEffect nothing = new LutEffect(TestLuts.Invert(5), "nothing", 0d);

        //Act
        nothing.Compose(composer);

        //Assert
        composer.GetNode(4, 4, 4, out float red, out float green, out float blue);
        red.Should().Be(1f);
        green.Should().Be(1f);
        blue.Should().Be(1f);
    }

    [Fact]
    public void An_effect_can_be_read_straight_out_of_a_cube_file()
    {
        //Arrange
        string directory = Path.Combine(
            Path.GetTempPath(),
            "codebrix-lut-effect-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cool-grade.cube");
        CubeLutFile.Write(TestLuts.Invert(9), path, null);

        try
        {
            //Act
            LutEffect whole = LutEffect.FromCubeFile(path);
            LutEffect quarter = LutEffect.FromCubeFile(path, 25d);

            //Assert
            whole.Name.Should().Be("cool-grade");
            whole.ApplyAtPercent.Should().Be(100d);
            whole.Lut3D.Size.Should().Be(9);
            quarter.ApplyAtPercent.Should().Be(25d);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void An_effect_made_from_a_combined_file_is_one_step_and_says_so()
    {
        //Arrange
        Lut1D shaper = new Lut1D(new[] { 0f, 0.5f });
        Lut3D table = TestLuts.Invert(9);
        CubeLut combined = new CubeLut(shaper, table, "shaped", "shaped");

        //Act
        LutEffect effect = LutEffect.FromCube(combined, 100d);
        EffectComposer composer = new EffectComposer(17);
        effect.Compose(composer);

        //Assert - out = 1 - (in / 2)
        effect.Layer.IsShaped.Should().BeTrue();
        composer.ToLut3D().Sample(1f, 1f, 1f, out float red, out _, out _);
        red.Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public void The_composer_folds_by_the_method_it_is_told_to_use()
    {
        //Arrange
        Lut3D curved = Curve(17, 2.4f);
        EffectComposer tetrahedral = new EffectComposer(33);
        EffectComposer trilinear = new EffectComposer(33) { Interpolation = LutInterpolation.Trilinear };

        //Act
        tetrahedral.ApplyLut(curved);
        trilinear.ApplyLut(curved);

        //Assert
        tetrahedral.Interpolation.Should().Be(LutInterpolation.Tetrahedral);

        tetrahedral.GetNode(5, 12, 20, out float tetraRed, out _, out _);
        trilinear.GetNode(5, 12, 20, out float triRed, out _, out _);
        Math.Abs(tetraRed - triRed).Should().BeLessThan(0.05f);
    }

    private static Lut3D Curve(int size, float exponent)
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
}
