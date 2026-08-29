using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// A deliberately slow, deliberately obvious, double-precision implementation of the planar-to-BGRA
/// conversion. It is the oracle the production converter is measured against, so it is written from the
/// specification rather than from the production code.
/// </summary>
/// <remarks>
/// It reproduces the parts of the conversion that are DEFINED behaviour rather than implementation detail -
/// the quarter-weight integer chroma blending, and the ordered-dither offset that replaces round-to-nearest
/// for deep sources - because those choices decide what the right answer IS. Everything downstream of them
/// (the matrix, the range scaling, the clamp) is computed in plain double arithmetic here, which is what
/// makes the comparison worth making: the production code does the same sums in 14-bit fixed point.
/// </remarks>
internal static class ScalarYuvReference
{
    private static readonly int[] Bayer =
    {
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5,
    };

    /// <summary>Converts tightly packed planar sample arrays to a tightly packed BGRA32 byte array.</summary>
    /// <param name="luma">The luma samples, row-major, <paramref name="width" /> per row.</param>
    /// <param name="chromaU">The first chroma plane's samples, or null for monochrome.</param>
    /// <param name="chromaV">The second chroma plane's samples, or null for monochrome.</param>
    /// <param name="width">The visible width.</param>
    /// <param name="height">The visible height.</param>
    /// <param name="layout">The plane layout.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="color">The colour description, before resolution.</param>
    /// <returns>A BGRA32 buffer of <c>width * height * 4</c> bytes.</returns>
    internal static byte[] ConvertToBgra(
        int[] luma,
        int[] chromaU,
        int[] chromaV,
        int width,
        int height,
        VideoPixelLayout layout,
        int bitDepth,
        VideoColorInfo color)
    {
        VideoColorInfo resolved = color.Resolve(height);
        bool monochrome = layout == VideoPixelLayout.Gray || chromaU == null || chromaV == null;

        int shiftX = layout == VideoPixelLayout.I420 || layout == VideoPixelLayout.I422 ? 1 : 0;
        int shiftY = layout == VideoPixelLayout.I420 ? 1 : 0;
        int chromaWidth = monochrome ? 0 : (width + (1 << shiftX) - 1) >> shiftX;
        int chromaHeight = monochrome ? 0 : (height + (1 << shiftY) - 1) >> shiftY;

        bool horizontalInterstitial = shiftX == 1 && resolved.ChromaSiting == VideoChromaSiting.Interstitial;
        bool verticalInterstitial = shiftY == 1
            && (resolved.ChromaSiting == VideoChromaSiting.Interstitial
                || resolved.ChromaSiting == VideoChromaSiting.Vertical);

        bool limited = resolved.Range != VideoColorRange.Full;
        int sampleScale = 1 << (bitDepth - 8);
        int maximum = (1 << bitDepth) - 1;

        int lumaOffset = limited ? 16 * sampleScale : 0;
        double lumaScale = limited ? 255.0 / (219.0 * sampleScale) : 255.0 / maximum;
        int chromaOffset = limited ? 128 * sampleScale : 1 << (bitDepth - 1);
        double chromaScale = limited ? 255.0 / (224.0 * sampleScale) : 255.0 / maximum;

        bool identity = !monochrome && resolved.Matrix == VideoMatrixCoefficients.Identity;

        double kr;
        double kb;
        switch (resolved.Matrix)
        {
            case VideoMatrixCoefficients.Fcc:
                kr = 0.30;
                kb = 0.11;
                break;
            case VideoMatrixCoefficients.Bt470Bg:
            case VideoMatrixCoefficients.Smpte170M:
                kr = 0.299;
                kb = 0.114;
                break;
            case VideoMatrixCoefficients.Smpte240M:
                kr = 0.212;
                kb = 0.087;
                break;
            case VideoMatrixCoefficients.Bt2020NonConstantLuminance:
            case VideoMatrixCoefficients.Bt2020ConstantLuminance:
                kr = 0.2627;
                kb = 0.0593;
                break;
            default:
                kr = 0.2126;
                kb = 0.0722;
                break;
        }

        double kg = 1.0 - kr - kb;
        double crToRed = 2.0 * (1.0 - kr) * chromaScale;
        double cbToBlue = 2.0 * (1.0 - kb) * chromaScale;
        double cbToGreen = 2.0 * kb * (1.0 - kb) / kg * chromaScale;
        double crToGreen = 2.0 * kr * (1.0 - kr) / kg * chromaScale;

        byte[] output = new byte[width * height * 4];
        int[] upsampledU = new int[width];
        int[] upsampledV = new int[width];

        for (int row = 0; row < height; row++)
        {
            if (monochrome)
            {
                Array.Clear(upsampledU);
                Array.Clear(upsampledV);
            }
            else
            {
                Upsample(chromaU, chromaWidth, chromaHeight, row, width, shiftX, shiftY,
                    horizontalInterstitial, verticalInterstitial, chromaOffset, upsampledU);
                Upsample(chromaV, chromaWidth, chromaHeight, row, width, shiftX, shiftY,
                    horizontalInterstitial, verticalInterstitial, chromaOffset, upsampledV);
            }

            for (int column = 0; column < width; column++)
            {
                int sample = luma[(row * width) + column];
                double red;
                double green;
                double blue;
                double offset;

                if (identity)
                {
                    // The planes are G, B and R, each on the luma range; no dithering is applied on this path.
                    offset = 0.5;
                    green = (sample - lumaOffset) * lumaScale;
                    blue = upsampledU[column] * lumaScale;
                    red = upsampledV[column] * lumaScale;
                }
                else
                {
                    offset = bitDepth > 8
                        ? Bayer[((row & 3) * 4) + (column & 3)] / 16.0
                        : 0.5;

                    double lumaTerm = (sample - lumaOffset) * lumaScale;
                    red = lumaTerm + (upsampledV[column] * crToRed);
                    green = lumaTerm - (upsampledU[column] * cbToGreen) - (upsampledV[column] * crToGreen);
                    blue = lumaTerm + (upsampledU[column] * cbToBlue);
                }

                int destination = ((row * width) + column) * 4;
                output[destination] = Quantise(blue, offset);
                output[destination + 1] = Quantise(green, offset);
                output[destination + 2] = Quantise(red, offset);
                output[destination + 3] = 255;
            }
        }

        return output;
    }

