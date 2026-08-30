using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks that building a chain of lookup tables and baking it to a ".cube" file needs NOTHING but this
/// package: no Skia, no presenter, no decoder, no video of any kind.
/// </summary>
/// <remarks>
/// <para>
/// This is a contract test for an application shape rather than for one class: "let a person tick some
/// ".cube" files, give each a percentage, and save the resulting table". A colour-grading utility with no
/// player in it is a reasonable thing to write against this library, and it should not have to reference
/// CodeBrix.VideoPlayback.Skia to do it, nor pull in an AV1 decoder it will never call.
/// </para>
/// <para>
/// The test project this lives in references CodeBrix.VideoPlayback and nothing else that could compose a
/// table, so the reference list is half the assertion: if baking ever needed the Skia package, this file
/// would stop compiling.
/// </para>
/// </remarks>
public class LutBakeWithoutVideoTests
{
    [Fact]
    public void A_chain_is_read_composed_and_written_with_the_core_package_alone()
    {
        //Arrange - three files on disk, exactly as a file picker would hand them over
        var folder = CreateFolder();

        try
        {
            var warm = WriteCube(folder, "warm.cube", Scale(17, 1.2f, 1f, 0.8f));
            var cool = WriteCube(folder, "cool.cube", Scale(17, 0.8f, 1f, 1.2f));
            var baked = Path.Combine(folder, "chain.cube");

            //Act - the whole application: read each file, give it a percentage, compose, write
            List<LutLayer> chain =
            [
                LutLayer.FromCubeFile(warm, 40d),
                LutLayer.FromCubeFile(cool, 65d),
            ];

            Lut3D resultant = LutComposer.Compose(chain);
            CubeLutFile.Write(resultant, baked, "warm@40 + cool@65");

            //Assert - a real ".cube" file that reads back as the table that was composed
            File.Exists(baked).Should().BeTrue();

            CubeLut readBack = CubeLutFile.ReadFile(baked);
            readBack.Title.Should().Be("warm@40 + cool@65");
            readBack.IsThreeDimensional.Should().BeTrue();
            readBack.Lut3D.Size.Should().Be(resultant.Size);
            readBack.Lut3D.Values.ToArray().Should().Equal(resultant.Values.ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_order_of_the_chain_reaches_the_baked_file()
    {
        //Arrange
        var folder = CreateFolder();

        try
        {
            var halve = WriteCube(folder, "halve.cube", Scale(17, 0.5f, 0.5f, 0.5f));
            var redden = WriteCube(folder, "redden.cube", Scale(17, 2f, 1f, 1f));

            //Act - the same two tables, both ways round
            Lut3D halveThenRedden = LutComposer.Compose(
            [
                LutLayer.FromCubeFile(halve),
                LutLayer.FromCubeFile(redden),
            ]);

            Lut3D reddenThenHalve = LutComposer.Compose(
            [
                LutLayer.FromCubeFile(redden),
                LutLayer.FromCubeFile(halve),
            ]);

            //Assert - order matters, and it is the chain's order that decides
            halveThenRedden.Values.ToArray().Should().NotEqual(reddenThenHalve.Values.ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_table_at_nothing_per_cent_leaves_the_chain_where_it_was()
    {
        //Arrange
        var folder = CreateFolder();

        try
        {
            var warm = WriteCube(folder, "warm.cube", Scale(17, 1.2f, 1f, 0.8f));

            //Act
            Lut3D withIt = LutComposer.Compose([LutLayer.FromCubeFile(warm, 100d)]);
            Lut3D withoutIt = LutComposer.Compose([LutLayer.FromCubeFile(warm, 0d)]);

            //Assert
            withoutIt.Values.ToArray().Should().Equal(Lut3D.CreateIdentity(withoutIt.Size).Values.ToArray());
            withIt.Values.ToArray().Should().NotEqual(withoutIt.Values.ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cbx-lut-bake-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string WriteCube(string folder, string name, Lut3D table)
    {
        var path = Path.Combine(folder, name);
        CubeLutFile.Write(table, path, Path.GetFileNameWithoutExtension(name));
        return path;
    }

    private static Lut3D Scale(int size, float red, float green, float blue)
    {
        Lut3D identity = Lut3D.CreateIdentity(size);
        var values = identity.Values.ToArray();

        for (var index = 0; index < values.Length; index += 3)
        {
            values[index] = System.Math.Clamp(values[index] * red, 0f, 1f);
            values[index + 1] = System.Math.Clamp(values[index + 1] * green, 0f, 1f);
            values[index + 2] = System.Math.Clamp(values[index + 2] * blue, 0f, 1f);
        }

        return new Lut3D(size, values);
    }
}
