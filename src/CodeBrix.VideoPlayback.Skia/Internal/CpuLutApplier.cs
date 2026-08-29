using System;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Skia.Effects;

namespace CodeBrix.VideoPlayback.Skia.Internal;

/// <summary>
/// Applies a resultant lookup table to a BGRA surface, one pixel at a time, on the processor.
/// </summary>
/// <remarks>
/// This is the slow road, taken only when an application has asked for it with
/// <c>AllowEffectsOnCpu</c>. Every pixel costs a trilinear interpolation of eight grid nodes, which is
/// roughly what the whole colour conversion costs - so a frame with effects on the processor takes about
/// twice as long as one without.
/// </remarks>
internal static class CpuLutApplier
{
    /// <summary>Applies a table to every pixel of a surface, in place.</summary>
    /// <param name="lut">The resultant table.</param>
    /// <param name="surface">The BGRA surface to transform.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> or <paramref name="surface" /> is null.</exception>
    internal static unsafe void Apply(Lut3D lut, BgraFrameBuffer surface)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));
        if (surface == null) throw new ArgumentNullException(nameof(surface));

        byte* baseAddress = (byte*)surface.Data;
        if (baseAddress == null) return;

        const float Inverse = 1f / 255f;

        for (int row = 0; row < surface.Height; row++)
        {
            byte* pixel = baseAddress + ((long)row * surface.Stride);
            for (int column = 0; column < surface.Width; column++, pixel += 4)
            {
                lut.Sample(
                    pixel[2] * Inverse,
                    pixel[1] * Inverse,
                    pixel[0] * Inverse,
                    out float red,
                    out float green,
                    out float blue);

                pixel[2] = ToByte(red);
                pixel[1] = ToByte(green);
                pixel[0] = ToByte(blue);
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
