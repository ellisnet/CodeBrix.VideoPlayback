using System;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Processing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// Measures <see cref="PlanarLutGrader" /> against FFmpeg's <c>lut3d</c> filter wrapped in the Y'CbCr-to-RGB
/// conversions FFmpeg needs around it, and pins the identity behaviour.
/// </summary>
[Collection(FfmpegOracleCollection.Name)]
public class PlanarLutGraderTests
{
    private const int Width = 48;

    private const int Height = 32;

    // FFmpeg's lut3d only takes RGB, so the oracle is the whole chain FFmpeg would run: scale (BT.709 studio
    // range in, accurate rounding, full chroma; BILINEAR chroma resampling for 4:2:0) to planar RGB, lut3d
    // (tetrahedral), scale back. Measured against FFmpeg 7.1.5 on a 48x32 ramp frame, in levels of the frame's
    // own bit depth:
    //   twist 17^3     4:4:4  8-bit  luma worst 1 mean 0.360   chroma worst 1 mean 0.158
    //   twist 17^3     4:4:4 10-bit  luma worst 1 mean 0.319   chroma worst 2 mean 0.389
    //   teal/orange 33 4:4:4  8-bit  luma worst 1 mean 0.413   chroma worst 1 mean 0.227
    //   twist 17^3     4:2:0  8-bit  luma worst 1 mean 0.363   chroma worst 2 mean 0.234
    //   twist 17^3     4:2:0 10-bit  luma worst 1 mean 0.341   chroma worst 8 mean 0.721
    // The luma means near 0.4 are FFmpeg's lut3d TRUNCATING its interpolated value where this grader rounds
    // (the same one-level-low bias LutCrossStageEquivalenceTests measures); the 4:2:0 chroma gap is the two
    // downsamplers - FFmpeg's bilinear (tent) filter against this grader's box average of the grade's change -
    // and at ten bits 8 levels is two eight-bit levels. The bounds are just above the measurement.
    [Theory]
    [InlineData("twist", "yuv444p", 8, 1, 1, 0.42, 0.2)]
    [InlineData("twist", "yuv444p10le", 10, 1, 2, 0.37, 0.43)]
    [InlineData("generated/teal_orange_strong_33.cube", "yuv444p", 8, 1, 1, 0.46, 0.27)]
    [InlineData("twist", "yuv420p", 8, 1, 2, 0.42, 0.28)]
    [InlineData("twist", "yuv420p10le", 10, 1, 8, 0.39, 0.78)]
    public void Apply_agrees_with_ffmpeg_lut3d_to_a_measured_bound(
        string table,
        string pixelFormat,
        int bitDepth,
        int lumaWorst,
        int chromaWorst,
        double lumaMean,
        double chromaMean)
    {
        //Arrange
        FfmpegProcessingOracle.SkipWhenAbsent();
        string directory = Path.Combine(Path.GetTempPath(), "codebrix-grader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string cubePath;
            Lut3D lut;
            if (table == "twist")
            {
                lut = Twist(17);
                cubePath = Path.Combine(directory, "twist.cube");
                CubeLutFile.Write(lut, cubePath, "twist");
            }
            else
            {
                cubePath = LutTestAssets.Path(table);
                lut = CubeLutFile.ReadFile(cubePath).Lut3D;
            }

            bool subsampled = pixelFormat.StartsWith("yuv420p", StringComparison.Ordinal);
            int bytes = bitDepth > 8 ? 2 : 1;
            int chromaWidth = subsampled ? Width / 2 : Width;
            int chromaHeight = subsampled ? Height / 2 : Height;
            byte[] y = Ramp(Width, Height, bitDepth, 0);
            byte[] u = Ramp(chromaWidth, chromaHeight, bitDepth, 1);
            byte[] v = Ramp(chromaWidth, chromaHeight, bitDepth, 2);
            string rgb = bitDepth == 8 ? "gbrp" : "gbrp10le";
            string flags = subsampled ? "bilinear+accurate_rnd+full_chroma_int+full_chroma_inp" : "accurate_rnd+full_chroma_int+full_chroma_inp";
            string chain =
                $"scale=in_color_matrix=bt709:in_range=limited:flags={flags},format={rgb},"
                + $"lut3d=file='{cubePath}':interp=tetrahedral,"
                + $"scale=out_color_matrix=bt709:out_range=limited:flags={flags},format={pixelFormat}";

            byte[] outY = new byte[y.Length];
            byte[] outU = new byte[u.Length];
            byte[] outV = new byte[v.Length];

            //Act
            byte[] expected = FfmpegProcessingOracle.FilterRaw(Concat(y, u, v), pixelFormat, Width, Height, chain, pixelFormat);

            ReadOnlyPlanarFrame source = subsampled
                ? ReadOnlyPlanarFrame.CreateI420(Width, Height, bitDepth, y, Width * bytes, u, chromaWidth * bytes, v, chromaWidth * bytes)
                : ReadOnlyPlanarFrame.CreateI444(Width, Height, bitDepth, y, Width * bytes, u, Width * bytes, v, Width * bytes);
            PlanarFrame destination = subsampled
                ? PlanarFrame.CreateI420(Width, Height, bitDepth, outY, Width * bytes, outU, chromaWidth * bytes, outV, chromaWidth * bytes)
                : PlanarFrame.CreateI444(Width, Height, bitDepth, outY, Width * bytes, outU, Width * bytes, outV, Width * bytes);

            PlanarLutGrader.Apply(lut, source, destination, VideoMatrixCoefficients.Bt709, VideoColorRange.Limited);

            //Assert - luma and chroma measured separately, in levels of the frame's own bit depth
            byte[] expectedY = expected.AsSpan(0, y.Length).ToArray();
            byte[] expectedChroma = expected.AsSpan(y.Length).ToArray();
            (int worstY, double meanY) = ProcessingTestPatterns.Compare(expectedY, outY, bytes);
            (int worstC, double meanC) = ProcessingTestPatterns.Compare(expectedChroma, Concat(outU, outV), bytes);
            ProcessingTestPatterns.Record($"grader {table} {pixelFormat} luma", worstY, meanY);
            ProcessingTestPatterns.Record($"grader {table} {pixelFormat} chroma", worstC, meanC);

            worstY.Should().BeLessThanOrEqualTo(lumaWorst);
            meanY.Should().BeLessThanOrEqualTo(lumaMean);
            worstC.Should().BeLessThanOrEqualTo(chromaWorst);
            meanC.Should().BeLessThanOrEqualTo(chromaMean);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(ProcessingFrameLayout.I444, 8)]
    [InlineData(ProcessingFrameLayout.I420, 8)]
    [InlineData(ProcessingFrameLayout.I420, 10)]
    [InlineData(ProcessingFrameLayout.I422, 12)]
    public void Apply_with_an_identity_table_returns_an_in_gamut_picture_unchanged(ProcessingFrameLayout layout, int bitDepth)
    {
        //Arrange - a picture made FROM RGB, so every pixel is inside the colour cube the table spans
        int shiftX = layout == ProcessingFrameLayout.I444 ? 0 : 1;
        int shiftY = layout == ProcessingFrameLayout.I420 ? 1 : 0;
        int bytes = bitDepth > 8 ? 2 : 1;
        int chromaWidth = (Width + shiftX) >> shiftX;
        int chromaHeight = (Height + shiftY) >> shiftY;
        InGamut(bitDepth, shiftX, shiftY, out byte[] y, out byte[] u, out byte[] v);
        byte[] outY = new byte[y.Length];
        byte[] outU = new byte[u.Length];
        byte[] outV = new byte[v.Length];

        ReadOnlyPlanarFrame source = layout switch
        {
            ProcessingFrameLayout.I444 => ReadOnlyPlanarFrame.CreateI444(Width, Height, bitDepth, y, Width * bytes, u, chromaWidth * bytes, v, chromaWidth * bytes),
            ProcessingFrameLayout.I422 => ReadOnlyPlanarFrame.CreateI422(Width, Height, bitDepth, y, Width * bytes, u, chromaWidth * bytes, v, chromaWidth * bytes),
            _ => ReadOnlyPlanarFrame.CreateI420(Width, Height, bitDepth, y, Width * bytes, u, chromaWidth * bytes, v, chromaWidth * bytes),
        };
        PlanarFrame destination = layout switch
        {
            ProcessingFrameLayout.I444 => PlanarFrame.CreateI444(Width, Height, bitDepth, outY, Width * bytes, outU, chromaWidth * bytes, outV, chromaWidth * bytes),
            ProcessingFrameLayout.I422 => PlanarFrame.CreateI422(Width, Height, bitDepth, outY, Width * bytes, outU, chromaWidth * bytes, outV, chromaWidth * bytes),
            _ => PlanarFrame.CreateI420(Width, Height, bitDepth, outY, Width * bytes, outU, chromaWidth * bytes, outV, chromaWidth * bytes),
        };

        //Act
        new PlanarLutGrader(Lut3D.CreateIdentity(33)).Apply(source, destination, VideoMatrixCoefficients.Bt709, VideoColorRange.Limited);

        //Assert - measured: identical, every sample, every layout and depth (the bound is zero)
        (int worstY, _) = ProcessingTestPatterns.Compare(y, outY, bytes);
        (int worstU, _) = ProcessingTestPatterns.Compare(u, outU, bytes);
        (int worstV, _) = ProcessingTestPatterns.Compare(v, outV, bytes);
        worstY.Should().Be(0);
        worstU.Should().Be(0);
        worstV.Should().Be(0);
    }

    [Fact]
    public void Apply_to_rgba_grades_the_colour_and_keeps_the_alpha()
    {
        //Arrange
        byte[] rgba = ProcessingTestPatterns.Rgba(Width, Height);
        byte[] output = new byte[rgba.Length];
        Lut3D lut = Twist(17);

        //Act
        PlanarLutGrader.Apply(
            lut,
            ReadOnlyPlanarFrame.CreateRgba(Width, Height, rgba, Width * 4),
            PlanarFrame.CreateRgba(Width, Height, output, Width * 4),
            VideoMatrixCoefficients.Unspecified,
            VideoColorRange.Unspecified);

        //Assert - every pixel is the table's answer, rounded; alpha untouched
        for (int at = 0; at < rgba.Length; at += 4)
        {
            lut.Sample(rgba[at] / 255f, rgba[at + 1] / 255f, rgba[at + 2] / 255f, LutInterpolation.Tetrahedral, out float r, out float g, out float b);
            output[at].Should().Be((byte)Math.Clamp((int)((r * 255f) + 0.5f), 0, 255));
            output[at + 1].Should().Be((byte)Math.Clamp((int)((g * 255f) + 0.5f), 0, 255));
            output[at + 2].Should().Be((byte)Math.Clamp((int)((b * 255f) + 0.5f), 0, 255));
            output[at + 3].Should().Be(rgba[at + 3]);
        }
    }

    [Fact]
    public void Apply_copies_the_alpha_plane_through()
    {
        //Arrange
        byte[] y = Ramp(Width, Height, 8, 0);
        byte[] a = Ramp(Width, Height, 8, 3);
        byte[] u = Ramp(Width / 2, Height / 2, 8, 1);
        byte[] v = Ramp(Width / 2, Height / 2, 8, 2);
        byte[] outA = new byte[a.Length];

        //Act
        PlanarLutGrader.Apply(
            Twist(9),
            ReadOnlyPlanarFrame.CreateI420(Width, Height, 8, y, Width, u, Width / 2, v, Width / 2, a, Width),
            PlanarFrame.CreateI420(Width, Height, 8, new byte[y.Length], Width, new byte[u.Length], Width / 2, new byte[v.Length], Width / 2, outA, Width),
            VideoMatrixCoefficients.Smpte170M,
            VideoColorRange.Full);

        //Assert
        outA.Should().Equal(a);
    }

    [Fact]
    public void Apply_refuses_gray_unknown_matrices_and_mismatched_frames()
    {
        //Arrange
        Lut3D lut = Lut3D.CreateIdentity(2);
        byte[] plane = new byte[16];
        byte[] chroma = new byte[4];

        //Act
        Exception gray = Record.Exception(() => PlanarLutGrader.Apply(
            lut,
            ReadOnlyPlanarFrame.CreateGray(4, 4, 8, plane, 4),
            PlanarFrame.CreateGray(4, 4, 8, new byte[16], 4),
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited));

        Exception unspecified = Record.Exception(() => PlanarLutGrader.Apply(
            lut,
            ReadOnlyPlanarFrame.CreateI420(4, 4, 8, plane, 4, chroma, 2, chroma, 2),
            PlanarFrame.CreateI420(4, 4, 8, new byte[16], 4, new byte[4], 2, new byte[4], 2),
            VideoMatrixCoefficients.Unspecified,
            VideoColorRange.Limited));

        Exception bt2020 = Record.Exception(() => PlanarLutGrader.Apply(
            lut,
            ReadOnlyPlanarFrame.CreateI420(4, 4, 8, plane, 4, chroma, 2, chroma, 2),
            PlanarFrame.CreateI420(4, 4, 8, new byte[16], 4, new byte[4], 2, new byte[4], 2),
            VideoMatrixCoefficients.Bt2020NonConstantLuminance,
            VideoColorRange.Limited));

        Exception size = Record.Exception(() => PlanarLutGrader.Apply(
            lut,
            ReadOnlyPlanarFrame.CreateI420(4, 4, 8, plane, 4, chroma, 2, chroma, 2),
            PlanarFrame.CreateI420(2, 2, 8, new byte[4], 2, new byte[1], 1, new byte[1], 1),
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Limited));

        //Assert
        gray.Should().BeOfType<NotSupportedException>();
        gray.Message.Should().Contain("Gray");
        unspecified.Should().BeOfType<ArgumentException>();
        unspecified.Message.Should().Contain("matrix");
        bt2020.Should().BeOfType<NotSupportedException>();
        bt2020.Message.Should().Contain("Bt2020NonConstantLuminance");
        size.Should().BeOfType<ArgumentException>();
        size.Message.Should().Contain("2x2");
    }

    /// <summary>A smooth but decidedly non-identity table: a channel rotation blended with a curve.</summary>
    private static Lut3D Twist(int size)
    {
        float[] values = new float[size * size * size * 3];
        int at = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    float red = r / (float)(size - 1);
                    float green = g / (float)(size - 1);
                    float blue = b / (float)(size - 1);
                    values[at++] = Math.Clamp((0.7f * red) + (0.3f * green * green), 0f, 1f);
                    values[at++] = Math.Clamp((0.8f * green) + (0.2f * blue), 0f, 1f);
                    values[at++] = Math.Clamp(MathF.Sqrt((0.6f * blue) + (0.4f * red)), 0f, 1f);
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>A studio-range plane: luma ramps for channel 0, chroma ramps around the centre otherwise.</summary>
    private static byte[] Ramp(int width, int height, int bitDepth, int channel)
    {
        int scale = 1 << (bitDepth - 8);
        int bytes = bitDepth > 8 ? 2 : 1;
        byte[] plane = new byte[width * height * bytes];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double value = channel switch
                {
                    0 => 16 + (219d * ((0.7 * x / (width - 1)) + (0.3 * y / (height - 1)))),
                    1 => 128 + (100d * ((x / (double)(width - 1)) - 0.5)),
                    2 => 128 + (100d * ((y / (double)(height - 1)) - 0.5)),
                    _ => 255d * ((x + y) % 7) / 6,
                };

                ProcessingTestPatterns.Write(plane, (y * width) + x, bytes, (int)Math.Round(value * scale));
            }
        }

        return plane;
    }

