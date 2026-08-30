using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using System;
using System.IO;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class BakeLocationsTests
{
    [Fact]
    public void CreateFileName_stamps_the_moment_it_was_made()
    {
        //Arrange
        DateTime made = new DateTime(2026, 8, 29, 14, 15, 30, DateTimeKind.Local);

        //Act
        var name = BakeLocations.CreateFileName(made);

        //Assert
        name.Should().Be("chain-20260829-141530.cube");
    }

    [Fact]
    public void CreateFileName_is_a_bare_name_and_never_a_location()
    {
        //Act - a suggestion for a save dialog, not somewhere the application picked on its own
        var name = BakeLocations.CreateFileName(new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Local));

        //Assert
        Path.GetDirectoryName(name).Should().BeEmpty();
        Path.IsPathRooted(name).Should().BeFalse();
        Path.GetExtension(name).Should().Be(BakeLocations.LutFileExtension);
    }

    [Fact]
    public void Two_bakes_a_second_apart_do_not_propose_the_same_file()
    {
        //Arrange
        DateTime first = new DateTime(2026, 8, 29, 14, 15, 30, DateTimeKind.Local);

        //Act
        var earlier = BakeLocations.CreateFileName(first);
        var later = BakeLocations.CreateFileName(first.AddSeconds(1));

        //Assert
        later.Should().NotBe(earlier);
    }
}
