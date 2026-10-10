using System;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// A frame's alpha plane - attaching one, its lifetime, and the processor converter's premultiplied output.
/// </summary>
public class VideoFrameAlphaTests
{
    private const int Width = 64;
    private const int Height = 4;

    private static readonly VideoColorInfo Limited = new VideoColorInfo(
        VideoColorPrimaries.Bt709,
        VideoTransferCharacteristics.Bt709,
        VideoMatrixCoefficients.Bt709,
        VideoColorRange.Limited,
        VideoChromaSiting.Vertical);

    private static readonly VideoColorInfo FullGray = new VideoColorInfo(
        VideoColorPrimaries.Unspecified,
        VideoTransferCharacteristics.Unspecified,
        VideoMatrixCoefficients.Unspecified,
        VideoColorRange.Full,
        VideoChromaSiting.Unknown);

    [Fact]
    public void A_frame_without_alpha_has_an_empty_alpha_plane()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();

        //Act
        using VideoFrame frame = Picture(pool, 8);

        //Assert
        frame.HasAlpha.Should().BeFalse();
        frame.A.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void An_attached_alpha_plane_is_the_alpha_frames_luma()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        VideoFrame alpha = Alpha(pool, 8, x => (ushort)(x * 4));
        IntPtr alphaData = alpha.Y.Data;

        //Act
        frame.AttachAlpha(alpha);

