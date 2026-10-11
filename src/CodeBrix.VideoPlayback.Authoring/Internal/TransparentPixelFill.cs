using System;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Gives every FULLY transparent pixel of an 8-bit 4:2:0 picture the colour of the nearest pixel that is not
/// fully transparent, so the colour under a soft edge carries on outward instead of falling to whatever the
/// source happened to keep there.
/// </summary>
/// <remarks>
/// <para>
/// WHY. A master file stores straight (not premultiplied) alpha, and a player that scales or filters the
/// picture mixes neighbouring colours BEFORE it applies the alpha. If the colour under the transparent region
/// is black - which is what most tools leave there - a light object's soft edge picks up a dark fringe. With
/// the edge colour carried outward, the mixing only ever meets colours that belong at the edge.
/// </para>
/// <para>
/// THE RULE. A pixel whose alpha is 0 takes the colour of the nearest pixel whose alpha is above 0; a pixel
/// whose alpha is above 0 - partly or fully opaque - keeps its colour exactly. The alpha plane is never
/// touched. A frame with no pixel above 0 has no colour to carry and is left unchanged.
/// </para>
/// <para>
/// THE ALGORITHM. One multi-source breadth-first flood over the luma grid: every pixel whose alpha is above 0
/// seeds the queue in raster order, and each pixel taken from the queue hands its SOURCE pixel to each
/// unvisited neighbour of its eight, always in the same order (up-left, up, up-right, left, right,
/// down-left, down, down-right). "Nearest" is therefore the chessboard distance, ties go to whichever source
/// reached the pixel first, and the result is the same on every run and every machine. Each fully transparent
/// pixel's luma is copied from its source pixel. A chroma sample is rewritten only when every luma pixel of
/// its 2x2 block (the part of it inside the frame, for an odd width or height) is fully transparent; it takes
/// the chroma sample that covers the source pixel of the block's top-left luma - source (sx, sy) gives chroma
/// (sx / 2, sy / 2). A chroma sample that covers any pixel above 0 is never changed, so neither a source
/// pixel's luma nor its chroma is ever overwritten. The work is linear in the number of pixels.
/// </para>
/// <para>
/// One instance owns its work buffers and reuses them frame after frame; it is not thread-safe.
/// </para>
/// </remarks>
internal sealed class TransparentPixelFill
{
    // Up-left, up, up-right, left, right, down-left, down, down-right.
    private static readonly int[] NeighbourX = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] NeighbourY = { -1, -1, -1, 0, 0, 1, 1, 1 };

    private int[] source = Array.Empty<int>();
    private int[] queue = Array.Empty<int>();

    /// <summary>Fills the fully transparent pixels of one frame in place.</summary>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="y">The luma plane, changed in place.</param>
    /// <param name="yStride">Bytes between the starts of two luma rows.</param>
    /// <param name="u">The Cb plane, (width + 1) / 2 by (height + 1) / 2 samples, changed in place.</param>
    /// <param name="v">The Cr plane, the same size as <paramref name="u" />, changed in place.</param>
    /// <param name="chromaStride">Bytes between the starts of two chroma rows.</param>
    /// <param name="alpha">The alpha plane, luma-sized, 0 = fully transparent. Never changed.</param>
    /// <param name="alphaStride">Bytes between the starts of two alpha rows.</param>
    /// <returns>True when at least one pixel was filled.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    internal bool Apply(
        int width,
        int height,
        Span<byte> y,
        int yStride,
        Span<byte> u,
        Span<byte> v,
        int chromaStride,
        ReadOnlySpan<byte> alpha,
        int alphaStride)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive.");

        int pixels = width * height;
        if (source.Length < pixels)
        {
            source = new int[pixels];
            queue = new int[pixels];
        }

        int head = 0;
        int tail = 0;
        bool anyTransparent = false;

        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> alphaRow = alpha.Slice(row * alphaStride, width);
            int rowStart = row * width;

            for (int column = 0; column < width; column++)
            {
                int index = rowStart + column;
                if (alphaRow[column] > 0)
                {
                    source[index] = index;
                    queue[tail++] = index;
                }
                else
                {
                    source[index] = -1;
                    anyTransparent = true;
                }
            }
        }

        // Nothing to fill, or nothing to fill it from.
        if (!anyTransparent || tail == 0) return false;

        while (head < tail)
        {
            int pixel = queue[head++];
            int pixelY = pixel / width;
            int pixelX = pixel - (pixelY * width);
            int from = source[pixel];

            for (int n = 0; n < 8; n++)
            {
                int neighbourX = pixelX + NeighbourX[n];
                int neighbourY = pixelY + NeighbourY[n];
                if (neighbourX < 0 || neighbourX >= width || neighbourY < 0 || neighbourY >= height) continue;

                int neighbour = (neighbourY * width) + neighbourX;
                if (source[neighbour] >= 0) continue;

                source[neighbour] = from;
                queue[tail++] = neighbour;
            }
        }

        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> alphaRow = alpha.Slice(row * alphaStride, width);
            int rowStart = row * width;

            for (int column = 0; column < width; column++)
            {
                if (alphaRow[column] > 0) continue;

                int from = source[rowStart + column];
                int fromY = from / width;
                int fromX = from - (fromY * width);
                y[(row * yStride) + column] = y[(fromY * yStride) + fromX];
            }
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;

        for (int chromaRow = 0; chromaRow < chromaHeight; chromaRow++)
        {
            int top = chromaRow * 2;
            int bottom = Math.Min(top + 1, height - 1);

            for (int chromaColumn = 0; chromaColumn < chromaWidth; chromaColumn++)
            {
                int left = chromaColumn * 2;
                int right = Math.Min(left + 1, width - 1);

                if (alpha[(top * alphaStride) + left] > 0
                    || alpha[(top * alphaStride) + right] > 0
                    || alpha[(bottom * alphaStride) + left] > 0
                    || alpha[(bottom * alphaStride) + right] > 0)
                {
                    continue;
                }

                int from = source[(top * width) + left];
                int fromY = from / width;
                int fromX = from - (fromY * width);
                int fromChroma = ((fromY / 2) * chromaStride) + (fromX / 2);
                int toChroma = (chromaRow * chromaStride) + chromaColumn;

                u[toChroma] = u[fromChroma];
                v[toChroma] = v[fromChroma];
            }
        }

        return true;
    }
}
