using CodeBrix.VideoPlayback.Color.Luts;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// The two hand-built lookup tables the effect and presenter tests grade with.
/// </summary>
/// <remarks>
/// Both are exactly representable at every node and exactly invertible in the head, so an assertion can say
/// what a pixel must become rather than how close it must get. They are LINKED into the Skia test project so
/// that the effect tests in the core suite and the presenter tests in the Skia suite grade with one
/// definition of "invert" and one of "halve".
/// </remarks>
internal static class TestLuts
{
    /// <summary>Builds the table that turns every colour into its opposite.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <returns>The table.</returns>
    internal static Lut3D Invert(int size)
    {
        float[] values = new float[size * size * size * 3];
        float last = size - 1;
        int index = 0;

        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    values[index++] = 1f - (red / last);
                    values[index++] = 1f - (green / last);
                    values[index++] = 1f - (blue / last);
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>Builds the table that multiplies every channel by a constant.</summary>
    /// <param name="size">The number of nodes along each axis.</param>
    /// <param name="factor">What every channel is multiplied by.</param>
    /// <returns>The table.</returns>
    internal static Lut3D Scale(int size, float factor)
    {
        float[] values = new float[size * size * size * 3];
        float last = size - 1;
        int index = 0;

        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    values[index++] = (red / last) * factor;
                    values[index++] = (green / last) * factor;
                    values[index++] = (blue / last) * factor;
                }
            }
        }

        return new Lut3D(size, values);
    }
}