        //Assert
        frame.HasAlpha.Should().BeTrue();
        frame.IsAlphaPremultiplied.Should().BeFalse();
        frame.A.Data.Should().Be(alphaData);
        frame.A.GetRowBytes(0)[10].Should().Be((byte)40);
        frame.ToString().Should().EndWith("with alpha");
    }

    [Fact]
    public void Releasing_the_picture_releases_its_alpha_frame()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoFrame frame = Picture(pool, 8);
        VideoFrame alpha = Alpha(pool, 8, x => 255);
        VideoFrame watch = alpha.Retain();
        frame.AttachAlpha(alpha);

        //Act
        frame.Dispose();

        //Assert
        watch.ReferenceCount.Should().Be(1);
        watch.Dispose();
        watch.ReferenceCount.Should().Be(0);
    }

    [Fact]
    public void An_alpha_frame_of_another_size_is_refused()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        using VideoFrame alpha = Gray(pool, 32, Height, 8, x => 0);

        //Act
        Action attach = () => frame.AttachAlpha(alpha);

        //Assert
        attach.Should().Throw<ArgumentException>().WithMessage("*32x4 at 8 bits*64x4 at 8 bits*");
    }

    [Fact]
    public void An_alpha_frame_of_another_bit_depth_is_refused()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        using VideoFrame alpha = Alpha(pool, 10, x => 0);

        //Act
        Action attach = () => frame.AttachAlpha(alpha);

        //Assert
        attach.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_second_alpha_plane_is_refused()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        frame.AttachAlpha(Alpha(pool, 8, x => 0));
        using VideoFrame second = Alpha(pool, 8, x => 0);

        //Act
        Action attach = () => frame.AttachAlpha(second);

        //Assert
        attach.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_frame_cannot_be_its_own_alpha_plane()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);

        //Act
        Action attach = () => frame.AttachAlpha(frame);

        //Assert
        attach.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_converter_premultiplies_an_eight_bit_alpha_gradient()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        byte[] opaque = Convert(frame);
        frame.AttachAlpha(Alpha(pool, 8, x => (ushort)(x * 255 / (Width - 1))));

        //Act
        byte[] result = Convert(frame);

        //Assert
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width * 4) + (x * 4);
                int a = x * 255 / (Width - 1);
                result[i + 3].Should().Be((byte)a);
                result[i + 0].Should().Be(Premultiply(opaque[i + 0], a));
                result[i + 1].Should().Be(Premultiply(opaque[i + 1], a));
                result[i + 2].Should().Be(Premultiply(opaque[i + 2], a));
            }
        }
    }

    [Fact]
    public void A_fully_transparent_pixel_is_all_zeros_and_a_fully_opaque_one_is_unchanged()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        byte[] opaque = Convert(frame);
        frame.AttachAlpha(Alpha(pool, 8, x => x < Width / 2 ? (ushort)0 : (ushort)255));

        //Act
        byte[] result = Convert(frame);

        //Assert
        result.AsSpan(0, 4).ToArray().Should().Equal(new byte[] { 0, 0, 0, 0 });
        int last = (Width - 1) * 4;
        result.AsSpan(last, 4).ToArray().Should().Equal(opaque.AsSpan(last, 4).ToArray());
    }

    [Fact]
    public void A_ten_bit_alpha_plane_is_rounded_to_eight_bits()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 10);
        frame.AttachAlpha(Alpha(pool, 10, x => (ushort)(x * 16)));

        //Act
        byte[] result = Convert(frame);

        //Assert
        for (int x = 0; x < Width; x++)
        {
            int sample = x * 16;
            int expected = ((sample * 255) + 511) / 1023;
            result[(x * 4) + 3].Should().Be((byte)expected);
        }
    }

    [Fact]
    public void Premultiplied_colour_only_gets_its_opacity_written()
    {
        //Arrange
        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame = Picture(pool, 8);
        byte[] opaque = Convert(frame);
        frame.AttachAlpha(Alpha(pool, 8, x => 100), premultiplied: true);

        //Act
        byte[] result = Convert(frame);

        //Assert
        frame.IsAlphaPremultiplied.Should().BeTrue();
        result[0].Should().Be(opaque[0]);
        result[1].Should().Be(opaque[1]);
        result[2].Should().Be(opaque[2]);
        result[3].Should().Be((byte)100);
    }

    [Fact]
    public void PremultiplyByAlpha_refuses_an_empty_plane()
    {
        //Arrange
        byte[] pixels = new byte[Width * 4];

        //Act
        Action premultiply = () => VideoFrameConverter.PremultiplyByAlpha(
            VideoFramePlane.Empty, 8, Width, 1, pixels, Width * 4);

        //Assert
        premultiply.Should().Throw<ArgumentException>();
    }

    private static byte Premultiply(byte colour, int alpha) => (byte)(((colour * alpha) + 127) / 255);

    private static byte[] Convert(VideoFrame frame)
    {
        byte[] pixels = new byte[VideoFrameConverter.GetBgraBufferSize(Width, Height)];
        VideoFrameConverter.ToBgra32(frame, pixels, Width * 4);
        return pixels;
    }

    private static unsafe VideoFrame Picture(PinnedFrameBufferPool pool, int bitDepth)
    {
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(Width, Height, VideoPixelLayout.I420, bitDepth));
        int scale = 1 << (bitDepth - 8);
        Fill(buffer.Y, bitDepth, (x, y) => (ushort)((40 + (x * 3)) * scale));
        Fill(buffer.U, bitDepth, (x, y) => (ushort)((100 + x) * scale));
        Fill(buffer.V, bitDepth, (x, y) => (ushort)((160 - x) * scale));

        return VideoFrame.Create(
            buffer,
            new VideoFrameInfo(Width, Height, Width, Height, VideoPixelLayout.I420, bitDepth, TimeSpan.Zero, 0, 0, true, Limited, null),
            pool);
    }

    private static VideoFrame Alpha(PinnedFrameBufferPool pool, int bitDepth, Func<int, ushort> sample) =>
        Gray(pool, Width, Height, bitDepth, sample);

    private static VideoFrame Gray(PinnedFrameBufferPool pool, int width, int height, int bitDepth, Func<int, ushort> sample)
    {
        VideoFrameBuffer buffer = pool.Rent(new VideoFrameBufferDescriptor(width, height, VideoPixelLayout.Gray, bitDepth));
        Fill(buffer.Y, bitDepth, (x, y) => sample(x));

        return VideoFrame.Create(
            buffer,
            new VideoFrameInfo(width, height, width, height, VideoPixelLayout.Gray, bitDepth, TimeSpan.Zero, 0, 0, true, FullGray, null),
            pool);
    }

    private static unsafe void Fill(VideoFramePlane plane, int bitDepth, Func<int, int, ushort> value)
    {
        for (int y = 0; y < plane.Height; y++)
        {
            byte* row = (byte*)plane.Data + ((long)y * plane.Stride);
            for (int x = 0; x < plane.Width; x++)
            {
                if (bitDepth == 8) row[x] = (byte)value(x, y);
                else ((ushort*)row)[x] = value(x, y);
            }
        }
    }
}
