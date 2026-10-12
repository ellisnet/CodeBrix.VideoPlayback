using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Deterministic synthetic pictures and the difference measurement the processing tests share.
/// </summary>
internal static class ProcessingTestPatterns
{
    /// <summary>
    /// A plane with smooth ramps, a hard-edged block, a fine checker strip and seeded noise - enough structure
    /// that a scaler's kernel, a rotation's orientation and a filter's edges all show.
    /// </summary>
    /// <returns>Packed samples: one byte each when <paramref name="bitDepth" /> is 8, else two little-endian bytes.</returns>
    public static byte[] Plane(int width, int height, int bitDepth, int seed = 1)
    {
        int maximum = (1 << bitDepth) - 1;
        int bytes = bitDepth > 8 ? 2 : 1;
        byte[] plane = new byte[width * height * bytes];
        Random random = new Random(seed);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double value;
                if (x >= width / 4 && x < width / 2 && y >= height / 4 && y < height / 2)
                {
                    value = 0.9;
                }
                else if (y >= (3 * height) / 4)
                {
                    value = ((x / 2) + (y / 2)) % 2 == 0 ? 0.15 : 0.85;
                }
                else
                {
                    value = (0.6 * x / (width - 1)) + (0.3 * y / (height - 1));
                }

                value += (random.NextDouble() - 0.5) * 0.06;
                int sample = (int)Math.Round(Math.Clamp(value, 0d, 1d) * maximum);
                Write(plane, (y * width) + x, bytes, sample);
            }
        }

        return plane;
    }

    /// <summary>An interleaved RGBA picture whose four channels each carry a different pattern.</summary>
    public static byte[] Rgba(int width, int height)
    {
        byte[] r = Plane(width, height, 8, 11);
        byte[] g = Plane(width, height, 8, 12);
        byte[] b = Plane(width, height, 8, 13);
        byte[] a = Plane(width, height, 8, 14);
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[(i * 4) + 0] = r[i];
            rgba[(i * 4) + 1] = (byte)(255 - g[i]);
            rgba[(i * 4) + 2] = b[(width * height) - 1 - i];
            rgba[(i * 4) + 3] = a[i];
        }

        return rgba;
    }

    public static int Read(byte[] plane, int index, int bytes) =>
        bytes == 1 ? plane[index] : plane[2 * index] | (plane[(2 * index) + 1] << 8);

    public static void Write(byte[] plane, int index, int bytes, int value)
    {
        if (bytes == 1)
        {
            plane[index] = (byte)value;
            return;
        }

        plane[2 * index] = (byte)value;
        plane[(2 * index) + 1] = (byte)(value >> 8);
    }

    /// <summary>Worst and mean absolute sample difference between two packed planes.</summary>
    public static (int Worst, double Mean) Compare(byte[] expected, byte[] actual, int bytes)
    {
        if (expected.Length != actual.Length)
        {
            throw new InvalidOperationException($"Lengths differ: {expected.Length} and {actual.Length}.");
        }

        int count = expected.Length / bytes;
        int worst = 0;
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int difference = Math.Abs(Read(expected, i, bytes) - Read(actual, i, bytes));
            if (difference > worst) worst = difference;
            total += difference;
        }

        return (worst, total / (double)count);
    }

    /// <summary>
    /// When CODEBRIX_PROCESSING_MEASURE names a file, appends one measurement line to it - how the bounds in
    /// these tests were measured. Silent otherwise.
    /// </summary>
    public static void Record(string what, int worst, double mean)
    {
        string path = Environment.GetEnvironmentVariable("CODEBRIX_PROCESSING_MEASURE");
        if (string.IsNullOrEmpty(path)) return;

        lock (typeof(ProcessingTestPatterns))
        {
            File.AppendAllText(path, $"{what}: worst {worst}, mean {mean:F4}{Environment.NewLine}");
        }
    }
}
