using System;
using CodeBrix.VideoPlayback.Skia.Effects;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Checks that a chain of effects folds into ONE resultant table, in order, and only when the chain changes.
/// </summary>
public class EffectComposerTests
{
    [Fact]
    public void A_new_composer_holds_the_table_that_changes_nothing()
    {
        //Arrange
        EffectComposer composer = new EffectComposer(9);

        //Act
        composer.GetNode(4, 2, 8, out float red, out float green, out float blue);

        //Assert
        composer.NodeCount.Should().Be(729);
        red.Should().BeApproximately(0.5f, 1e-6f);
        green.Should().BeApproximately(0.25f, 1e-6f);
        blue.Should().Be(1f);
    }

    [Fact]
    public void Two_tables_compose_into_one_and_the_order_matters()
    {
        //Arrange - halve, then invert
        LutEffect halve = new LutEffect(Scale(17, 0.5f), "halve");
        LutEffect invert = new LutEffect(Invert(17), "invert");

        EffectComposer halveThenInvert = new EffectComposer(17);
        EffectComposer invertThenHalve = new EffectComposer(17);

        //Act
        halve.Compose(halveThenInvert);
        invert.Compose(halveThenInvert);

        invert.Compose(invertThenHalve);
        halve.Compose(invertThenHalve);

        //Assert - white in: halve gives 0.5 then invert gives 0.5; invert gives 0 then halve gives 0
        halveThenInvert.ToLut3D().Sample(1f, 1f, 1f, out float first, out _, out _);
        invertThenHalve.ToLut3D().Sample(1f, 1f, 1f, out float second, out _, out _);

        first.Should().BeApproximately(0.5f, 1e-4f);
        second.Should().Be(0f);
    }

    [Fact]
    public void An_arbitrary_colour_function_composes_like_a_table()
    {
        //Arrange
        EffectComposer composer = new EffectComposer(9);

        //Act - swap red and blue
        composer.Apply((ref float red, ref float green, ref float blue) =>
        {
            float keep = red;
            red = blue;
            blue = keep;
        });

        //Assert
        composer.ToLut3D().Sample(1f, 0.5f, 0f, out float outRed, out float outGreen, out float outBlue);
        outRed.Should().Be(0f);
        outGreen.Should().BeApproximately(0.5f, 1e-4f);
        outBlue.Should().Be(1f);
    }

    [Fact]
    public void Reset_puts_the_grid_back_to_identity()
    {
        //Arrange
        EffectComposer composer = new EffectComposer(5);
        composer.ApplyLut(Invert(5));

        //Act
        composer.Reset();

        //Assert
        composer.GetNode(4, 4, 4, out float red, out float green, out float blue);
        red.Should().Be(1f);
        green.Should().Be(1f);
        blue.Should().Be(1f);
    }

    [Fact]
    public void The_presenter_composes_its_chain_once_and_not_once_per_frame()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.Cpu,
            AllowEffectsOnCpu = true,
            EffectLutSize = 9,
        };

        presenter.Effects.Add(new LutEffect(Scale(9, 0.5f), "halve"));

        //Act
        Lut3D first = presenter.GetResultantLut();
        presenter.GetResultantLut();
        presenter.GetResultantLut();
        long afterThreeReads = presenter.GetStatistics().EffectCompositions;

        presenter.Effects.Add(new LutEffect(Invert(9), "invert"));
        Lut3D second = presenter.GetResultantLut();
        long afterTheChainChanged = presenter.GetStatistics().EffectCompositions;

        //Assert
        afterThreeReads.Should().Be(1L);
        afterTheChainChanged.Should().Be(2L);

        first.Sample(1f, 1f, 1f, out float halved, out _, out _);
        second.Sample(1f, 1f, 1f, out float halvedThenInverted, out _, out _);

        halved.Should().BeApproximately(0.5f, 1e-4f);
        halvedThenInverted.Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public void Removing_the_last_effect_leaves_no_resultant_table_at_all()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        LutEffect effect = new LutEffect(Invert(9));
        presenter.Effects.Add(effect);

        //Act
        presenter.Effects.Remove(effect);

        //Assert
        presenter.GetResultantLut().Should().BeNull();
        presenter.EffectsActive.Should().BeFalse();
    }

    [Fact]
    public void Changing_the_grid_size_recomposes_the_chain_at_the_new_size()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = VideoRenderPath.Cpu,
            AllowEffectsOnCpu = true,
        };

        presenter.Effects.Add(new LutEffect(Invert(9)));
        presenter.GetResultantLut().Size.Should().Be(SkiaVideoPresenter.DefaultEffectLutSize);

        //Act
        presenter.EffectLutSize = 17;
        Lut3D resized = presenter.GetResultantLut();

        //Assert
        resized.Size.Should().Be(17);
        presenter.GetStatistics().EffectCompositions.Should().Be(2L);
    }

    [Fact]
    public void A_grid_size_the_shader_could_not_carry_is_refused()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter();

        //Act
        Action tooSmall = () => presenter.EffectLutSize = 1;
        Action tooLarge = () => presenter.EffectLutSize = 200;

        //Assert
        tooSmall.Should().Throw<ArgumentOutOfRangeException>();
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
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
}
