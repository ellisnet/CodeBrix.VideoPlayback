using System;
using System.IO;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Builds frames in code, so a rendering test can state exactly what went in and check exactly what came out
/// without a container or a codec anywhere near it.
/// </summary>
public static class TestFrames
{
    /// <summary>
    /// The colour description these frames carry when a test does not care: the one essentially all video in
    /// these containers uses.
    /// </summary>
    public static VideoColorInfo Bt709Limited { get; } = new VideoColorInfo(
        VideoColorPrimaries.Bt709,
        VideoTransferCharacteristics.Bt709,
        VideoMatrixCoefficients.Bt709,
        VideoColorRange.Limited,
        VideoChromaSiting.Vertical);

    /// <summary>Creates a frame filled with a pattern that varies in all three planes.</summary>
    /// <param name="pool">The pool the frame's buffer comes from.</param>
    /// <param name="width">The frame's width in luma samples.</param>
    /// <param name="height">The frame's height in luma samples.</param>
    /// <param name="layout">The plane layout.</param>
    /// <param name="color">The colour description to record on the frame.</param>
    /// <param name="seed">A number that shifts the pattern, so two frames differ.</param>
    /// <param name="timestamp">The frame's timestamp.</param>
    /// <param name="frameNumber">The frame's number.</param>
    /// <returns>A frame the caller owns and must dispose.</returns>
    public static unsafe VideoFrame CreatePattern(
        PinnedFrameBufferPool pool,
        int width,
        int height,
        VideoPixelLayout layout,
        VideoColorInfo color,
        int seed = 0,
        TimeSpan timestamp = default,
        long frameNumber = 0)
    {
        VideoFrameBufferDescriptor descriptor = new VideoFrameBufferDescriptor(width, height, layout, 8);
        VideoFrameBuffer buffer = pool.Rent(descriptor);

        Fill(buffer.Y, (x, y) => (byte)(16 + ((seed * 7) + (x * 3) + (y * 5)) % 220));

        if (!buffer.U.IsEmpty)
        {
            Fill(buffer.U, (x, y) => (byte)(16 + ((seed * 3) + (x * 11) + (y * 2)) % 224));
            Fill(buffer.V, (x, y) => (byte)(16 + ((seed * 5) + (x * 2) + (y * 13)) % 224));
        }

        return VideoFrame.Create(
            buffer,
            new VideoFrameInfo(
                width,
                height,
                width,
                height,
                layout,
                8,
                timestamp,
                timestamp.Ticks,
                frameNumber,
                true,
                color,
                null),
            pool);
    }

    /// <summary>Creates a frame in which every sample of every plane holds one constant.</summary>
    /// <param name="pool">The pool the frame's buffer comes from.</param>
    /// <param name="width">The frame's width in luma samples.</param>
    /// <param name="height">The frame's height in luma samples.</param>
    /// <param name="luma">The value every luma sample takes.</param>
    /// <param name="blueChroma">The value every first-chroma sample takes.</param>
    /// <param name="redChroma">The value every second-chroma sample takes.</param>
    /// <param name="color">The colour description to record on the frame.</param>
    /// <returns>A frame the caller owns and must dispose.</returns>
    public static VideoFrame CreateFlat(
        PinnedFrameBufferPool pool,
        int width,
        int height,
        byte luma,
        byte blueChroma,
        byte redChroma,
        VideoColorInfo color)
    {
        VideoFrameBufferDescriptor descriptor =
            new VideoFrameBufferDescriptor(width, height, VideoPixelLayout.I420, 8);

        VideoFrameBuffer buffer = pool.Rent(descriptor);

        Fill(buffer.Y, (x, y) => luma);
        Fill(buffer.U, (x, y) => blueChroma);
        Fill(buffer.V, (x, y) => redChroma);

        return VideoFrame.Create(
            buffer,
            new VideoFrameInfo(
                width,
                height,
                width,
                height,
                VideoPixelLayout.I420,
                8,
                TimeSpan.Zero,
                0,
                0,
                true,
                color,
                null),
            pool);
    }

    /// <summary>Finds the golden corpus, wherever the test assembly happens to be running from.</summary>
    /// <param name="name">The asset's file name.</param>
    /// <returns>The asset's full path.</returns>
    public static string Asset(string name)
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "assets", name);
        if (File.Exists(beside)) return beside;

        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "assets", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        Assert.Skip($"The golden asset '{name}' is not present; run tests/assets/generate-cbv-assets.sh.");
        return null;
    }

    private static unsafe void Fill(VideoFramePlane plane, Func<int, int, byte> sample)
    {
        if (plane.IsEmpty) return;

        byte* data = (byte*)plane.Data;
        for (int y = 0; y < plane.Height; y++)
        {
            byte* row = data + ((long)y * plane.Stride);
            for (int x = 0; x < plane.Width; x++) row[x] = sample(x, y);
        }
    }
}
