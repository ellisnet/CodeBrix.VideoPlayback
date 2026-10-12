using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Reads shaped frames both ways for the managed-versus-ffmpeg comparisons: the in-process shaping over a
/// <see cref="SyntheticEngine" />, and the ffmpeg prepare pass's lossless intermediate read back by the ffmpeg
/// engine - and measures how far apart they are.
/// </summary>
public static class ShapedFrames
{
    /// <summary>One frame's samples as values, with its place in time.</summary>
    public sealed class Snapshot
    {
        /// <summary>The frame's timestamp.</summary>
        public TimeSpan Timestamp;

        /// <summary>The frame's index.</summary>
        public long Index;

        /// <summary>The width.</summary>
        public int Width;

        /// <summary>The height.</summary>
        public int Height;

        /// <summary>The bit depth.</summary>
        public int BitDepth;

        /// <summary>Luma.</summary>
        public int[] Y;

        /// <summary>Cb.</summary>
        public int[] U;

        /// <summary>Cr.</summary>
        public int[] V;

        /// <summary>Alpha, or null.</summary>
        public int[] A;
    }

    /// <summary>The worst and mean absolute differences of each plane over a whole run of frames.</summary>
    public sealed class Difference
    {
        /// <summary>Worst luma difference.</summary>
        public int LumaWorst;

        /// <summary>Worst chroma difference (both planes).</summary>
        public int ChromaWorst;

        /// <summary>Worst alpha difference.</summary>
        public int AlphaWorst;

        /// <summary>Mean luma difference.</summary>
        public double LumaMean;

        /// <summary>Mean chroma difference.</summary>
        public double ChromaMean;

        /// <inheritdoc />
        public override string ToString() =>
            $"luma worst {LumaWorst} mean {LumaMean:0.###}, chroma worst {ChromaWorst} mean {ChromaMean:0.###}, alpha worst {AlphaWorst}";
    }

    /// <summary>Reads every frame of a source.</summary>
    /// <param name="source">The source; disposed.</param>
    /// <returns>The frames.</returns>
    public static List<Snapshot> ReadAll(IVideoFrameSource source)
    {
        List<Snapshot> frames = new List<Snapshot>();
        using (source)
        {
            while (source.TryReadFrame(out VideoFrameData frame))
            {
                frames.Add(new Snapshot
                {
                    Timestamp = frame.Timestamp,
                    Index = frame.FrameIndex,
                    Width = frame.Width,
                    Height = frame.Height,
                    BitDepth = frame.BitDepth,
                    Y = Values(frame.Y, frame.BytesPerSample),
                    U = frame.U.IsEmpty ? null : Values(frame.U, frame.BytesPerSample),
                    V = frame.V.IsEmpty ? null : Values(frame.V, frame.BytesPerSample),
                    A = frame.A.IsEmpty ? null : Values(frame.A, frame.BytesPerSample),
                });
            }
        }

        return frames;
    }

    /// <summary>The shaped frames the in-process path hands the encoder.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="request">The request.</param>
    /// <param name="work">The folder for a composed table.</param>
    /// <param name="alpha">Whether to ask for the alpha plane.</param>
    /// <returns>The frames.</returns>
    public static List<Snapshot> Managed(IVideoProcessor engine, VideoAuthoringRequest request, WorkFolder work, bool alpha)
    {
        ResolvedLutChain lut = LutChainResolver.Resolve(request.Video.Luts, request.OutputPath, work.Path, true, null);
        ManagedFrameShaping shaping = ManagedFrameShaping.Create(request, lut, true);
        VideoSourceOptions options = new VideoSourceOptions
        {
            PixelLayout = VideoPixelLayout.I420,
            BitDepth = request.Video.BitDepth,
            IncludeAlpha = alpha,
        };

        return ReadAll(InProcessAuthoring.OpenPicture(engine, request.SourcePath, options, shaping, false));
    }

    /// <summary>The frames the ffmpeg prepare pass writes, read back by the ffmpeg engine.</summary>
    /// <param name="request">The request.</param>
    /// <param name="work">The folder for the intermediate and a composed table.</param>
    /// <param name="alpha">Whether the intermediate keeps alpha.</param>
    /// <returns>The frames.</returns>
    public static List<Snapshot> FFmpeg(VideoAuthoringRequest request, WorkFolder work, bool alpha)
    {
        ResolvedLutChain lut = LutChainResolver.Resolve(request.Video.Luts, request.OutputPath, work.Path, true, null);
        string prepared = work.File("prepared-" + Guid.NewGuid().ToString("N") + ".mkv");
        AuthoringCommandFactory.BuildPreparePass(request, lut, prepared, alpha).ProcessSynchronously();

        VideoSourceOptions options = new VideoSourceOptions
        {
            PixelLayout = VideoPixelLayout.I420,
            BitDepth = request.Video.BitDepth,
            IncludeAlpha = alpha,
        };

        return ReadAll(CodeBrixVideoProcessing.Processor.OpenVideo(prepared, options));
    }

    /// <summary>Measures two runs of frames against each other, frame for frame.</summary>
    /// <param name="managed">The in-process frames.</param>
    /// <param name="ffmpeg">The ffmpeg frames.</param>
    /// <returns>The differences.</returns>
    /// <exception cref="InvalidOperationException">The runs differ in count or geometry.</exception>
    public static Difference Compare(IReadOnlyList<Snapshot> managed, IReadOnlyList<Snapshot> ffmpeg)
    {
        if (managed.Count != ffmpeg.Count)
        {
            throw new InvalidOperationException($"{managed.Count} managed frame(s) against {ffmpeg.Count} from ffmpeg.");
        }

        Difference difference = new Difference();
        long lumaTotal = 0, lumaCount = 0, chromaTotal = 0, chromaCount = 0;

        for (int i = 0; i < managed.Count; i++)
        {
            Snapshot a = managed[i];
            Snapshot b = ffmpeg[i];
            if (a.Width != b.Width || a.Height != b.Height)
            {
                throw new InvalidOperationException($"Frame {i} is {a.Width}x{a.Height} managed and {b.Width}x{b.Height} from ffmpeg.");
            }

            Accumulate(a.Y, b.Y, ref difference.LumaWorst, ref lumaTotal, ref lumaCount);
            Accumulate(a.U, b.U, ref difference.ChromaWorst, ref chromaTotal, ref chromaCount);
            Accumulate(a.V, b.V, ref difference.ChromaWorst, ref chromaTotal, ref chromaCount);
            long ignoredTotal = 0, ignoredCount = 0;
            if (a.A != null && b.A != null) Accumulate(a.A, b.A, ref difference.AlphaWorst, ref ignoredTotal, ref ignoredCount);
        }

        difference.LumaMean = lumaCount == 0 ? 0 : (double)lumaTotal / lumaCount;
        difference.ChromaMean = chromaCount == 0 ? 0 : (double)chromaTotal / chromaCount;
        return difference;
    }

    private static void Accumulate(int[] a, int[] b, ref int worst, ref long total, ref long count)
    {
        if (a == null || b == null) return;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            if (d > worst) worst = d;
            total += d;
            count++;
        }
    }

    private static int[] Values(VideoPlane plane, int bytes)
    {
        int samples = plane.RowLength / bytes;
        int[] values = new int[samples * plane.Rows];
        for (int row = 0; row < plane.Rows; row++)
        {
            ReadOnlySpan<byte> data = plane.GetRow(row);
            for (int x = 0; x < samples; x++)
            {
                values[(row * samples) + x] = bytes == 1 ? data[x] : data[2 * x] | (data[(2 * x) + 1] << 8);
            }
        }

        return values;
    }
}
