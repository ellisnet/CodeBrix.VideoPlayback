using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class LutChainTests
{
    [Fact]
    public void TrySet_reports_a_change_the_first_time_and_not_the_second()
    {
        //Arrange
        LutChain chain = new LutChain();

        //Act
        var first = chain.TrySet([new LutChainEntry("/tmp/one.cube", 40)]);
        var second = chain.TrySet([new LutChainEntry("/tmp/one.cube", 40)]);

        //Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        chain.ChangeCount.Should().Be(1);
    }

    [Fact]
    public void TrySet_reports_a_change_when_a_percentage_moves()
    {
        //Arrange
        LutChain chain = new LutChain();
        chain.TrySet([new LutChainEntry("/tmp/one.cube", 40)]);

        //Act
        var changed = chain.TrySet([new LutChainEntry("/tmp/one.cube", 60)]);

        //Assert
        changed.Should().BeTrue();
        chain.Entries[0].ApplyAtPercent.Should().Be(60);
    }

    [Fact]
    public void TrySet_reports_a_change_when_the_order_moves()
    {
        //Arrange
        LutChain chain = new LutChain();
        chain.TrySet([new LutChainEntry("/tmp/one.cube", 40), new LutChainEntry("/tmp/two.cube", 40)]);

        //Act
        var changed = chain.TrySet([new LutChainEntry("/tmp/two.cube", 40), new LutChainEntry("/tmp/one.cube", 40)]);

        //Assert
        changed.Should().BeTrue();
        chain.Entries[0].FileName.Should().Be("two.cube");
    }

    [Fact]
    public void TrySet_of_nothing_clears_the_chain_once()
    {
        //Arrange
        LutChain chain = new LutChain();
        chain.TrySet([new LutChainEntry("/tmp/one.cube", 40)]);

        //Act
        var cleared = chain.TrySet(null);
        var again = chain.TrySet([]);

        //Assert
        cleared.Should().BeTrue();
        again.Should().BeFalse();
        chain.Entries.Count.Should().Be(0);
        chain.Signature.Should().Be(string.Empty);
    }
}
