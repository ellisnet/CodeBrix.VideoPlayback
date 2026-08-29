using System;
using System.Diagnostics;
using System.IO;
using CodeBrix.VideoPlayback.Color;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Skia.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Bakes an effective table out of a chain with percentages, then applies it to the SAME pixels twice - once
/// through FFmpeg's <c>lut3d</c> filter, which is what the authoring pipeline will do, and once through this
/// library's own processor path, which is what playback does - and measures how far apart the two answers
/// are, for BOTH ways of reading a table.
/// </summary>
/// <remarks>
/// <para>
/// This is the claim the whole engine exists to make: a grade the application shows and the grade the
/// pipeline encodes are the same grade. FFmpeg's filter reads the baked table's nodes TETRAHEDRALLY, and so
/// does this library by default, so the two now agree to the byte on almost every pixel. Reading it
/// TRILINEARLY instead - the cheaper texture-filter read, which a consumer may choose - costs about half a
/// level on average, and that cost is measured here rather than assumed.
/// </para>
/// <para>
/// FFmpeg is a developer-machine tool and never ships, so it is used here as an ORACLE only. The tests skip
/// themselves when it is not installed, exactly as the other host-dependent tests do.
/// </para>
/// </remarks>
public class LutCrossStageEquivalenceTests
{
    private const string FfmpegPath = "/usr/bin/ffmpeg";

    private const int Width = 64;

    private const int Height = 48;

    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void An_effective_table_applied_by_ffmpeg_and_by_the_processor_path_agree_to_a_measured_bound(
        LutInterpolation interpolation)
    {
        //Arrange
        SkipWhenFfmpegIsAbsent();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "codebrix-lut-equivalence-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            Lut3D twist = Twist(17);
            Lut1D gamma = Gamma(256, 2.2f);

            Lut3D effective = LutComposer.Compose(
                new[] { new LutLayer(twist, 70d), new LutLayer(gamma, 35d) });

            string cubePath = Path.Combine(directory, "effective.cube");
            CubeLutFile.Write(effective, cubePath, "effective");

            byte[] source = BuildTestFrame();

            //Act
            byte[] throughFfmpeg = ApplyWithFfmpeg(directory, cubePath, source);
            byte[] throughLibrary = ApplyWithCpuLutApplier(effective, source, interpolation);

            //Assert
            int worst = 0;
            long total = 0;
            int ffmpegHigher = 0;
            for (int index = 0; index < source.Length; index++)
            {
                int signed = throughFfmpeg[index] - throughLibrary[index];
                if (signed > 0) ffmpegHigher++;

                int difference = Math.Abs(signed);
                if (difference > worst) worst = difference;
                total += difference;
            }

            double mean = total / (double)source.Length;

            // Measured on this machine against FFmpeg 7.1.5-0+deb13u1, 3072 pixels of 9216 colour bytes:
            //   TETRAHEDRAL  worst 1 level of 255, mean 0.4894, and FFmpeg is LOWER on all 4510 of the
            //                bytes that differ and higher on none.
            //   TRILINEAR    worst 1 level,        mean 0.4902, FFmpeg lower on all 4518, higher on none.
            //
            // The two readings barely move that number, and the reason is in the direction: this is not
            // interpolation, it is ROUNDING. FFmpeg's eight-bit lut3d path truncates the interpolated
            // value where CpuLutApplier rounds half up, so FFmpeg lands one level low on about half the
            // bytes and never one level high. The interpolation difference itself is three hundred times
            // smaller - see the test below, which measures it with FFmpeg out of the picture entirely.
            //
            // The bounds are just above the measurement; a baked table that did not mean what FFmpeg reads
            // would be out by tens of levels, not one.
            worst.Should().BeLessThanOrEqualTo(3);
            mean.Should().BeLessThan(0.8d);
            ffmpegHigher.Should().BeLessThanOrEqualTo(2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void An_identity_chain_baked_and_applied_by_ffmpeg_returns_the_picture_it_was_given()
    {
        //Arrange
        SkipWhenFfmpegIsAbsent();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "codebrix-lut-identity-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            Lut3D effective = LutComposer.Compose(new[] { new LutLayer(Lut3D.CreateIdentity(33)) });
            string cubePath = Path.Combine(directory, "identity.cube");
            CubeLutFile.Write(effective, cubePath, "identity");

            byte[] source = BuildTestFrame();

            //Act
            byte[] throughFfmpeg = ApplyWithFfmpeg(directory, cubePath, source);

            //Assert - a 33-node identity is exact at its nodes and linear between them, so this is tight
            int worst = 0;
            for (int index = 0; index < source.Length; index++)
            {
                int difference = Math.Abs(throughFfmpeg[index] - source[index]);
                if (difference > worst) worst = difference;
            }

            worst.Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }


    [Fact]
    public void The_two_readings_of_one_effective_table_differ_by_far_less_than_the_rounding_does()
    {
        //Arrange - the same table read both ways over the same pixels, with FFmpeg out of it entirely
        Lut3D effective = LutComposer.Compose(
            new[] { new LutLayer(Twist(17), 70d), new LutLayer(Gamma(256, 2.2f), 35d) });

        byte[] source = BuildTestFrame();

        //Act
        byte[] tetrahedral = ApplyWithCpuLutApplier(effective, source, LutInterpolation.Tetrahedral);
        byte[] trilinear = ApplyWithCpuLutApplier(effective, source, LutInterpolation.Trilinear);

        int worst = 0;
        long total = 0;
        for (int index = 0; index < source.Length; index++)
        {
            int difference = Math.Abs(tetrahedral[index] - trilinear[index]);
            if (difference > worst) worst = difference;
            total += difference;
        }

        double mean = total / (double)source.Length;

        //Assert - measured: worst 1 level of 255, mean 0.00152, i.e. 14 of 9216 bytes differ and each by
        //one level. On a table as smooth as a composed 33-node grade the two readings are all but the same
        //picture, which is WHY the gap to FFmpeg above barely moves between them: that gap is eight-bit
        //rounding and this is the whole of the interpolation difference. Tetrahedral is the default
        //because it is what grading tools and FFmpeg MEAN by a lookup - and because it holds the neutral
        //axis exactly - not because it repaints this fixture.
        worst.Should().BeLessThanOrEqualTo(2);
        mean.Should().BeLessThan(0.02d);
    }

    private static void SkipWhenFfmpegIsAbsent() =>
        Assert.SkipUnless(
            File.Exists(FfmpegPath),
            $"FFmpeg is not installed at '{FfmpegPath}'; it is the oracle these tests compare against.");

    /// <summary>A deterministic picture that reaches into every corner of the colour cube.</summary>
    /// <returns>RGB24 pixels, three bytes each, row by row.</returns>
    private static byte[] BuildTestFrame()
    {
        byte[] pixels = new byte[Width * Height * 3];

        int at = 0;
        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                pixels[at++] = (byte)((column * 255) / (Width - 1));
                pixels[at++] = (byte)((row * 255) / (Height - 1));
                pixels[at++] = (byte)(((column + row) * 255) / (Width + Height - 2));
            }
        }

        return pixels;
    }

