using System;
using CodeBrix.VideoPlayback.Skia.Effects;

namespace CodeBrix.VideoPlayback.Skia.Internal;

/// <summary>
/// Lays a composed three-dimensional lookup grid out as the two-dimensional strip the shader samples.
/// </summary>
/// <remarks>
/// <para>
/// The strip is <c>size * size</c> pixels wide and <c>size</c> pixels tall: one tile per blue index, laid
/// left to right, red running across a tile and green down it. The pixel holding the node
/// <c>(r, g, b)</c> is therefore at <c>x = (b * size) + r</c>, <c>y = g</c>.
/// </para>
/// <para>
/// Pixels are RGBA, one byte per channel, alpha 255, unpremultiplied - the values are DATA, not a colour to
/// be blended, so nothing must touch them between here and the sampler.
/// </para>
/// </remarks>
internal static class LutAtlas
{
    /// <summary>The number of bytes one atlas pixel occupies.</summary>
    internal const int BytesPerPixel = 4;

    /// <summary>The width in pixels of the atlas for a grid of the given size.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <returns><paramref name="size" /> squared.</returns>
    internal static int GetWidth(int size) => size * size;

    /// <summary>The height in pixels of the atlas for a grid of the given size.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <returns><paramref name="size" />.</returns>
    internal static int GetHeight(int size) => size;

    /// <summary>Writes a composed grid into an atlas.</summary>
    /// <param name="composer">The composed grid.</param>
    /// <param name="destination">Where the pixels go, RGBA one byte per channel.</param>
    /// <param name="stride">The distance in bytes from one atlas row to the next.</param>
    /// <exception cref="ArgumentNullException"><paramref name="composer" /> is null.</exception>
    /// <exception cref="ArgumentException">The destination is too small for the atlas.</exception>
    internal static void Write(EffectComposer composer, Span<byte> destination, int stride)
    {
        if (composer == null) throw new ArgumentNullException(nameof(composer));

        int size = composer.Size;
        int width = GetWidth(size);
        int height = GetHeight(size);
        long required = ((long)(height - 1) * stride) + ((long)width * BytesPerPixel);

        if (stride < width * BytesPerPixel || destination.Length < required)
        {
            throw new ArgumentException(
                $"A {size}-node lookup atlas is {width}x{height} pixels and needs {required} bytes at a stride "
                + $"of {stride}; the destination holds {destination.Length}.",
                nameof(destination));
        }

        float[] nodes = composer.Nodes;

        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                Span<byte> row = destination.Slice(green * stride, width * BytesPerPixel);
                for (int red = 0; red < size; red++)
                {
                    int node = ((((blue * size) + green) * size) + red) * 3;
                    int pixel = ((blue * size) + red) * BytesPerPixel;

                    row[pixel] = ToByte(nodes[node]);
                    row[pixel + 1] = ToByte(nodes[node + 1]);
                    row[pixel + 2] = ToByte(nodes[node + 2]);
                    row[pixel + 3] = 255;
                }
            }
        }
    }

    private static byte ToByte(float value)
    {
        int scaled = (int)((value * 255f) + 0.5f);
        if (scaled < 0) return 0;
        return scaled > 255 ? (byte)255 : (byte)scaled;
    }
}
