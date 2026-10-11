using System;
using CodeBrix.VideoPlayback.Authoring.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// The master flavour's transparent-pixel fill on small synthetic planes: who takes whose colour, what is never
/// touched, and what happens at the edges of an odd-sized frame.
/// </summary>
public class TransparentPixelFillTests
{
    [Fact]
    public void A_single_opaque_pixel_colours_every_transparent_pixel()
    {
        //Arrange
        Planes planes = new Planes(5, 4);
        planes.SetPixel(2, 1, 200, 90, 160, 255);

        //Act
        bool changed = planes.Fill();

        //Assert
        changed.Should().BeTrue();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 5; x++) planes.LumaAt(x, y).Should().Be((byte)200);
        }

        for (int cy = 0; cy < planes.ChromaHeight; cy++)
        {
            for (int cx = 0; cx < planes.ChromaWidth; cx++)
            {
                planes.UAt(cx, cy).Should().Be((byte)90);
                planes.VAt(cx, cy).Should().Be((byte)160);
            }
        }
    }

    [Fact]
    public void An_opaque_block_keeps_its_own_colour_and_lends_it_outward()
    {
        //Arrange
        Planes planes = new Planes(8, 8);
        for (int y = 2; y < 6; y++)
        {
            for (int x = 2; x < 6; x++) planes.SetPixel(x, y, (byte)(100 + x + (y * 8)), 70, 180, 255);
        }

        //Act
        planes.Fill();

        //Assert
        for (int y = 2; y < 6; y++)
        {
            for (int x = 2; x < 6; x++) planes.LumaAt(x, y).Should().Be((byte)(100 + x + (y * 8)));
        }

        // A corner has exactly one nearest block pixel; a pixel beside an edge of the block ties between
        // several of that edge's pixels, and takes one of them.
        planes.LumaAt(0, 0).Should().Be(planes.LumaAt(2, 2));
        planes.LumaAt(7, 7).Should().Be(planes.LumaAt(5, 5));
        new[] { planes.LumaAt(2, 2), planes.LumaAt(2, 3), planes.LumaAt(2, 4), planes.LumaAt(2, 5) }
            .Should().Contain(planes.LumaAt(0, 4));
        new[] { planes.LumaAt(2, 5), planes.LumaAt(3, 5), planes.LumaAt(4, 5), planes.LumaAt(5, 5) }
            .Should().Contain(planes.LumaAt(4, 7));
    }

    [Fact]
    public void Each_transparent_pixel_takes_the_nearest_source_along_the_horizontal_axis()
    {
        //Arrange
        Planes planes = new Planes(9, 1);
        planes.SetPixel(0, 0, 20, 128, 128, 255);
        planes.SetPixel(8, 0, 220, 128, 128, 255);

        //Act
        planes.Fill();

        //Assert
        planes.LumaAt(1, 0).Should().Be((byte)20);
        planes.LumaAt(3, 0).Should().Be((byte)20);
        planes.LumaAt(5, 0).Should().Be((byte)220);
        planes.LumaAt(7, 0).Should().Be((byte)220);
    }

    [Fact]
    public void Each_transparent_pixel_takes_the_nearest_source_along_the_vertical_axis()
    {
        //Arrange
        Planes planes = new Planes(1, 9);
        planes.SetPixel(0, 0, 30, 128, 128, 255);
        planes.SetPixel(0, 8, 230, 128, 128, 255);

        //Act
        planes.Fill();

        //Assert
        planes.LumaAt(0, 2).Should().Be((byte)30);
        planes.LumaAt(0, 3).Should().Be((byte)30);
        planes.LumaAt(0, 6).Should().Be((byte)230);
        planes.LumaAt(0, 7).Should().Be((byte)230);
    }

    [Fact]
    public void A_partly_transparent_pixel_keeps_its_colour_and_the_alpha_plane_is_never_changed()
    {
        //Arrange
        Planes planes = new Planes(4, 2);
        planes.SetPixel(0, 0, 200, 60, 190, 255);
        planes.SetPixel(3, 0, 40, 100, 140, 1);
        byte[] alphaBefore = (byte[])planes.A.Clone();

        //Act
        planes.Fill();

        //Assert
        planes.LumaAt(3, 0).Should().Be((byte)40);
        planes.UAt(1, 0).Should().Be((byte)100);
        planes.VAt(1, 0).Should().Be((byte)140);
        planes.A.Should().Equal(alphaBefore);
    }

    [Fact]
    public void A_fully_transparent_frame_is_left_unchanged()
    {
        //Arrange
        Planes planes = new Planes(6, 4);
        for (int i = 0; i < planes.Y.Length; i++) planes.Y[i] = (byte)i;
        byte[] lumaBefore = (byte[])planes.Y.Clone();
        byte[] uBefore = (byte[])planes.U.Clone();

        //Act
        bool changed = planes.Fill();

        //Assert
        changed.Should().BeFalse();
        planes.Y.Should().Equal(lumaBefore);
        planes.U.Should().Equal(uBefore);
    }

    [Fact]
    public void A_fully_opaque_frame_is_left_unchanged()
    {
        //Arrange
        Planes planes = new Planes(4, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++) planes.SetPixel(x, y, (byte)(x * 10 + y), 1, 2, 255);
        }

        byte[] lumaBefore = (byte[])planes.Y.Clone();

        //Act
        bool changed = planes.Fill();

        //Assert
        changed.Should().BeFalse();
        planes.Y.Should().Equal(lumaBefore);
    }

    [Fact]
    public void A_chroma_sample_that_covers_any_visible_pixel_is_not_rewritten()
    {
        //Arrange
        Planes planes = new Planes(4, 4);
        planes.SetPixel(1, 1, 200, 50, 210, 255);
        planes.U[planes.ChromaIndex(1, 1)] = 7;
        planes.V[planes.ChromaIndex(1, 1)] = 8;

        //Act
        planes.Fill();

        //Assert
        planes.UAt(0, 0).Should().Be((byte)50);
        planes.VAt(0, 0).Should().Be((byte)210);
        planes.UAt(1, 1).Should().Be((byte)50);
        planes.VAt(1, 1).Should().Be((byte)210);
    }

    [Fact]
    public void A_chroma_block_with_one_visible_luma_pixel_keeps_its_own_chroma()
    {
        //Arrange
        Planes planes = new Planes(4, 2);
        planes.SetPixel(0, 0, 200, 50, 210, 255);
        planes.SetPixel(3, 1, 10, 0, 0, 0);
        planes.U[planes.ChromaIndex(1, 0)] = 33;
        planes.A[(1 * planes.Width) + 2] = 255;

        //Act
        planes.Fill();

        //Assert
        planes.UAt(1, 0).Should().Be((byte)33);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(7, 7)]
    [InlineData(6, 5)]
    [InlineData(1, 1)]
    public void Odd_and_even_sizes_fill_every_transparent_pixel_from_a_visible_one(int width, int height)
    {
        //Arrange
        Planes planes = new Planes(width, height);
        planes.SetPixel(width - 1, height - 1, 180, 77, 99, 255);

        //Act
        planes.Fill();

        //Assert
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) planes.LumaAt(x, y).Should().Be((byte)180);
        }

        for (int cy = 0; cy < planes.ChromaHeight; cy++)
        {
            for (int cx = 0; cx < planes.ChromaWidth; cx++)
            {
                planes.UAt(cx, cy).Should().Be((byte)77);
                planes.VAt(cx, cy).Should().Be((byte)99);
            }
        }
    }

    [Fact]
    public void Strides_wider_than_the_picture_are_honoured()
    {
        //Arrange
        Planes planes = new Planes(3, 2, padding: 5);
        planes.SetPixel(0, 0, 150, 20, 30, 255);
        planes.Y[3] = 99;

        //Act
        planes.Fill();

        //Assert
        planes.LumaAt(2, 1).Should().Be((byte)150);
        planes.Y[3].Should().Be((byte)99);
    }

    [Fact]
    public void The_same_planes_always_fill_the_same_way()
    {
        //Arrange
        Planes first = new Planes(16, 12);
        Planes second = new Planes(16, 12);
        Random random = new Random(1234);
        for (int i = 0; i < 20; i++)
        {
            int x = random.Next(16);
            int y = random.Next(12);
            byte luma = (byte)random.Next(256);
            first.SetPixel(x, y, luma, (byte)(luma / 2), (byte)(255 - luma), 255);
            second.SetPixel(x, y, luma, (byte)(luma / 2), (byte)(255 - luma), 255);
        }

        //Act
        first.Fill();
        second.Fill();

        //Assert
        first.Y.Should().Equal(second.Y);
        first.U.Should().Equal(second.U);
        first.V.Should().Equal(second.V);
    }

    /// <summary>One 8-bit 4:2:0 picture with an alpha plane, every pixel fully transparent to begin with.</summary>
    private sealed class Planes
    {
        private readonly TransparentPixelFill fill = new TransparentPixelFill();

        public Planes(int width, int height, int padding = 0)
        {
            Width = width;
            Height = height;
            LumaStride = width + padding;
            ChromaWidth = (width + 1) / 2;
            ChromaHeight = (height + 1) / 2;
            ChromaStride = ChromaWidth + padding;
            Y = new byte[LumaStride * height];
            U = new byte[ChromaStride * ChromaHeight];
            V = new byte[ChromaStride * ChromaHeight];
            A = new byte[LumaStride * height];
            Array.Fill(U, (byte)128);
            Array.Fill(V, (byte)128);
        }

        public int Width { get; }

        public int Height { get; }

        public int LumaStride { get; }

        public int ChromaWidth { get; }

        public int ChromaHeight { get; }

        public int ChromaStride { get; }

        public byte[] Y { get; }

        public byte[] U { get; }

        public byte[] V { get; }

        public byte[] A { get; }

        public void SetPixel(int x, int y, byte luma, byte cb, byte cr, byte alpha)
        {
            Y[(y * LumaStride) + x] = luma;
            A[(y * LumaStride) + x] = alpha;
            U[ChromaIndex(x / 2, y / 2)] = cb;
            V[ChromaIndex(x / 2, y / 2)] = cr;
        }

        public int ChromaIndex(int cx, int cy) => (cy * ChromaStride) + cx;

        public byte LumaAt(int x, int y) => Y[(y * LumaStride) + x];

        public byte UAt(int cx, int cy) => U[ChromaIndex(cx, cy)];

        public byte VAt(int cx, int cy) => V[ChromaIndex(cx, cy)];

        public bool Fill() => fill.Apply(Width, Height, Y, LumaStride, U, V, ChromaStride, A, LumaStride);
    }
}
