using System;
using System.Diagnostics;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Skia.Effects;
using CodeBrix.VideoPlayback.Skia.Internal;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Measures the two ways the shader reads the resultant lookup atlas - tetrahedrally, which is the default
/// and what FFmpeg does, and trilinearly, which is what a texture filter does - against the processor path
/// reading the same table and against FFmpeg reading the same table.
/// </summary>
/// <remarks>
/// <para>
/// One effective table is composed once and then READ two ways, so what is measured here is the atlas read
/// alone and not the folding of the chain. The picture is a synthetic YUV frame put through the whole
/// shader, so the comparison is made on the pixels the shader really produces.
/// </para>
/// <para>
/// The processor comparison runs everywhere. The FFmpeg comparison skips itself when
/// <c>/usr/bin/ffmpeg</c> is absent, and the graphics-device comparison skips itself when no headless
/// context can be made.
/// </para>
/// </remarks>
public class LutShaderInterpolationTests
{
    private const string FfmpegPath = "/usr/bin/ffmpeg";

    private const int Width = 48;

    private const int Height = 32;

    private const int Nodes = 33;

    private readonly Xunit.ITestOutputHelper output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Where the measured differences are written.</param>
    public LutShaderInterpolationTests(Xunit.ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Both_lookup_shader_variants_compile_and_read_the_atlas_their_own_way()
    {
        //Arrange
        string tetrahedral = YuvShaderSource.Build(LutInterpolation.Tetrahedral);
        string trilinear = YuvShaderSource.Build(LutInterpolation.Trilinear);

        //Act
        using SKRuntimeEffect first = SKRuntimeEffect.CreateShader(tetrahedral, out string firstErrors);
        using SKRuntimeEffect second = SKRuntimeEffect.CreateShader(trilinear, out string secondErrors);

        //Assert
        (first != null).Should().BeTrue(firstErrors ?? string.Empty);
        (second != null).Should().BeTrue(secondErrors ?? string.Empty);

        // The tetrahedral variant fetches NODES and must not be filtered; the trilinear one leans on the
        // filter and must be.
        tetrahedral.Should().Contain("lookupNode");
        trilinear.Should().NotContain("lookupNode");
        YuvShaderSource.NeedsFilteredAtlas(LutInterpolation.Tetrahedral).Should().BeFalse();
        YuvShaderSource.NeedsFilteredAtlas(LutInterpolation.Trilinear).Should().BeTrue();
    }

    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void The_shader_and_the_processor_read_the_resultant_table_the_same_way(
        LutInterpolation interpolation)
    {
        //Arrange
        Lut3D effective = BuildEffectiveTable();

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame =
            TestFrames.CreatePattern(pool, Width, Height, VideoPixelLayout.I420, TestFrames.Bt709Limited, 4);

        using SKImage atlas = BuildAtlas(effective);

        //Act
        byte[] plain = RenderWithShader(frame, null, 0, interpolation, null);
        byte[] graded = RenderWithShader(frame, atlas, Nodes, interpolation, null);
        byte[] wanted = ApplyOnProcessor(effective, plain, interpolation);

        //Assert
        Measurement measured = Compare(graded, wanted);
        output.WriteLine(
            $"shader vs processor, {interpolation}: worst {measured.Worst}, mean {measured.Mean:0.0000}.");

        // Both read the very same table by the very same rule. What is left is not the read: the
        // processor here is handed the shader's ALREADY EIGHT-BIT conversion output and grades that,
        // while the shader grades the float colour before it is ever quantised - so the comparison
        // carries one extra rounding that playback itself does not. It is the same for both readings,
        // which is the point. Measured: worst 2 levels of 255, mean 0.2322 tetrahedral / 0.2289
        // trilinear.
        measured.Worst.Should().BeLessThanOrEqualTo(3);
        measured.Mean.Should().BeLessThan(0.4d);
    }

    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void The_shader_and_ffmpeg_read_the_resultant_table_to_a_measured_bound(
        LutInterpolation interpolation)
    {
        //Arrange
        SkipWhenFfmpegIsAbsent();
        Lut3D effective = BuildEffectiveTable();

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        using VideoFrame frame =
            TestFrames.CreatePattern(pool, Width, Height, VideoPixelLayout.I420, TestFrames.Bt709Limited, 4);

        using SKImage atlas = BuildAtlas(effective);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "codebrix-lut-shader-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            byte[] plain = RenderWithShader(frame, null, 0, interpolation, null);
            byte[] graded = RenderWithShader(frame, atlas, Nodes, interpolation, null);

            string cubePath = Path.Combine(directory, "effective.cube");
            CubeLutFile.Write(effective, cubePath, "effective");

            //Act - FFmpeg is handed exactly the pixels the shader started the lookup from
            byte[] throughFfmpeg = ToBgra(
                RunFfmpeg(directory, cubePath, ToRgb24(plain)),
                plain.Length / 4);

            //Assert
            Measurement measured = Compare(graded, throughFfmpeg);
            output.WriteLine(
                $"shader vs ffmpeg lut3d, {interpolation}: worst {measured.Worst}, "
                + $"mean {measured.Mean:0.0000}.");

            // FFmpeg's lut3d reads TETRAHEDRALLY. Measured against FFmpeg 7.1.5-0+deb13u1:
            //   tetrahedral  worst 2 levels of 255, mean 0.4167
            //   trilinear    worst 2 levels,        mean 0.4188
            // The two barely differ, and that is honest rather than disappointing: this comparison is
            // dominated by rounding, twice over. FFmpeg is handed the shader's ALREADY EIGHT-BIT
            // conversion output and grades that, while the shader grades the float colour before it is
            // ever quantised; and FFmpeg's eight-bit path truncates where this library rounds half up
            // (measured and fenced in LutCrossStageEquivalenceTests). What this test proves is the
            // ceiling - the shader's whole graded output agrees with FFmpeg's to two levels of 255 by
            // either reading - and the interpolation difference itself is measured with the rounding out
            // of the way in LutCrossStageEquivalenceTests.
            measured.Worst.Should().BeLessThanOrEqualTo(3);
            measured.Mean.Should().BeLessThan(0.6d);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(LutInterpolation.Tetrahedral)]
    [InlineData(LutInterpolation.Trilinear)]
    public void The_real_graphics_device_reads_the_atlas_the_same_way_the_raster_backend_does(
        LutInterpolation interpolation)
    {
        //Arrange
        Assert.SkipUnless(
            HeadlessGraphicsContext.IsAvailable,
            "No headless graphics context: " + HeadlessGraphicsContext.UnavailableReason);

        Lut3D effective = BuildEffectiveTable();

        //Act & Assert
        HeadlessGraphicsContext.Run(graphics =>
        {
            using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
            using VideoFrame frame = TestFrames.CreatePattern(
                pool,
                Width,
                Height,
                VideoPixelLayout.I420,
                TestFrames.Bt709Limited,
                4);

            using SKImage atlas = BuildAtlas(effective);

            byte[] onRaster = RenderWithShader(frame, atlas, Nodes, interpolation, null);
            byte[] onDevice = RenderWithShader(frame, atlas, Nodes, interpolation, graphics);

            Measurement measured = Compare(onRaster, onDevice);
            output.WriteLine(
                $"graphics device vs raster, {interpolation}: worst {measured.Worst}, "
                + $"mean {measured.Mean:0.0000}.");

            // Measured on this machine's Mesa surfaceless context: worst 1 level of 255 for both, mean
            // 0.0035 tetrahedral and 0.0043 trilinear - the driver's own rounding, nothing more. The
            // manual node fetches the tetrahedral variant makes land on the same texels on the device as
            // they do on the raster backend, which is the thing that could have gone wrong.
            measured.Worst.Should().BeLessThanOrEqualTo(2);
            measured.Mean.Should().BeLessThan(0.05d);
        });
    }

    [Fact]
    public void The_presenter_reads_tables_tetrahedrally_unless_it_is_told_otherwise()
    {
        //Arrange
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter();

        //Act & Assert
        presenter.EffectInterpolation.Should().Be(LutInterpolation.Tetrahedral);

        presenter.EffectInterpolation = LutInterpolation.Trilinear;
        presenter.EffectInterpolation.Should().Be(LutInterpolation.Trilinear);

        Action refused = () => presenter.EffectInterpolation = (LutInterpolation)7;
        refused.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Changing_how_tables_are_read_recomposes_the_chain()
    {
        //Arrange - a chain whose tables bend, so the two readings really do give different tables
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter
        {
            RenderPath = Rendering.VideoRenderPath.Cpu,
            AllowEffectsOnCpu = true,
            EffectLutSize = Nodes,
        };

        presenter.Effects.Add(new LutEffect(Twist(17), "twist", 70d));
        presenter.Effects.Add(new LutEffect(Gamma(256, 2.2f), "gamma", 35d));

        //Act
        Lut3D tetrahedral = presenter.GetResultantLut();
        long afterFirst = presenter.GetStatistics().EffectCompositions;

        presenter.EffectInterpolation = LutInterpolation.Trilinear;
        Lut3D trilinear = presenter.GetResultantLut();
        long afterSecond = presenter.GetStatistics().EffectCompositions;

        //Assert
        afterFirst.Should().Be(1L);
        afterSecond.Should().Be(2L);

        float worst = 0f;
        for (int index = 0; index < tetrahedral.Values.Length; index++)
        {
            float difference = Math.Abs(tetrahedral.Values[index] - trilinear.Values[index]);
            if (difference > worst) worst = difference;
        }

        // The setting reaches the FOLDING as well as the reading, so the two tables differ.
        worst.Should().BeGreaterThan(0f);
    }

    private static void SkipWhenFfmpegIsAbsent() =>
        Assert.SkipUnless(
            File.Exists(FfmpegPath),
            $"FFmpeg is not installed at '{FfmpegPath}'; it is the oracle this test compares against.");

    private static Lut3D BuildEffectiveTable() =>
        LutComposer.Compose(
            new[] { new LutLayer(Twist(17), 70d), new LutLayer(Gamma(256, 2.2f), 35d) },
            new LutComposerOptions { OutputSize = Nodes });

    /// <summary>Writes a table into the atlas image the presenter would have built for it.</summary>
    /// <param name="lut">The resultant table, whose size must be the atlas size.</param>
    /// <returns>The atlas as an image, RGBA and unpremultiplied, owning its own pixels.</returns>
    /// <remarks>
    /// The pixels are COPIED into the image rather than borrowed from the bitmap. The presenter can use
    /// SKImage.FromBitmap because it keeps the bitmap in a field for the image's whole life; a helper that
    /// lets the bitmap go at the end of the method cannot, and borrowing there is a use-after-free that
    /// shows up as an occasional frame of garbage rather than as a crash.
    /// </remarks>
    private static unsafe SKImage BuildAtlas(Lut3D lut)
    {
        EffectComposer composer = new EffectComposer(lut.Size);
        composer.ApplyLut(lut);

        int width = LutAtlas.GetWidth(lut.Size);
        int height = LutAtlas.GetHeight(lut.Size);
        SKImageInfo info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        byte[] pixels = new byte[(long)width * height * LutAtlas.BytesPerPixel];
        LutAtlas.Write(composer, pixels, width * LutAtlas.BytesPerPixel);

        fixed (byte* first = pixels)
        {
            return SKImage.FromPixelCopy(info, (IntPtr)first, width * LutAtlas.BytesPerPixel);
        }
    }

    private static byte[] RenderWithShader(
        VideoFrame frame,
        SKImage lookupAtlas,
        int lookupSize,
        LutInterpolation interpolation,
        GRContext graphics)
    {
        SKImageInfo info =
            new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using YuvSurfaceRenderer renderer = new YuvSurfaceRenderer();
        using SKSurface surface = graphics == null
            ? SKSurface.Create(info)
            : SKSurface.Create(graphics, false, info);

        SKImage atlas = lookupAtlas;
        if (graphics != null && atlas != null) atlas = atlas.ToTextureImage(graphics);

        try
        {
            renderer.Render(frame, surface, graphics, atlas, lookupSize, interpolation);

            byte[] pixels = new byte[frame.Width * frame.Height * 4];
            using SKImage snapshot = surface.Snapshot();
            snapshot.ReadPixels(
                new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0),
                frame.Width * 4,
                0,
                0);

            return pixels;
        }
        finally
        {
            if (!ReferenceEquals(atlas, lookupAtlas)) atlas?.Dispose();
        }
    }

    private static unsafe byte[] ApplyOnProcessor(
        Lut3D lut,
        byte[] bgra,
        LutInterpolation interpolation)
    {
        using Color.BgraFrameBufferPool pool = new Color.BgraFrameBufferPool();
        Color.BgraFrameBuffer surface = pool.Rent(Width, Height);

        Span<byte> pixels = surface.AsSpan();
        bgra.AsSpan().CopyTo(pixels);

        CpuLutApplier.Apply(lut, surface, interpolation);

        byte[] result = new byte[bgra.Length];
        pixels.CopyTo(result);
        return result;
    }

    private static byte[] ToRgb24(byte[] bgra)
    {
        byte[] rgb = new byte[bgra.Length / 4 * 3];
        for (int pixel = 0; pixel < bgra.Length / 4; pixel++)
        {
            rgb[pixel * 3] = bgra[(pixel * 4) + 2];
            rgb[(pixel * 3) + 1] = bgra[(pixel * 4) + 1];
            rgb[(pixel * 3) + 2] = bgra[pixel * 4];
        }

        return rgb;
    }

    private static byte[] ToBgra(byte[] rgb, int pixels)
    {
        byte[] bgra = new byte[pixels * 4];
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            bgra[pixel * 4] = rgb[(pixel * 3) + 2];
            bgra[(pixel * 4) + 1] = rgb[(pixel * 3) + 1];
            bgra[(pixel * 4) + 2] = rgb[pixel * 3];
            bgra[(pixel * 4) + 3] = 255;
        }

        return bgra;
    }

