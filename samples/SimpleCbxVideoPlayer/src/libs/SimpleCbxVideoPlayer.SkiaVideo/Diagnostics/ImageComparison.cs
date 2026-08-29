using SkiaSharp;
using System;
using System.IO;

namespace SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;

/// <summary>Compares two captured pictures, so that an unattended run can assert about one.</summary>
/// <remarks>
/// A byte-for-byte comparison of two PNGs answers "identical or not" and nothing else. Two pictures that
/// SHOULD agree - the same grade reached by two different routes, say - can differ in the last bit of a
/// channel and still be right, so what is wanted is a distance, and a tolerance to compare it against.
/// </remarks>
public static class ImageComparison
{
    /// <summary>Compares two picture files.</summary>
    /// <param name="firstPath">One picture.</param>
    /// <param name="secondPath">The other.</param>
    /// <returns>How far apart they are, or null when either file cannot be read as a picture.</returns>
    public static ImageComparisonResult Compare(string firstPath, string secondPath)
    {
        if (!File.Exists(firstPath) || !File.Exists(secondPath)) { return null; }

        using SKBitmap first = SKBitmap.Decode(firstPath);
        using SKBitmap second = SKBitmap.Decode(secondPath);

        if (first == null || second == null) { return null; }

        if (first.Width != second.Width || first.Height != second.Height)
        {
            return new ImageComparisonResult(0, 0, false, 255, 255, 100);
        }

        long total = 0;
        var counted = 0;
        var maximum = 0;
        var differing = 0;

        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                SKColor left = first.GetPixel(x, y);
                SKColor right = second.GetPixel(x, y);

                var red = Math.Abs(left.Red - right.Red);
                var green = Math.Abs(left.Green - right.Green);
                var blue = Math.Abs(left.Blue - right.Blue);

                total += red + green + blue;
                counted += 3;

                var worst = Math.Max(red, Math.Max(green, blue));

                if (worst > maximum) { maximum = worst; }

                if (worst > 0) { differing++; }
            }
        }

        var pixels = first.Width * (long)first.Height;

        return new ImageComparisonResult(
            first.Width,
            first.Height,
            true,
            maximum,
            counted == 0 ? 0 : total / (double)counted,
            pixels == 0 ? 0 : differing * 100.0 / pixels);
    }
}
