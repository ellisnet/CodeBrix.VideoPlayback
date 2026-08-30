using System.Collections.Generic;
using System.Linq;
using CodeBrix.VideoPlayback.Effects;
using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class LutEffectFactoryTests
{
    [Fact]
    public void Build_keeps_the_chain_in_order_and_carries_each_percentage()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var folder = temp.CreateFolder("luts");
        var first = temp.CreateCube(folder, "first.cube", "First");
        var second = temp.CreateCube(folder, "second.cube", "Second");

        List<LutChainEntry> entries = [new LutChainEntry(first, 40), new LutChainEntry(second, 75)];

        //Act
        var effects = LutEffectFactory.Build(entries, out var failures);

        //Assert
        failures.Count.Should().Be(0);
        effects.Count.Should().Be(2);
        effects.OfType<LutEffect>().First().ApplyAtPercent.Should().Be(40);
        effects.OfType<LutEffect>().Last().ApplyAtPercent.Should().Be(75);
    }

    [Fact]
    public void Build_reports_a_file_it_cannot_read_rather_than_throwing()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var folder = temp.CreateFolder("luts");
        var good = temp.CreateCube(folder, "good.cube", "Good");
        var broken = temp.CreateFile(folder, "broken.cube", "TITLE \"Broken\"\nLUT_3D_SIZE 2\n0.0 0.0\n");

        //Act
        var effects = LutEffectFactory.Build([new LutChainEntry(good), new LutChainEntry(broken)], out var failures);

        //Assert
        effects.Count.Should().Be(1);
        failures.Count.Should().Be(1);
        failures[0].Should().Contain("broken.cube");
    }

    [Fact]
    public void Build_of_nothing_is_an_empty_chain()
    {
        //Act
        var effects = LutEffectFactory.Build(null, out var failures);

        //Assert
        effects.Count.Should().Be(0);
        failures.Count.Should().Be(0);
    }
}
