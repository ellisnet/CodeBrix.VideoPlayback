using System;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

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
        CubeLut cube = CubeLutFile.Parse(text, "fallback");

        //Assert
        cube.Name.Should().Be("two nodes");
        cube.Lut3D.Size.Should().Be(2);
        cube.Lut1D.Should().BeNull();

        cube.Lut3D.Sample(0.25f, 0.5f, 1f, out float red, out float green, out float blue);
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
        CubeLut cube = CubeLutFile.Parse(text, "curves");

        //Assert
        cube.Lut3D.Should().BeNull();
        cube.Lut1D.Size.Should().Be(3);
        cube.Name.Should().Be("curves");

        cube.Lut1D.Sample(0.5f, 0.5f, 0.5f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().BeApproximately(0.75f, 1e-5f);
    }

    [Fact]
    public void A_domain_wider_than_zero_to_one_is_applied_rather_than_refused()
    {
        //Arrange - an identity cube declared over 0 to 4, so an ordinary picture uses its bottom quarter
        string text = string.Join(
            '\n',
            "LUT_3D_SIZE 2",
            "DOMAIN_MIN 0 0 0",
            "DOMAIN_MAX 4 4 4",
            "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1");

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.Lut3D.HasDefaultDomain.Should().BeFalse();
        cube.Lut3D.DomainMaximum[0].Should().Be(4f);

        cube.Lut3D.Sample(1f, 2f, 4f, out float red, out float green, out float blue);
        red.Should().BeApproximately(0.25f, 1e-5f);
        green.Should().BeApproximately(0.5f, 1e-5f);
        blue.Should().Be(1f);
    }

    [Fact]
    public void A_domain_that_does_not_rise_is_refused_by_name()
    {
        //Arrange
        string text = string.Join(
            '\n',
            "LUT_3D_SIZE 2",
            "DOMAIN_MIN 1 0 0",
            "DOMAIN_MAX 1 1 1",
            "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1");

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*does not rise*");
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
            CubeLut cube = CubeLutFile.ReadFile(path);

            //Assert
            cube.Name.Should().Be("warm-grade");
            cube.Lut1D.Size.Should().Be(2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_title_with_a_comment_after_it_reads_as_the_title_alone()
    {
        //Arrange - the shape found in the wild: a quoted title, then a '#' note on the same line
        string text = string.Join(
            '\n',
            "TITLE \"1D Identity Cube LUT\" # comment here",
            "LUT_1D_SIZE 2",
            "0 0 0",
            "1 1 1");

        //Act
        CubeLut cube = CubeLutFile.Parse(text, "fallback");

        //Assert
        cube.Title.Should().Be("1D Identity Cube LUT");
        cube.Name.Should().Be("1D Identity Cube LUT");
    }

    [Fact]
    public void An_unquoted_title_with_a_comment_after_it_reads_as_the_title_alone()
    {
        //Arrange
        string text = string.Join('\n', "TITLE example # and a note", "LUT_3D_SIZE 2", Identity2());

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.Title.Should().Be("example");
    }

    [Fact]
    public void A_comment_part_way_along_a_keyword_or_a_row_is_ignored()
    {
        //Arrange
        string text = string.Join(
            '\n',
            "LUT_3D_SIZE 2 # two nodes",
            "DOMAIN_MIN 0 0 0   # the usual",
            "0 0 0",
            "1 0 0 # red",
            "0 1 0",
            "1 1 0",
            "0 0 1",
            "1 0 1",
            "0 1 1",
            "1 1 1");

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.Lut3D.Size.Should().Be(2);
        cube.Lut3D.HasDefaultDomain.Should().BeTrue();
    }

    [Fact]
    public void Carriage_returns_and_a_missing_last_newline_are_both_ordinary()
    {
        //Arrange - CRLF throughout, and the last row ends at end of file
        string text = string.Join(
            "\r\n",
            "# a carriage-return file",
            "LUT_3D_SIZE 2",
            string.Empty,
            "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1");

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.Lut3D.Size.Should().Be(2);
        cube.Lut3D.Values[21].Should().Be(1f);
    }

    [Fact]
    public void A_combined_shaper_and_table_is_read_as_both()
    {
        //Arrange - a 1-D shaper declared first, then the 3-D table it feeds
        string text = string.Join(
            '\n',
            "# a shaper and the table it feeds",
            "LUT_1D_SIZE 2",
            "LUT_1D_INPUT_RANGE 0.25 0.75",
            "LUT_3D_SIZE 2",
            "LUT_3D_INPUT_RANGE 0.0 2.0",
            "0 0 0",
            "1 1 1",
            Identity2());

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.IsCombined.Should().BeTrue();
        cube.Lut1D.Size.Should().Be(2);
        cube.Lut1D.DomainMinimum[0].Should().BeApproximately(0.25f, 1e-6f);
        cube.Lut1D.DomainMaximum[0].Should().BeApproximately(0.75f, 1e-6f);
        cube.Lut3D.Size.Should().Be(2);
        cube.Lut3D.DomainMaximum[0].Should().Be(2f);
    }

    [Fact]
    public void A_combined_file_whose_row_count_matches_neither_half_is_refused()
    {
        //Arrange
        string text = string.Join('\n', "LUT_1D_SIZE 2", "LUT_3D_SIZE 2", "0 0 0", "1 1 1");

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*a shaper and the table it feeds*");
    }

    [Fact]
    public void A_size_no_table_could_have_is_refused_before_anything_is_counted()
    {
        //Arrange - 2000 cubed times three overflows a 32-bit count, which used to go unnoticed
        string huge = string.Join('\n', "LUT_3D_SIZE 2000", "0 0 0");
        string tiny = string.Join('\n', "LUT_3D_SIZE 1", "0 0 0");

        //Act
        Action tooLarge = () => CubeLutFile.Parse(huge, null);
        Action tooSmall = () => CubeLutFile.Parse(tiny, null);

        //Assert
        tooLarge.Should().Throw<InvalidDataException>().WithMessage("*accepts 2 to 129*");
        tooSmall.Should().Throw<InvalidDataException>().WithMessage("*accepts 2 to 129*");
    }

    [Fact]
    public void A_row_that_is_not_a_number_names_its_line_and_what_it_holds()
    {
        //Arrange
        string text = string.Join('\n', "LUT_3D_SIZE 2", "0 0 0", "1 0 zero", "0 1 0");

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*Line 3*zero*finite number*");
    }

    [Fact]
    public void A_row_holding_not_a_number_or_infinity_never_reaches_a_table()
    {
        //Arrange
        string notANumber = string.Join('\n', "LUT_3D_SIZE 2", "NaN NaN NaN", "1 0 0");
        string endless = string.Join('\n', "LUT_1D_SIZE 2", "0 0 0", "Infinity 1 1");
        string negative = string.Join('\n', "LUT_1D_SIZE 2", "0 0 0", "-Infinity 1 1");

        //Act
        Action first = () => CubeLutFile.Parse(notANumber, null);
        Action second = () => CubeLutFile.Parse(endless, null);
        Action third = () => CubeLutFile.Parse(negative, null);

        //Assert
        first.Should().Throw<InvalidDataException>().WithMessage("*finite number*");
        second.Should().Throw<InvalidDataException>().WithMessage("*finite number*");
        third.Should().Throw<InvalidDataException>().WithMessage("*finite number*");
    }

    [Fact]
    public void A_domain_that_is_not_a_finite_number_is_refused()
    {
        //Arrange
        string text = string.Join(
            '\n',
            "LUT_3D_SIZE 2",
            "DOMAIN_MAX NaN NaN NaN",
            Identity2());

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*finite number*");
    }

    [Fact]
    public void A_word_that_merely_begins_with_a_keyword_is_not_that_keyword()
    {
        //Arrange - TITLED is not TITLE, and there is no row it could be either
        string text = string.Join('\n', "TITLED nonsense", "LUT_3D_SIZE 2", Identity2());

        //Act
        Action parse = () => CubeLutFile.Parse(text, null);

        //Assert
        parse.Should().Throw<InvalidDataException>().WithMessage("*Line 1*3 numbers*");
    }

    [Fact]
    public void An_input_range_keyword_states_the_domain_just_as_the_domain_keywords_do()
    {
        //Arrange
        string text = string.Join('\n', "LUT_3D_SIZE 2", "LUT_3D_INPUT_RANGE -1 3", Identity2());

        //Act
        CubeLut cube = CubeLutFile.Parse(text, null);

        //Assert
        cube.Lut3D.DomainMinimum[2].Should().Be(-1f);
        cube.Lut3D.DomainMaximum[2].Should().Be(3f);
    }

    private static string Identity2() =>
        string.Join('\n', "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1");
}
