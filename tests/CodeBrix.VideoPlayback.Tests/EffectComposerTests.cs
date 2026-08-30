using System;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Effects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks that a chain of effects folds into ONE resultant table, in order, and only when the chain changes.
/// </summary>
public class EffectComposerTests
{
    [Fact]
    public void A_chain_composes_to_a_table_with_no_presenter_anywhere_near_it()
    {
        //Arrange - the whole point: no presenter, no graphics context, no frame, no window.
        //  An application that only picks tables and bakes the result needs none of them.
        LutEffect halve = new LutEffect(TestLuts.Scale(17, 0.5f), "halve");
        LutEffect invert = new LutEffect(TestLuts.Invert(17), "invert");

        //Act
        Lut3D composed = EffectComposer.Compose([halve, invert], 17);

        //Assert - the same table the long-hand composer produces
        EffectComposer longHand = new EffectComposer(17);
        halve.Compose(longHand);
        invert.Compose(longHand);

        composed.Should().NotBeNull();
        composed.Size.Should().Be(17);
        composed.Values.ToArray().Should().Equal(longHand.ToLut3D().Values.ToArray());
    }

    [Fact]
    public void Compose_of_nothing_is_null_rather_than_an_identity_table()
    {
        //Act
        Lut3D fromNull = EffectComposer.Compose(null);
        Lut3D fromEmpty = EffectComposer.Compose([]);

        //Assert - nothing to compose is not the same as "a table that changes nothing"
        fromNull.Should().BeNull();
        fromEmpty.Should().BeNull();
    }

    [Fact]
    public void Compose_defaults_to_the_size_playback_composes_at()
    {
        //Arrange
        LutEffect halve = new LutEffect(TestLuts.Scale(17, 0.5f), "halve");

        //Act
        Lut3D composed = EffectComposer.Compose([halve]);

        //Assert - so a baked file and a played chain agree without anyone naming a size
        composed.Size.Should().Be(EffectComposer.DefaultSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-8)]
    [InlineData(4096)]
    public void A_grid_outside_the_allowed_range_is_refused(int size)
    {
        //Act
        Action creating = () => _ = new EffectComposer(size);

        //Assert
        creating.Should().Throw<ArgumentOutOfRangeException>();
    }

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
        LutEffect halve = new LutEffect(TestLuts.Scale(17, 0.5f), "halve");
        LutEffect invert = new LutEffect(TestLuts.Invert(17), "invert");

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
        composer.ApplyLut(TestLuts.Invert(5));

        //Act
        composer.Reset();

        //Assert
        composer.GetNode(4, 4, 4, out float red, out float green, out float blue);
        red.Should().Be(1f);
        green.Should().Be(1f);
        blue.Should().Be(1f);
    }
}
