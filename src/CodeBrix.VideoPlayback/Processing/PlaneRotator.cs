using System;
using System.Runtime.InteropServices;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Turns one plane of samples by quarter turns and/or mirrors it - a luma, chroma or alpha plane at any bit
/// depth, or an interleaved RGBA plane. Pure sample moves: no value is changed, so the result is exact.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> The flips are applied FIRST, in the source's own orientation, and the rotation second:
/// <c>output = Rotate(Flip(source))</c> - the same picture FFmpeg's <c>hflip</c> and/or <c>vflip</c>
/// followed by the rotation produce.
/// </para>
/// <para>
/// <b>Geometry.</b> <see cref="RotationAngle.Clockwise90" /> and <see cref="RotationAngle.Clockwise270" />
/// swap the width and height; <see cref="GetRotatedSize" /> says what the destination must be. Clockwise 90
/// matches FFmpeg's <c>transpose=clock</c>, clockwise 270 matches <c>transpose=cclock</c>, 180 matches
/// <c>hflip,vflip</c>.
/// </para>
/// <para>
/// For whole frames - where a quarter turn of 4:2:0 needs both chroma planes turned too, and 4:2:2 cannot be
/// turned at all - use <see cref="FrameRotator" />.
/// </para>
/// </remarks>
public static class PlaneRotator
{
    /// <summary>The size a plane has after a rotation.</summary>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <param name="angle">The rotation.</param>
    /// <param name="rotatedWidth">The destination width: the source height after a quarter turn, else the width.</param>
    /// <param name="rotatedHeight">The destination height: the source width after a quarter turn, else the height.</param>
    public static void GetRotatedSize(int width, int height, RotationAngle angle, out int rotatedWidth, out int rotatedHeight)
    {
        bool quarter = angle == RotationAngle.Clockwise90 || angle == RotationAngle.Clockwise270;
        rotatedWidth = quarter ? height : width;
        rotatedHeight = quarter ? width : height;
    }