    private static byte[] ApplyWithFfmpeg(string directory, string cubePath, byte[] source)
    {
        string inputPath = Path.Combine(directory, "in.rgb");
        string outputPath = Path.Combine(directory, "out.rgb");
        File.WriteAllBytes(inputPath, source);

        ProcessStartInfo start = new ProcessStartInfo(FfmpegPath)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in new[]
                 {
                     "-hide_banner",
                     "-nostdin",
                     "-y",
                     "-f", "rawvideo",
                     "-pix_fmt", "rgb24",
                     "-s", $"{Width}x{Height}",
                     "-i", inputPath,
                     "-vf", "lut3d=file=" + cubePath,
                     "-f", "rawvideo",
                     "-pix_fmt", "rgb24",
                     outputPath,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start);
        string diagnostics = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, "ffmpeg said: " + diagnostics);

        byte[] result = File.ReadAllBytes(outputPath);
        result.Length.Should().Be(source.Length);
        return result;
    }

    private static byte[] ApplyWithCpuLutApplier(
        Lut3D effective,
        byte[] source,
        LutInterpolation interpolation)
    {
        using BgraFrameBufferPool pool = new BgraFrameBufferPool();
        BgraFrameBuffer surface = pool.Rent(Width, Height);

        Span<byte> pixels = surface.AsSpan();
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            pixels[(pixel * 4) + 2] = source[(pixel * 3)];
            pixels[(pixel * 4) + 1] = source[(pixel * 3) + 1];
            pixels[pixel * 4] = source[(pixel * 3) + 2];
            pixels[(pixel * 4) + 3] = 255;
        }

        CpuLutApplier.Apply(effective, surface, interpolation);

        byte[] result = new byte[source.Length];
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            result[pixel * 3] = pixels[(pixel * 4) + 2];
            result[(pixel * 3) + 1] = pixels[(pixel * 4) + 1];
            result[(pixel * 3) + 2] = pixels[pixel * 4];
        }

        return result;
    }

    private static Lut3D Twist(int size)
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
                    float r = red / last;
                    float g = green / last;
                    float b = blue / last;
                    float luma = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                    float shift = (luma - 0.5f) * 0.6f;

                    values[index++] = Clamp(r + shift);
                    values[index++] = Clamp(g + (shift * 0.2f));
                    values[index++] = Clamp(b - shift);
                }
            }
        }

        return new Lut3D(size, values);
    }

    private static Lut1D Gamma(int size, float exponent)
    {
        float[] curve = new float[size];
        for (int point = 0; point < size; point++)
        {
            curve[point] = MathF.Pow(point / (float)(size - 1), exponent);
        }

        return new Lut1D(curve);
    }

    private static float Clamp(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
}
