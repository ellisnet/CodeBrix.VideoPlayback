using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class LutChainEntryTests
{
    [Fact]
    public void the_default_strength_is_forty_percent()
    {
        //Act
        LutChainEntry entry = new LutChainEntry("/tmp/one.cube");

        //Assert
        entry.ApplyAtPercent.Should().Be(LutChainEntry.DefaultApplyAtPercent);
        entry.ApplyAtPercent.Should().Be(40);
    }

    [Fact]
    public void a_strength_outside_the_range_is_clamped()
    {
        //Assert
        new LutChainEntry("/tmp/one.cube", 140).ApplyAtPercent.Should().Be(100);
        new LutChainEntry("/tmp/one.cube", -12).ApplyAtPercent.Should().Be(0);
        LutChainEntry.ClampPercent(double.NaN).Should().Be(0);
    }

    [Fact]
    public void TryParsePercent_reads_a_number_and_clamps_it()
    {
        //Act
        var read = LutChainEntry.TryParsePercent(" 62.5 ", out var percent);

        //Assert
        read.Should().BeTrue();
        percent.Should().Be(62.5);

        LutChainEntry.TryParsePercent("250", out var high).Should().BeTrue();
        high.Should().Be(100);

        LutChainEntry.TryParsePercent("not a number", out var none).Should().BeFalse();
        none.Should().Be(0);
    }

    [Fact]
    public void Signature_changes_with_the_file_and_with_the_strength()
    {
        //Act
        var one = new LutChainEntry("/tmp/one.cube", 40).Signature;
        var same = new LutChainEntry("/tmp/one.cube", 40).Signature;
        var louder = new LutChainEntry("/tmp/one.cube", 41).Signature;
        var other = new LutChainEntry("/tmp/two.cube", 40).Signature;

        //Assert
        one.Should().Be(same);
        one.Should().NotBe(louder);
        one.Should().NotBe(other);
    }
}
