using System;
using CodeBrix.VideoPlayback.Effects;
using CodeBrix.VideoPlayback.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the two-dimensional strip the shader samples the resultant lookup table from.
/// </summary>
public class LutAtlasTests
{
    [Fact]
    public void The_atlas_is_size_squared_wide_and_size_tall()
    {
        //Arrange & Act
        int width = LutAtlas.GetWidth(33);
        int height = LutAtlas.GetHeight(33);

        //Assert
        width.Should().Be(1089);
        height.Should().Be(33);
    }

    [Fact]
    public void Blue_picks_the_tile_red_runs_across_it_and_green_runs_down_it()
    {
        //Arrange
        const int Size = 5;
        EffectComposer composer = new EffectComposer(Size);
        int stride = LutAtlas.GetWidth(Size) * LutAtlas.BytesPerPixel;
        byte[] atlas = new byte[stride * LutAtlas.GetHeight(Size)];

        //Act
        LutAtlas.Write(composer, atlas, stride);

        //Assert - the identity grid puts each index straight into its own channel
        for (int blue = 0; blue < Size; blue++)
        {
            for (int green = 0; green < Size; green++)
            {
                for (int red = 0; red < Size; red++)
                {
                    int offset = (green * stride) + ((((blue * Size) + red) * LutAtlas.BytesPerPixel));
                    atlas[offset].Should().Be(Level(red, Size));
                    atlas[offset + 1].Should().Be(Level(green, Size));
                    atlas[offset + 2].Should().Be(Level(blue, Size));
                    atlas[offset + 3].Should().Be((byte)255);
                }
            }
        }
    }

    [Fact]
    public void A_destination_too_small_for_the_atlas_is_refused_by_name()
    {
        //Arrange
        EffectComposer composer = new EffectComposer(5);
        byte[] tooSmall = new byte[16];

        //Act
        Action write = () => LutAtlas.Write(composer, tooSmall, 4);

        //Assert
        write.Should().Throw<ArgumentException>().WithMessage("*25x5 pixels*");
    }

    private static byte Level(int index, int size) => (byte)(((index / (float)(size - 1)) * 255f) + 0.5f);
}