    private static byte[] RunFfmpeg(string directory, string cubePath, byte[] source)
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
                     "-hide_banner", "-nostdin", "-y",
                     "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{Width}x{Height}", "-i", inputPath,
                     "-vf", "lut3d=file=" + cubePath,
                     "-f", "rawvideo", "-pix_fmt", "rgb24", outputPath,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start);
        string diagnostics = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, "ffmpeg said: " + diagnostics);
        return File.ReadAllBytes(outputPath);
    }

    /// <summary>Compares two BGRA buffers over their colour channels, ignoring alpha.</summary>
    /// <param name="first">One buffer.</param>
    /// <param name="second">The other, of the same length.</param>
    /// <returns>The worst and the mean absolute difference in levels of 255.</returns>
    private static Measurement Compare(byte[] first, byte[] second)
    {
        int worst = 0;
        long total = 0;
        int counted = 0;

        for (int index = 0; index < first.Length; index++)
        {
            if (index % 4 == 3) continue;

            int difference = Math.Abs(first[index] - second[index]);
            if (difference > worst) worst = difference;
            total += difference;
            counted++;
        }

        return new Measurement(worst, total / (double)counted);
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

    private readonly struct Measurement
    {
        internal Measurement(int worst, double mean)
        {
            Worst = worst;
            Mean = mean;
        }

        internal int Worst { get; }

        internal double Mean { get; }
    }
}