    private static byte Quantise(double value, double ditherOffset)
    {
        double shifted = Math.Floor(value + ditherOffset);
        if (shifted < 0.0) return 0;
        if (shifted > 255.0) return 255;
        return (byte)shifted;
    }

    private static void Upsample(
        int[] plane,
        int chromaWidth,
        int chromaHeight,
        int outputRow,
        int width,
        int shiftX,
        int shiftY,
        bool horizontalInterstitial,
        bool verticalInterstitial,
        int chromaOffset,
        int[] destination)
    {
        int[] blended = new int[chromaWidth];

        int firstRow;
        int secondRow;
        int firstWeight;
        int secondWeight;

        if (shiftY == 0)
        {
            firstRow = Clamp(outputRow, chromaHeight);
            secondRow = firstRow;
            firstWeight = 4;
            secondWeight = 0;
        }
        else if (!verticalInterstitial)
        {
            firstRow = Clamp(outputRow / 2, chromaHeight);
            secondRow = firstRow;
            firstWeight = 4;
            secondWeight = 0;
        }
        else if (outputRow % 2 == 0)
        {
            firstRow = Clamp(outputRow / 2, chromaHeight);
            secondRow = Clamp((outputRow / 2) - 1, chromaHeight);
            firstWeight = 3;
            secondWeight = 1;
        }
        else
        {
            firstRow = Clamp(outputRow / 2, chromaHeight);
            secondRow = Clamp((outputRow / 2) + 1, chromaHeight);
            firstWeight = 3;
            secondWeight = 1;
        }

        for (int index = 0; index < chromaWidth; index++)
        {
            int a = plane[(firstRow * chromaWidth) + index];
            int b = plane[(secondRow * chromaWidth) + index];
            blended[index] = secondWeight == 0
                ? a
                : ((a * firstWeight) + (b * secondWeight) + 2) / 4;
        }

        for (int column = 0; column < width; column++)
        {
            int value;
            if (shiftX == 0)
            {
                value = blended[Clamp(column, chromaWidth)];
            }
            else if (!horizontalInterstitial)
            {
                value = blended[Clamp(column / 2, chromaWidth)];
            }
            else
            {
                int centre = column / 2;
                int neighbour = column % 2 == 0 ? centre - 1 : centre + 1;
                int a = blended[Clamp(centre, chromaWidth)];
                int b = blended[Clamp(neighbour, chromaWidth)];
                value = ((a * 3) + b + 2) / 4;
            }

            destination[column] = value - chromaOffset;
        }
    }

    private static int Clamp(int index, int count)
    {
        if (index < 0) return 0;
        return index >= count ? count - 1 : index;
    }
}
