using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Assets;
using System.IO;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class RepositoryAssetsTests
{
    [Fact]
    public void FindRepositoryRoot_walks_up_to_the_folder_holding_the_corpus()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        temp.CreateFolder("tests", "assets", "authoring");
        var deep = temp.CreateFolder("samples", "App", "src", "App.LinuxX11", "bin", "Release", "net10.0");

        //Act
        var root = RepositoryAssets.FindRepositoryRoot(deep);

        //Assert
        root.Should().Be(temp.Path);
    }

    [Fact]
    public void FindRepositoryRoot_returns_null_when_no_ancestor_holds_the_corpus()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var folder = temp.CreateFolder("nothing", "here");

        //Act
        var root = RepositoryAssets.FindRepositoryRoot(folder);

        //Assert
        root.Should().BeNull();
    }

    [Fact]
    public void FindRepositoryRoot_returns_null_for_a_blank_start()
    {
        //Act
        var root = RepositoryAssets.FindRepositoryRoot("  ");

        //Assert
        root.Should().BeNull();
    }

    [Fact]
    public void GetAuthoringFolder_and_GetLutsFolder_land_inside_the_root()
    {
        //Arrange
        var root = Path.Combine("a", "b");

        //Act
        var authoring = RepositoryAssets.GetAuthoringFolder(root);
        var luts = RepositoryAssets.GetLutsFolder(root);

        //Assert
        authoring.Should().Be(Path.Combine(root, "tests", "assets", "authoring"));
        luts.Should().Be(Path.Combine(root, "tests", "assets", "LUTs"));
    }
}