    /// <summary>
    /// The clockwise rotation that makes a stored picture upright, from the display-matrix angle an FFmpeg
    /// probe reports (its <c>DisplayMatrixRotation</c>, counter-clockwise degrees: -90 means the picture is
    /// meant to be shown turned 90 degrees clockwise).
    /// </summary>
    /// <param name="degrees">The display-matrix angle in counter-clockwise degrees; rounded to the nearest quarter turn.</param>
    /// <returns>The clockwise rotation to apply: for -90, <see cref="RotationAngle.Clockwise90" />; for 90, <see cref="RotationAngle.Clockwise270" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not a finite number.</exception>
    public static RotationAngle FromDisplayMatrixRotation(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, $"degrees must be a finite angle; it is {degrees}.");
        }

        long quarter = (long)Math.Round(-degrees / 90d, MidpointRounding.AwayFromZero);
        return (RotationAngle)((int)(((quarter % 4) + 4) % 4) * 90);
    }

    /// <summary>Rotates and/or flips one plane into another.</summary>
    /// <param name="source">The source samples.</param>
    /// <param name="width">The source width in samples (pixels, for interleaved RGBA).</param>
    /// <param name="height">The source height in rows.</param>
    /// <param name="sourceStride">The source stride in BYTES.</param>
    /// <param name="destination">
    /// Where the turned samples go, sized as <see cref="GetRotatedSize" /> says; must not overlap the source.
    /// Only the visible samples of each row are written.
    /// </param>
    /// <param name="destinationStride">The destination stride in BYTES.</param>
    /// <param name="bytesPerSample">1 for eight-bit samples, 2 for deeper samples.</param>
    /// <param name="channels">1 for a plane, 4 for interleaved RGBA (the four bytes of a pixel move together).</param>
    /// <param name="angle">The clockwise rotation.</param>
    /// <param name="flipHorizontal">Mirror left-right first (in the source's orientation).</param>
    /// <param name="flipVertical">Mirror top-bottom first (in the source's orientation).</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, stride, sample width, channel count or the angle is out of range.</exception>
    /// <exception cref="ArgumentException">A span is too small, or the two spans overlap.</exception>
    public static void Rotate(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int bytesPerSample,
        int channels,
        RotationAngle angle,
        bool flipHorizontal = false,
        bool flipVertical = false)
    {
        ProcessingGuard.Dimension(width, nameof(width));
        ProcessingGuard.Dimension(height, nameof(height));
        ProcessingGuard.BytesPerSample(bytesPerSample, nameof(bytesPerSample));
        ProcessingGuard.Channels(channels, nameof(channels));
        CheckAngle(angle);

        GetRotatedSize(width, height, angle, out int outputWidth, out int outputHeight);
        ProcessingGuard.Plane(source.Length, width, height, sourceStride, bytesPerSample, channels, nameof(source), nameof(sourceStride));
        ProcessingGuard.Plane(destination.Length, outputWidth, outputHeight, destinationStride, bytesPerSample, channels, nameof(destination), nameof(destinationStride));
        ProcessingGuard.NoOverlap(source, destination, nameof(destination));

        int element = bytesPerSample * channels;
        int rowBytes = outputWidth * element;

        // Output (dx, dy) reads the source at x = xx*dx + xy*dy + x0, y = yx*dx + yy*dy + y0.
        int xx, xy, x0, yx, yy, y0;
        switch (angle)
        {
            case RotationAngle.Clockwise90:
                xx = 0; xy = 1; x0 = 0;
                yx = -1; yy = 0; y0 = height - 1;
                break;
            case RotationAngle.Rotate180:
                xx = -1; xy = 0; x0 = width - 1;
                yx = 0; yy = -1; y0 = height - 1;
                break;
            case RotationAngle.Clockwise270:
                xx = 0; xy = -1; x0 = width - 1;
                yx = 1; yy = 0; y0 = 0;
                break;
            default:
                xx = 1; xy = 0; x0 = 0;
                yx = 0; yy = 1; y0 = 0;
                break;
        }

        if (flipHorizontal)
        {
            xx = -xx;
            xy = -xy;
            x0 = width - 1 - x0;
        }

        if (flipVertical)
        {
            yx = -yx;
            yy = -yy;
            y0 = height - 1 - y0;
        }

        // Rows that come out of the source in order are whole-row copies.
        if (xx == 1 && yx == 0)
        {
            for (int dy = 0; dy < outputHeight; dy++)
            {
                int sy = (yy * dy) + y0;
                source.Slice(sy * sourceStride, rowBytes).CopyTo(destination.Slice(dy * destinationStride, rowBytes));
            }

            return;
        }

        long step = ((long)yx * sourceStride) + ((long)xx * element);
        for (int dy = 0; dy < outputHeight; dy++)
        {
            long offset = ((long)((yy * dy) + y0) * sourceStride) + ((long)((xy * dy) + x0) * element);
            Span<byte> row = destination.Slice(dy * destinationStride, rowBytes);

            switch (element)
            {
                case 1:
                    CopyRow<byte>(source, offset, step, MemoryMarshal.Cast<byte, byte>(row));
                    break;
                case 2:
                    CopyRow<ushort>(source, offset, step, MemoryMarshal.Cast<byte, ushort>(row));
                    break;
                case 4:
                    CopyRow<uint>(source, offset, step, MemoryMarshal.Cast<byte, uint>(row));
                    break;
                default:
                    CopyRow<ulong>(source, offset, step, MemoryMarshal.Cast<byte, ulong>(row));
                    break;
            }
        }
    }

    internal static void CheckAngle(RotationAngle angle)
    {
        if (angle != RotationAngle.None
            && angle != RotationAngle.Clockwise90
            && angle != RotationAngle.Rotate180
            && angle != RotationAngle.Clockwise270)
        {
            throw new ArgumentOutOfRangeException(
                nameof(angle),
                angle,
                $"angle must be a quarter turn (0, 90, 180 or 270 degrees clockwise); it is {(int)angle}.");
        }
    }

    private static void CopyRow<T>(ReadOnlySpan<byte> source, long offset, long step, Span<T> row)
        where T : unmanaged
    {
        for (int dx = 0; dx < row.Length; dx++, offset += step)
        {
            row[dx] = MemoryMarshal.Read<T>(source.Slice((int)offset));
        }
    }
}
