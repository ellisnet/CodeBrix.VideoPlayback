using System;
using System.Globalization;
using System.IO;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Media;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// A short synthetic 4:2:0 clip computed in memory - 8 or 10 bits, with or without alpha, with or without a
/// display rotation - that a <see cref="SyntheticEngine" /> serves frame by frame and that
/// <see cref="WriteForFFmpeg" /> writes to disk, losslessly, for the ffmpeg engine to read: the same pixels on
/// both paths, which is what makes the managed-versus-ffmpeg comparisons fair.
/// </summary>
/// <remarks>
/// Nothing here is third-party media. The picture is a raster-order luma ramp from studio black to studio
/// white (877 distinct levels at 10 bits), smooth chroma ramps that move with the frame index, a hard-edged block
/// whose level counts the frames, and - with alpha - a plane that is 0 in the left quarter, ramps across the
/// middle and is fully opaque on the right.
/// </remarks>
public sealed class SyntheticClip
{
    /// <summary>Describes a clip.</summary>
    /// <param name="width">The stored width.</param>
    /// <param name="height">The stored height.</param>
    /// <param name="bitDepth">8 or 10.</param>
    /// <param name="frameCount">How many frames.</param>
    /// <param name="rateNumerator">The frame rate's numerator.</param>
    /// <param name="rateDenominator">The frame rate's denominator.</param>
    /// <param name="alpha">Whether it carries an alpha plane.</param>
    /// <param name="rotationDegrees">The CLOCKWISE rotation a player applies for display (0, 90, 180, 270).</param>
    public SyntheticClip(
        int width,
        int height,
        int bitDepth = 8,
        int frameCount = 20,
        int rateNumerator = 10,
        int rateDenominator = 1,
        bool alpha = false,
        int rotationDegrees = 0)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        FrameCount = frameCount;
        Rate = new FrameRate(rateNumerator, rateDenominator);
        Alpha = alpha;
        RotationDegrees = rotationDegrees;
    }

    /// <summary>The stored width.</summary>
    public int Width { get; }

    /// <summary>The stored height.</summary>
    public int Height { get; }

    /// <summary>8 or 10.</summary>
    public int BitDepth { get; }

    /// <summary>How many frames.</summary>
    public int FrameCount { get; }

    /// <summary>The frame rate.</summary>
    public FrameRate Rate { get; }

    /// <summary>Whether it carries an alpha plane.</summary>
    public bool Alpha { get; }

    /// <summary>The clockwise display rotation.</summary>
    public int RotationDegrees { get; }

    /// <summary>The duration: the frame count at the frame rate.</summary>
    public TimeSpan Duration => Rate.TimestampOf(FrameCount);

    /// <summary>The chroma width (rounded up).</summary>
    public int ChromaWidth => (Width + 1) / 2;

    /// <summary>The chroma height (rounded up).</summary>
    public int ChromaHeight => (Height + 1) / 2;

    /// <summary>The largest sample value at the clip's depth.</summary>
    public int Maximum => (1 << BitDepth) - 1;

    /// <summary>One frame's samples at the clip's own depth, as values.</summary>
    /// <param name="index">The frame index.</param>
    /// <param name="y">The luma samples, row by row.</param>
    /// <param name="u">The Cb samples.</param>
    /// <param name="v">The Cr samples.</param>
    /// <param name="a">The alpha samples, or null without alpha.</param>
    public void Samples(int index, out int[] y, out int[] u, out int[] v, out int[] a)
    {
        int scale = 1 << (BitDepth - 8);
        int pixels = Width * Height;
        y = new int[pixels];
        u = new int[ChromaWidth * ChromaHeight];
        v = new int[ChromaWidth * ChromaHeight];
        a = Alpha ? new int[pixels] : null;

        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                int i = (row * Width) + column;
                int luma = (16 * scale) + (int)((long)i * 219 * scale / Math.Max(1, pixels - 1));

                // A hard-edged block that counts the frames, so a dropped or doubled frame is visible.
                if (row >= 2 && row < 10 && column >= 2 && column < 10)
                {
                    luma = (16 + ((index * 23) % 200)) * scale;
                }

                y[i] = luma;

                if (a != null)
                {
                    int quarter = Width / 4;
                    a[i] = column < quarter ? 0
                        : column >= Width - quarter ? Maximum
                        : (int)((long)(column - quarter) * Maximum / Math.Max(1, Width - (2 * quarter)));
                }
            }
        }

        for (int row = 0; row < ChromaHeight; row++)
        {
            for (int column = 0; column < ChromaWidth; column++)
            {
                int i = (row * ChromaWidth) + column;
                u[i] = (16 + Triangle((column * 3) + (index * 3))) * scale;
                v[i] = (16 + Triangle((row * 5) + column + (index * 5))) * scale;
            }
        }
    }

    /// <summary>One frame as the engine contract describes it, at a requested depth.</summary>
    /// <param name="index">The frame index.</param>
    /// <param name="depth">8 or 10: the clip's own depth, or the other one (rounded down, or widened).</param>
    /// <param name="includeAlpha">Whether to attach the alpha plane (when the clip has one).</param>
    /// <returns>The frame, on the clip's nominal timestamp grid.</returns>
    public VideoFrameData Frame(int index, int depth, bool includeAlpha)
    {
        Samples(index, out int[] y, out int[] u, out int[] v, out int[] a);
        int bytes = depth > 8 ? 2 : 1;

        VideoPlane alphaPlane = includeAlpha && a != null
            ? VideoPlane.FromArray(Pack(a, depth), Width * bytes, Height)
            : default;

        return VideoFrameData.CreateI420(
            Width,
            Height,
            VideoPlane.FromArray(Pack(y, depth), Width * bytes, Height),
            VideoPlane.FromArray(Pack(u, depth), ChromaWidth * bytes, ChromaHeight),
            VideoPlane.FromArray(Pack(v, depth), ChromaWidth * bytes, ChromaHeight),
            Rate.TimestampOf(index),
            index,
            alphaPlane,
            depth);
    }

    /// <summary>
    /// Writes the clip losslessly (FFV1 in Matroska, BT.709 studio range) for the ffmpeg engine and the ffmpeg
    /// prepare pass to read, stamped with the clip's display rotation.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>The path.</returns>
    public string WriteForFFmpeg(string path)
    {
        string folder = Path.GetDirectoryName(path);
        Directory.CreateDirectory(folder);
        string raw = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".raw");
        string stored = RotationDegrees == 0 ? path : Path.Combine(folder, "stored-" + Path.GetFileName(path));

        using (FileStream file = new FileStream(raw, FileMode.Create, FileAccess.Write))
        {
            for (int index = 0; index < FrameCount; index++)
            {
                Samples(index, out int[] y, out int[] u, out int[] v, out int[] a);
                file.Write(Pack(y, BitDepth));
                file.Write(Pack(u, BitDepth));
                file.Write(Pack(v, BitDepth));
                if (a != null) file.Write(Pack(a, BitDepth));
            }
        }

        string pixelFormat = (Alpha ? "yuva420p" : "yuv420p") + (BitDepth > 8 ? "10le" : string.Empty);
        string size = Number(Width) + "x" + Number(Height);
        string rate = Number(Rate.Numerator) + "/" + Number(Rate.Denominator);

        FFMpegArguments
            .FromFileInput(raw, false, input => input.WithCustomArgument(
                "-f rawvideo -pix_fmt " + pixelFormat + " -s " + size + " -framerate " + rate))
            .OutputToFile(stored, true, output => output
                .WithVideoCodec("ffv1")
                .ForcePixelFormat(pixelFormat)
                // TAGGED, never converted: setparams only labels the frames. Output-side -colorspace / -color_*
                // options make ffmpeg 7.1 negotiate a conversion from the untagged raw input instead, and the
                // picture that comes out is not the one that went in.
                .WithCustomArgument("-vf setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709")
                .ForceFormat("matroska"))
            .ProcessSynchronously();

        if (RotationDegrees != 0)
        {
            FFMpegArguments
                .FromFileInput(stored, false, input => input.WithCustomArgument(
                    "-display_rotation " + Number(-RotationDegrees)))
                .OutputToFile(path, true, output => output
                    .CopyChannel()
                    .ForceFormat("matroska"))
                .ProcessSynchronously();
        }

        File.Delete(raw);
        return path;
    }

    /// <summary>Packs sample values at a depth: one byte each at 8 bits, two little-endian bytes at 10.</summary>
    /// <param name="values">Values at the clip's depth.</param>
    /// <param name="depth">The depth to pack at; converted from the clip's own when they differ.</param>
    /// <returns>The bytes.</returns>
    public byte[] Pack(int[] values, int depth)
    {
        if (depth <= 8)
        {
            byte[] narrow = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                int value = BitDepth > 8 ? Math.Min(255, (values[i] + 2) >> 2) : values[i];
                narrow[i] = (byte)value;
            }

            return narrow;
        }

        byte[] wide = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            int value = BitDepth > 8 ? values[i] : values[i] << 2;
            wide[2 * i] = (byte)value;
            wide[(2 * i) + 1] = (byte)(value >> 8);
        }

        return wide;
    }

    // 0 up to 223 and back down again: a chroma ramp with no hard edge in it.
    private static int Triangle(int value)
    {
        int t = value % 448;
        return t < 224 ? t : 447 - t;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
