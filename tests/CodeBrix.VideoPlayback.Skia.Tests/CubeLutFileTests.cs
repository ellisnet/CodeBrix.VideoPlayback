using System;
using System.IO;
using CodeBrix.VideoPlayback.Skia.Effects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>Checks the ".cube" reader against the shapes those files actually take.</summary>
public class CubeLutFileTests
{
    [Fact]
    public void A_three_dimensional_table_is_read_with_red_changing_fastest()
    {
        //Arrange
        string text = string.Join(
            '\n',
            "# a two-node identity table",
            "TITLE \"two nodes\"",
            "LUT_3D_SIZE 2",
            string.Empty,
            "0.0 0.0 0.0",
            "1.0 0.0 0.0",
            "0.0 1.0 0.0",
            "1.0 1.0 0.0",
            "0.0 0.0 1.0",
            "1.0 0.0 1.0",
            "0.0 1.0 1.0",
            "1.0 1.0 1.0");

        //Act
        LutEffect effect = CubeLutFile.Parse(text, "fallback");

        //Assert
        effect.Name.Should().Be("two nodes");
        effect.Lut3D.Size.Should().Be(2);
        effect.Lut1D.Should().BeNull();

        effect.Lut3D.Sample(0.25f, 0.5f, 1f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void A_one_dimensional_table_becomes_three_curves()
    {
        //Arrange
        string text = string.Join('\n', "LUT_1D_SIZE 3", "0 0 0", "0.25 0.5 0.75", "1 1 1");

        //Act
        LutEffect effect = CubeLutFile.Parse(text, "curves");

        //Assert
        effect.Lut3D.Should().BeNull();
        effect.Lut1D.Size.Should().Be(3);
        effect.Name.Should().Be("curves");

        effect.Lut1D.Sample(0.5f, 0.5f, 0.5f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void A_domain_this_reader_does_not_apply_is_refused_rather_than_ignored()
    {
        //Arrange
        string text = string.Join(
            '\n',
            "LUT_3D_SIZE 2",
            "DOMAIN_MIN 0 0 0",
            "DOMAIN_MAX 4 4 4",
            "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1");

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*input domain*");
    }

    [Fact]
    public void A_row_count_that_does_not_match_the_stated_size_is_named_as_the_fault()
    {
        //Arrange
        string text = string.Join('\n', "LUT_3D_SIZE 2", "0 0 0", "1 0 0");

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*needs 8 rows*");
    }

    [Fact]
    public void Text_that_states_no_size_at_all_is_not_a_cube_file()
    {
        //Arrange
        string text = "0 0 0\n1 1 1";

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*neither LUT_3D_SIZE nor LUT_1D_SIZE*");
    }

    [Fact]
    public void A_missing_file_is_reported_by_path()
    {
        //Arrange
        string path = Path.Combine(Path.GetTempPath(), "codebrix-there-is-no-such-lut.cube");

        //Act
        Action read = () => CubeLutFile.ReadFile(path);

        //Assert
        read.Should().Throw<FileNotFoundException>().WithMessage("*there is no .cube lookup-table file*");
    }

    [Fact]
    public void A_file_on_disk_is_read_and_named_after_itself_when_it_has_no_title()
    {
        //Arrange
        string directory = Path.Combine(Path.GetTempPath(), "codebrix-cube-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "warm-grade.cube");
        File.WriteAllText(
            path,
            string.Join('\n', "LUT_1D_SIZE 2", "0 0 0", "1 1 1"));

        try
        {
            //Act
            LutEffect effect = CubeLutFile.ReadFile(path);

            //Assert
            effect.Name.Should().Be("warm-grade");
            effect.Lut1D.Size.Should().Be(2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
