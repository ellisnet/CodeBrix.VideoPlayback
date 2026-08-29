using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Assets;
using System.Linq;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class LutCatalogTests
{
    [Fact]
    public void Scan_reads_generated_and_found_and_never_invalid()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        temp.CreateCube(temp.CreateFolder("LUTs", "generated"), "sepia_33.cube", "Sepia 33");
        temp.CreateCube(temp.CreateFolder("LUTs", "found", "cameraluts"), "camera.cube", "Camera Look");
        temp.CreateCube(temp.CreateFolder("LUTs", "invalid"), "bad_cube.cube", "Broken On Purpose");

        //Act
        var entries = LutCatalog.Scan(luts);

        //Assert
        entries.Count.Should().Be(2);
        entries.Any(entry => entry.GroupName == LutCatalog.ExcludedFolderName).Should().BeFalse();
        entries.Any(entry => entry.FileName == "bad_cube.cube").Should().BeFalse();
    }

    [Fact]
    public void Scan_searches_the_found_folder_recursively()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        temp.CreateCube(temp.CreateFolder("LUTs", "found", "projectOne"), "one.cube", "One");
        temp.CreateCube(temp.CreateFolder("LUTs", "found", "projectTwo", "deeper"), "two.cube", "Two");

        //Act
        var entries = LutCatalog.Scan(luts);

        //Assert
        entries.Count.Should().Be(2);
    }

    [Fact]
    public void Scan_groups_generated_before_found()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        temp.CreateCube(temp.CreateFolder("LUTs", "found"), "aaa.cube", "AAA");
        temp.CreateCube(temp.CreateFolder("LUTs", "generated"), "zzz.cube", "ZZZ");

        //Act
        var entries = LutCatalog.Scan(luts);

        //Assert
        entries[0].GroupName.Should().Be("generated");
        entries[1].GroupName.Should().Be("found");
    }

    [Fact]
    public void Scan_shows_the_title_when_there_is_one_and_the_file_name_when_there_is_not()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        var generated = temp.CreateFolder("LUTs", "generated");
        temp.CreateCube(generated, "titled.cube", "A Proper Title");
        temp.CreateCube(generated, "untitled.cube", null);

        //Act
        var entries = LutCatalog.Scan(luts);

        //Assert
        entries.Single(entry => entry.FileName == "titled.cube").DisplayName.Should().Be("A Proper Title");
        entries.Single(entry => entry.FileName == "untitled.cube").DisplayName.Should().Be("untitled.cube");
    }

    [Fact]
    public void ParseTitle_reads_a_quoted_title_and_ignores_a_trailing_comment()
    {
        //Assert
        LutCatalog.ParseTitle("TITLE \"1D Identity Cube LUT\" # comment here").Should().Be("1D Identity Cube LUT");
        LutCatalog.ParseTitle("  TITLE \"Sepia 33\"  ").Should().Be("Sepia 33");
        LutCatalog.ParseTitle("TITLE Unquoted Title #comment").Should().Be("Unquoted Title");
        LutCatalog.ParseTitle("LUT_3D_SIZE 33").Should().BeNull();
        LutCatalog.ParseTitle("# TITLE \"in a comment\"").Should().BeNull();
        LutCatalog.ParseTitle(null).Should().BeNull();
    }

    [Fact]
    public void MatchIndex_finds_an_exact_file_name_first()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        var generated = temp.CreateFolder("LUTs", "generated");
        temp.CreateCube(generated, "cool_33.cube", "Cool 33");
        temp.CreateCube(generated, "very_cool_33.cube", "Very Cool 33");
        var entries = LutCatalog.Scan(luts);

        //Act
        var index = LutCatalog.MatchIndex(entries, "cool_33.cube");

        //Assert
        entries[index].FileName.Should().Be("cool_33.cube");
    }

    [Fact]
    public void MatchIndex_falls_back_to_part_of_a_name_or_a_title()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        temp.CreateCube(temp.CreateFolder("LUTs", "generated"), "teal_orange_strong_33.cube", "Teal and Orange");
        var entries = LutCatalog.Scan(luts);

        //Act
        var byPartOfName = LutCatalog.MatchIndex(entries, "teal_orange");
        var byTitle = LutCatalog.MatchIndex(entries, "Orange");

        //Assert
        byPartOfName.Should().Be(0);
        byTitle.Should().Be(0);
    }

    [Fact]
    public void MatchIndex_says_minus_one_when_nothing_matches()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var luts = temp.CreateFolder("LUTs");
        temp.CreateCube(temp.CreateFolder("LUTs", "generated"), "sepia_33.cube", "Sepia 33");
        var entries = LutCatalog.Scan(luts);

        //Act and assert
        //  "sepia_33.cube@40" is what a mis-split command line produces: it must NOT match sepia_33.cube.
        LutCatalog.MatchIndex(entries, "sepia_33.cube@40").Should().Be(-1);
        LutCatalog.MatchIndex(entries, "no_such_table.cube").Should().Be(-1);
        LutCatalog.MatchIndex(entries, "  ").Should().Be(-1);
        LutCatalog.MatchIndex(null, "sepia_33.cube").Should().Be(-1);
    }

    [Fact]
    public void CreateExternalEntry_describes_a_cube_file_from_outside_the_corpus()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var baked = temp.CreateCube(temp.CreateFolder("baked"), "chain.cube", "SimpleCbxVideoPlayer: sepia_33@40");

        //Act
        LutCatalogEntry entry = LutCatalog.CreateExternalEntry(baked);

        //Assert
        entry.Should().NotBeNull();
        entry.GroupName.Should().Be(LutCatalog.ExternalGroupName);
        entry.FileName.Should().Be("chain.cube");
        entry.DisplayName.Should().Be("SimpleCbxVideoPlayer: sepia_33@40");
    }

    [Fact]
    public void CreateExternalEntry_refuses_anything_that_is_not_a_cube_file_that_exists()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var notACube = temp.CreateFile(temp.Path, "notes.txt", "hello");

        //Assert
        LutCatalog.CreateExternalEntry(notACube).Should().BeNull();
        LutCatalog.CreateExternalEntry("/no/such/file.cube").Should().BeNull();
        LutCatalog.CreateExternalEntry("sepia_33.cube@40").Should().BeNull();
        LutCatalog.CreateExternalEntry("   ").Should().BeNull();
    }

    [Fact]
    public void Scan_of_the_real_corpus_finds_the_twenty_one_usable_tables()
    {
        //Arrange
        var root = RepositoryAssets.FindRepositoryRoot();
        Assert.SkipWhen(root == null, "This test reads the repository's own corpus, which is not beside the test assembly.");

        //Act
        var entries = LutCatalog.Scan(RepositoryAssets.GetLutsFolder(root));

        //Assert
        entries.Count.Should().Be(21);
        entries.Count(entry => entry.GroupName == "generated").Should().Be(12);
        entries.Count(entry => entry.GroupName == "found").Should().Be(9);
        entries.Any(entry => entry.FullPath.Contains("invalid")).Should().BeFalse();
        entries.Single(entry => entry.FileName == "sepia_33.cube").DisplayName.Should().Be("Sepia 33");
    }
}
