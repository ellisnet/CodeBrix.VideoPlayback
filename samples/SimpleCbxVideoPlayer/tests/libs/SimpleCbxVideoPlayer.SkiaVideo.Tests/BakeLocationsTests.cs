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
    public void CreateFilePath_lands_in_the_baked_luts_folder_beside_the_application()
    {
        //Act
        var path = BakeLocations.CreateFilePath(new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Local));

        //Assert
        Path.GetDirectoryName(path).Should().Be(BakeLocations.DefaultFolder);
        BakeLocations.DefaultFolder.Should().Contain(BakeLocations.FolderName);
        Path.GetExtension(path).Should().Be(BakeLocations.LutFileExtension);
    }
}
