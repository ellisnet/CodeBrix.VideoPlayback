using System;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Reads every ".cube" file in the corpus under <c>tests/assets/LUTs</c> with this repository's own reader.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is the answer to "does this reader cope with the files that exist", as opposed to the files a
/// test writes for itself: nine tables collected from six licence-cleared open projects - a real 33-cubed
/// camera transform, two creative looks printed at seventeen significant digits with no header at all, an
/// unquoted TITLE, a file with no trailing newline, a title with a comment after it, and a COMBINED shaper
/// plus table - and twelve written by the generator beside them.
/// </para>
/// <para>
/// Every test here skips itself, naming the folder, when the corpus is not in the checkout.
/// </para>
/// </remarks>
public class LutCorpusTests
{
    [Theory]
    [MemberData(nameof(LutTestAssets.EveryCubeFile), MemberType = typeof(LutTestAssets))]
    public void Every_cube_file_in_the_corpus_is_read(string relativePath)
    {
        //Arrange
        string path = LutTestAssets.Path(relativePath);

        //Act
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Assert
        cube.Should().NotBeNull();
        cube.Name.Should().NotBeNullOrEmpty();

        if (cube.Lut3D != null)
        {
            cube.Lut3D.Values.Length.Should().Be(cube.Lut3D.Size * cube.Lut3D.Size * cube.Lut3D.Size * 3);
        }

        if (cube.Lut1D != null)
        {
            cube.Lut1D.Red.Length.Should().Be(cube.Lut1D.Size);
        }

        (cube.Lut3D != null || cube.Lut1D != null).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(LutTestAssets.EveryCubeFile), MemberType = typeof(LutTestAssets))]
    public void Every_cube_file_in_the_corpus_survives_being_written_and_read_again(string relativePath)
    {
        //Arrange
        string path = LutTestAssets.Path(relativePath);
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Act - a combined file is written back as its two halves, which is what this reader can express
        if (cube.Lut3D != null)
        {
            CubeLut again = CubeLutFile.Parse(CubeLutFile.ToText(cube.Lut3D, cube.Title), "again");
            again.Lut3D.Size.Should().Be(cube.Lut3D.Size);
            again.Lut3D.Values.ToArray().Should().Equal(cube.Lut3D.Values.ToArray());
            again.Lut3D.DomainMinimum.ToArray().Should().Equal(cube.Lut3D.DomainMinimum.ToArray());
            again.Lut3D.DomainMaximum.ToArray().Should().Equal(cube.Lut3D.DomainMaximum.ToArray());
        }

        //Assert
        if (cube.Lut1D != null)
        {
            CubeLut again = CubeLutFile.Parse(CubeLutFile.ToText(cube.Lut1D, cube.Title), "again");
            again.Lut1D.Size.Should().Be(cube.Lut1D.Size);
            again.Lut1D.Red.ToArray().Should().Equal(cube.Lut1D.Red.ToArray());
            again.Lut1D.Green.ToArray().Should().Equal(cube.Lut1D.Green.ToArray());
            again.Lut1D.Blue.ToArray().Should().Equal(cube.Lut1D.Blue.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(LutTestAssets.EveryInvalidCubeFile), MemberType = typeof(LutTestAssets))]
    public void Every_broken_cube_file_is_refused_by_name_rather_than_half_read(string relativePath)
    {
        //Arrange
        string path = LutTestAssets.Path(relativePath);

        //Act
        Exception thrown = Record.Exception(() => CubeLutFile.ReadFile(path));

        //Assert - a specific, explained refusal, never a crash and never a half-filled table
        thrown.Should().NotBeNull();
        thrown.Should().BeOfType<InvalidDataException>();
        thrown.Message.Should().NotBeNullOrEmpty();
        thrown.Message.Should().Contain(".cube");
    }

    [Fact]
    public void The_two_identical_generated_tables_read_to_the_same_numbers_whatever_their_line_endings()
    {
        //Arrange
        LutTestAssets.SkipWhenAbsent("generated");
        string lineFeed = LutTestAssets.Path("generated/identity_33.cube");
        string carriageReturn = LutTestAssets.Path("generated/crlf_variant_33.cube");

        //Act
        CubeLut first = CubeLutFile.ReadFile(lineFeed);
        CubeLut second = CubeLutFile.ReadFile(carriageReturn);

        //Assert
        second.Lut3D.Size.Should().Be(first.Lut3D.Size);
        second.Lut3D.Values.ToArray().Should().Equal(first.Lut3D.Values.ToArray());
    }

    [Fact]
    public void The_corpus_file_with_a_declared_domain_is_applied_over_that_domain()
    {
        //Arrange - the file declares -0.5 to 1.5 and clamps that range into 0..1
        string path = LutTestAssets.Path("generated/domain_test_33.cube");

        //Act
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Assert
        cube.Lut3D.HasDefaultDomain.Should().BeFalse();
        cube.Lut3D.DomainMinimum[0].Should().BeApproximately(-0.5f, 1e-6f);
        cube.Lut3D.DomainMaximum[0].Should().BeApproximately(1.5f, 1e-6f);

        // The declared domain puts input 0 at three quarters of the way along the first quarter of the
        // cube, where the file's own clamp(-0.5 + 2 * i / 32) is still 0.
        cube.Lut3D.Sample(0f, 0f, 0f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0f, 1e-5f);
        green.Should().BeApproximately(0f, 1e-5f);
        blue.Should().BeApproximately(0f, 1e-5f);

        cube.Lut3D.Sample(1f, 1f, 1f, out red, out green, out blue);
        red.Should().BeApproximately(1f, 1e-5f);
        green.Should().BeApproximately(1f, 1e-5f);
        blue.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void The_corpus_combined_file_is_read_as_a_shaper_and_the_table_it_feeds()
    {
        //Arrange
        string path = LutTestAssets.Path("found/smol-cube/shaper_3d.cube");

        //Act
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Assert
        cube.IsCombined.Should().BeTrue();
        cube.Lut1D.Size.Should().Be(3);
        cube.Lut3D.Size.Should().Be(2);

        // The shaper's own input range, and the table's, are different and both are read.
        cube.Lut1D.DomainMinimum[0].Should().BeApproximately(0.00123f, 1e-7f);
        cube.Lut1D.DomainMaximum[0].Should().BeApproximately(0.98765f, 1e-7f);
        cube.Lut3D.DomainMinimum[0].Should().BeApproximately(0.0927061729f, 1e-7f);
        cube.Lut3D.DomainMaximum[0].Should().BeApproximately(1.0196260353f, 1e-7f);

        // The first three rows are the shaper's, the last eight the table's - and the table's values run
        // well outside 0 to 1, which is ordinary for an output.
        cube.Lut1D.Red[0].Should().BeApproximately(0.12345f, 1e-7f);
        cube.Lut1D.Blue[2].Should().BeApproximately(0.87654f, 1e-7f);
        cube.Lut3D.Values[(7 * 3) + 2].Should().BeApproximately(13.5f, 1e-5f);

        // It becomes ONE layer of a chain, not two.
        LutLayer layer = cube.ToLayer();
        layer.IsShaped.Should().BeTrue();
        layer.IsThreeDimensional.Should().BeTrue();
        layer.Size.Should().Be(2);
    }

    [Fact]
    public void The_corpus_file_whose_title_carries_a_comment_reads_the_title_alone()
    {
        //Arrange
        string path = LutTestAssets.Path("found/cube-lut-factory.js/1DLUT.cube");

        //Act
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Assert
        cube.Title.Should().Be("1D Identity Cube LUT");
        cube.Name.Should().Be("1D Identity Cube LUT");
        cube.Lut1D.Size.Should().Be(4);
    }

    [Fact]
    public void A_corpus_table_composes_with_itself_at_a_hundred_percent_to_itself()
    {
        //Arrange
        string path = LutTestAssets.Path("found/dart-lut/exmp_linear.cube");
        CubeLut cube = CubeLutFile.ReadFile(path);

        //Act
        Lut3D effective = LutComposer.Compose(
            new[] { cube.ToLayer() },
            new LutComposerOptions { OutputSize = cube.Lut3D.Size });

        //Assert
        for (int index = 0; index < effective.Values.Length; index++)
        {
            effective.Values[index].Should().BeApproximately(cube.Lut3D.Values[index], 1e-5f);
        }
    }
}
