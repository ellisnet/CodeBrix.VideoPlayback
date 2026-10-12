using System;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// The pixel arithmetic of an export: a decoded picture to RGBA, straight-alpha "over" a backdrop, and the
/// flattening of a backdrop's own transparency over grey.
/// </summary>
internal static class ExportPixels
{
    /// <summary>The 50% grey every backdrop falls back to: 0x80 in each channel.</summary>
    internal const byte Grey = 0x80;

    /// <summary>
    /// Converts a decoded picture to opaque BGRA with the core's converter - the matrix and range the frame reports,
    /// deep samples dithered to 8 bits - over a <paramref name="width" /> by <paramref name="height" /> window
    /// at its top left.
    /// </summary>
    /// <param name="frame">The picture.</param>
    /// <param name="width">The output width (the frame's, or one less when it was odd).</param>
    /// <param name="height">The output height.</param>
    /// <param name="bgra">Where the pixels go, <c>width * height * 4</c> bytes.</param>
    internal static void ToBgra(VideoFrame frame, int width, int height, byte[] bgra)
    {
        VideoFrameConverter.ToBgra32(
            frame.Y,
            frame.U,
            frame.V,
            width,
            height,
            frame.Layout,
            frame.BitDepth,
            frame.Color,
            bgra,
            width * 4);
    }

    /// <summary>Copies opaque BGRA to RGBA, swapping red and blue and making every pixel opaque.</summary>
    /// <param name="bgra">The source pixels.</param>
    /// <param name="rgba">The destination, the same size.</param>
    internal static void BgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = 255;
        }
    }

    /// <summary>
    /// Composites a picture over a backdrop - straight alpha "over" - writing opaque RGBA. A pixel whose colour was
    /// premultiplied before encoding is added to the backdrop's uncovered share instead.
    /// </summary>
    /// <param name="bgra">The picture, opaque BGRA as <see cref="ToBgra" /> wrote it.</param>
    /// <param name="alpha">The alpha plane, or the empty plane for a fully opaque picture.</param>
    /// <param name="bitDepth">The alpha plane's bit depth: 8, 10 or 12.</param>
    /// <param name="premultiplied">True when the picture's colours were premultiplied by the alpha.</param>
    /// <param name="backdrop">The backdrop, opaque RGBA at the output size.</param>
    /// <param name="rgba">The result, RGBA at the output size.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    internal static void CompositeOver(
        ReadOnlySpan<byte> bgra,
        in VideoFramePlane alpha,
        int bitDepth,
        bool premultiplied,
        ReadOnlySpan<byte> backdrop,
        Span<byte> rgba,
        int width,
        int height)
    {
        if (alpha.IsEmpty)
        {
            BgraToRgba(bgra.Slice(0, width * height * 4), rgba);
            return;
        }

        int maximum = (1 << bitDepth) - 1;
        int half = maximum / 2;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> alphaRow = alpha.GetRowBytes(y);
            int rowStart = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int a;
                if (bitDepth == 8)
                {
                    a = alphaRow[x];
                }
                else
                {
                    int sample = alphaRow[2 * x] | (alphaRow[(2 * x) + 1] << 8);
                    if (sample > maximum) sample = maximum;
                    a = ((sample * 255) + half) / maximum;
                }

                int i = rowStart + (x * 4);
                int uncovered = 255 - a;
                if (premultiplied)
                {
                    rgba[i] = Clamp(bgra[i + 2] + (((backdrop[i] * uncovered) + 127) / 255));
                    rgba[i + 1] = Clamp(bgra[i + 1] + (((backdrop[i + 1] * uncovered) + 127) / 255));
                    rgba[i + 2] = Clamp(bgra[i] + (((backdrop[i + 2] * uncovered) + 127) / 255));
                }
                else
                {
                    rgba[i] = (byte)(((bgra[i + 2] * a) + (backdrop[i] * uncovered) + 127) / 255);
                    rgba[i + 1] = (byte)(((bgra[i + 1] * a) + (backdrop[i + 1] * uncovered) + 127) / 255);
                    rgba[i + 2] = (byte)(((bgra[i] * a) + (backdrop[i + 2] * uncovered) + 127) / 255);
                }

                rgba[i + 3] = 255;
            }
        }
    }

    /// <summary>Fills RGBA pixels with one opaque colour.</summary>
    /// <param name="rgba">The pixels.</param>
    /// <param name="rgb">The colour as <c>0xRRGGBB</c>.</param>
    internal static void Fill(Span<byte> rgba, int rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8) & 0xFF);
        byte b = (byte)(rgb & 0xFF);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
    }

    /// <summary>Flattens any transparency in RGBA pixels over 50% grey, so the backdrop is opaque.</summary>
    /// <param name="rgba">The pixels, changed in place.</param>
    internal static void FlattenOverGrey(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;

            int uncovered = (255 - a) * Grey;
            rgba[i] = (byte)(((rgba[i] * a) + uncovered + 127) / 255);
            rgba[i + 1] = (byte)(((rgba[i + 1] * a) + uncovered + 127) / 255);
            rgba[i + 2] = (byte)(((rgba[i + 2] * a) + uncovered + 127) / 255);
            rgba[i + 3] = 255;
        }
    }

    private static byte Clamp(int value) => value > 255 ? (byte)255 : (byte)value;
}
