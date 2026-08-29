using System;
using System.Globalization;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the ".cube" writer: what it emits, that it never emits an exponent, and that a table written and
/// read again is the very same table.
/// </summary>
/// <remarks>
/// The writer is what the authoring pipeline uses to hand a composed grade to FFmpeg, so a number that does
/// not survive the trip is a graded picture that does not match the one played back.
/// </remarks>
public class CubeLutWriterTests
{
    [Fact]
    public void A_written_table_states_its_size_first_and_its_rows_red_fastest()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(2);

        //Act
        string text = CubeLutFile.ToText(lut, "two nodes");

        //Assert
        string[] lines = text.Split('\n');
        lines[0].Should().Be(CubeLutFile.GeneratorComment);
        lines[1].Should().Be("TITLE \"two nodes\"");
        lines[2].Should().Be("LUT_3D_SIZE 2");
        lines[3].Should().Be(string.Empty);
        lines[4].Should().Be("0.0 0.0 0.0");
        lines[5].Should().Be("1.0 0.0 0.0");
        lines[6].Should().Be("0.0 1.0 0.0");
        lines[11].Should().Be("1.0 1.0 1.0");
    }

    [Fact]
    public void A_written_file_uses_line_feeds_and_never_carriage_returns()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(3);

        //Act
        string text = CubeLutFile.ToText(lut, null);

        //Assert
        text.Should().NotContain("\r");
        text.Should().Contain("\n");
    }

    [Fact]
    public void The_default_domain_is_left_unstated_and_any_other_is_stated()
    {
        //Arrange
        Lut3D plain = Lut3D.CreateIdentity(2);
        Lut3D wide = new Lut3D(
            2,
            Lut3D.CreateIdentity(2).Values.ToArray(),
            new[] { -0.5f, -0.5f, -0.5f },
            new[] { 1.5f, 1.5f, 1.5f });

        //Act
        string plainText = CubeLutFile.ToText(plain, null);
        string wideText = CubeLutFile.ToText(wide, null);

        //Assert
        plainText.Should().NotContain("DOMAIN_MIN");
        plainText.Should().NotContain("DOMAIN_MAX");
        wideText.Should().Contain("DOMAIN_MIN -0.5 -0.5 -0.5");
        wideText.Should().Contain("DOMAIN_MAX 1.5 1.5 1.5");
    }

    [Fact]
    public void No_number_is_ever_written_in_exponent_notation()
    {
        //Arrange - values chosen so that the runtime's own shortest form IS an exponent
        float[] values = new float[2 * 2 * 2 * 3];
        values[0] = 1.2e-9f;
        values[1] = 3.4e-30f;
        values[2] = -5.6e-12f;
        values[3] = 7.8e12f;
        Lut3D lut = new Lut3D(2, values);

        //Act
        string text = CubeLutFile.ToText(lut, null);

        //Assert - the keyword lines carry an E of their own, so only the DATA rows are examined
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#' || char.IsLetter(line[0])) continue;

            line.Should().NotContain("E");
            line.Should().NotContain("e");
        }

        text.Should().Contain("0.0000000012");
        text.Should().Contain("7800000000000.0");
    }

    [Fact]
    public void Every_number_the_writer_emits_reads_back_as_the_very_same_number()
    {
        //Arrange - awkward values: a third, a denormal-ish tiny, a big one, and the plain ones
        float[] awkward =
        {
            0f, 1f, 0.5f, 1f / 3f, 2f / 3f, 0.1f, 0.30000001f, -0.25f, 1.0000001f,
            1.2e-9f, 3.4e-30f, 7.8e12f, float.Epsilon, -float.Epsilon, 1.401e-45f,
        };

        //Act & Assert
        foreach (float value in awkward)
        {
            string text = CubeLutFile.Format(value);

            text.Should().NotContain("E");
            float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).Should().Be(value);
        }
    }

    [Fact]
    public void A_table_read_written_and_read_again_holds_exactly_the_numbers_it_started_with()
    {
        //Arrange - six decimals, seventeen decimals and a bare integer all in one file
        string original = string.Join(
            '\n',
            "TITLE \"round trip\"",
            "LUT_3D_SIZE 2",
            "0 0 0",
            "1 0 0",
            "0.333333 0.6666666666666666 0.123456789",
            "1.0 0.0000001 0.9999999",
            "0.000012345 0.5 0.25",
            "1 0 1",
            "0.1 0.2 0.3",
            "1 1 1");

        CubeLut first = CubeLutFile.Parse(original, "first");

        //Act
        CubeLut second = CubeLutFile.Parse(CubeLutFile.ToText(first.Lut3D, first.Title), "second");

        //Assert
        second.Title.Should().Be("round trip");
        second.Lut3D.Values.ToArray().Should().Equal(first.Lut3D.Values.ToArray());
    }

    [Fact]
    public void Curves_read_written_and_read_again_hold_exactly_what_they_started_with()
    {
        //Arrange
        string original = string.Join(
            '\n',
            "LUT_1D_SIZE 4",
            "DOMAIN_MIN 0 0 0",
            "DOMAIN_MAX 2 2 2",
            "0 0 0",
            "0.3333 0.3333 0.3333",
            "0.6667 0.6667 0.6667",
            "1 1 1");

        CubeLut first = CubeLutFile.Parse(original, "curves");

        //Act
        CubeLut second = CubeLutFile.Parse(CubeLutFile.ToText(first.Lut1D, "curves"), "again");

        //Assert
        second.Lut1D.Size.Should().Be(4);
        second.Lut1D.Red.ToArray().Should().Equal(first.Lut1D.Red.ToArray());
        second.Lut1D.DomainMaximum[0].Should().Be(2f);
    }

    [Fact]
    public void The_numbers_are_written_the_same_way_whatever_the_thread_culture_is()
    {
        //Arrange - a culture whose decimal separator is a comma would ruin the file
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        Lut3D lut = new Lut3D(2, Halves());

        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            //Act
            string text = CubeLutFile.ToText(lut, "kultur");

            //Assert
            text.Should().Contain("0.5 0.5 0.5");
            text.Should().NotContain(",");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_title_is_cleaned_of_the_characters_the_format_cannot_carry()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(2);

        //Act
        string quoted = CubeLutFile.ToText(lut, "a \"quoted\" name");
        string broken = CubeLutFile.ToText(lut, "two\nlines");
        string blank = CubeLutFile.ToText(lut, "   ");

        //Assert
        quoted.Should().Contain("TITLE \"a quoted name\"");
        broken.Should().Contain("TITLE \"two lines\"");
        blank.Should().NotContain("TITLE");

        CubeLutFile.Parse(quoted, null).Title.Should().Be("a quoted name");
    }

    [Fact]
    public void A_table_written_to_a_file_is_the_same_bytes_as_one_written_to_text()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(3);
        string directory = TestAssets.CreateTemporaryDirectory("cube-writer");
        string path = Path.Combine(directory, "identity.cube");

        try
        {
            //Act
            CubeLutFile.Write(lut, path, "identity three");
            string fromFile = File.ReadAllText(path);

            //Assert
            fromFile.Should().Be(CubeLutFile.ToText(lut, "identity three"));
            File.ReadAllBytes(path)[0].Should().Be((byte)'#');
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_table_written_to_a_stream_leaves_the_stream_open_and_flushed()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(2);
        using MemoryStream stream = new MemoryStream();

        //Act
        CubeLutFile.Write(lut, stream, "streamed");
        stream.Position = 0;
        CubeLut read = CubeLutFile.Read(stream, "streamed");

        //Assert
        stream.CanRead.Should().BeTrue();
        read.Lut3D.Size.Should().Be(2);
        read.Title.Should().Be("streamed");
    }

    [Fact]
    public void A_number_that_is_not_finite_is_refused_rather_than_written()
    {
        //Act
        Action notANumber = () => CubeLutFile.Format(float.NaN);
        Action endless = () => CubeLutFile.Format(float.PositiveInfinity);

        //Assert
        notANumber.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*finite*");
        endless.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*finite*");
    }

    private static float[] Halves()
    {
        float[] values = new float[2 * 2 * 2 * 3];
        for (int index = 0; index < values.Length; index++) values[index] = 0.5f;
        return values;
    }
}