    /// <summary>Builds BT.709 studio-range planes from an RGB pattern, chroma taken at each cell's first pixel.</summary>
    private static void InGamut(int bitDepth, int shiftX, int shiftY, out byte[] y, out byte[] u, out byte[] v)
    {
        int bytes = bitDepth > 8 ? 2 : 1;
        int scale = 1 << (bitDepth - 8);
        int chromaWidth = (Width + shiftX) >> shiftX;
        int chromaHeight = (Height + shiftY) >> shiftY;
        y = new byte[Width * Height * bytes];
        u = new byte[chromaWidth * chromaHeight * bytes];
        v = new byte[chromaWidth * chromaHeight * bytes];
        const double Kr = 0.2126;
        const double Kb = 0.0722;

        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                // Keep away from the cube's faces, so rounding cannot push a colour outside it.
                double r = 0.05 + (0.9 * column / (Width - 1));
                double g = 0.05 + (0.9 * row / (Height - 1));
                double b = 0.05 + (0.9 * ((column + row) % 11) / 10);
                double luma = (Kr * r) + ((1 - Kr - Kb) * g) + (Kb * b);
                ProcessingTestPatterns.Write(y, (row * Width) + column, bytes, (int)Math.Round(((219 * luma) + 16) * scale));

                if ((row & ((1 << shiftY) - 1)) == 0 && (column & ((1 << shiftX) - 1)) == 0)
                {
                    int cell = ((row >> shiftY) * chromaWidth) + (column >> shiftX);
                    double cb = (b - luma) / (2 * (1 - Kb));
                    double cr = (r - luma) / (2 * (1 - Kr));
                    ProcessingTestPatterns.Write(u, cell, bytes, (int)Math.Round(((224 * cb) + 128) * scale));
                    ProcessingTestPatterns.Write(v, cell, bytes, (int)Math.Round(((224 * cr) + 128) * scale));
                }
            }
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts) length += part.Length;
        byte[] all = new byte[length];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, at);
            at += part.Length;
        }

        return all;
    }
}
