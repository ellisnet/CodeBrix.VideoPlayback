using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Media;

// The engine contract and the playback core both name a colour-range enum; the grader takes the core's.
using CoreColorMatrix = CodeBrix.VideoProcessing.Core.Media.VideoColorMatrix;
using CoreColorRange = CodeBrix.VideoProcessing.Core.Media.VideoColorRange;
using GraderColorRange = CodeBrix.VideoPlayback.Decoding.VideoColorRange;
using VideoMatrixCoefficients = CodeBrix.VideoPlayback.Decoding.VideoMatrixCoefficients;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// A frame source that hands on an engine's frames shaped in process - turned upright, scaled, converted to a
/// constant frame rate and graded, in that order - by the core's managed processing primitives.
/// </summary>
/// <remarks>
/// <para>
/// Every frame it hands on is its own: the samples live in arrays this source allocated, so they stay valid
/// after the next read whatever the engine does with its buffers. A duplicated frame is the same picture handed
/// on again with the next output timestamp; a dropped one is never graded.
/// </para>
/// <para>
/// The frame-rate decision for a frame needs the NEXT frame's timestamp, so the source reads one frame ahead.
/// Timestamps on the engine's nominal grid (<c>FrameRate.TimestampOf(index)</c>, which is what a constant-rate
/// engine delivers) are pushed to the resampler as exact fractions; anything else as a <see cref="TimeSpan" />.
/// </para>
/// </remarks>
internal sealed class ShapedVideoFrameSource : IVideoFrameSource
{
    private readonly IVideoFrameSource inner;
    private readonly ManagedFrameShaping shaping;
    private readonly bool alphaOnly;
    private readonly RotationAngle rotation;
    private readonly int turnedWidth;
    private readonly int turnedHeight;
    private readonly int width;
    private readonly int height;
    private readonly bool scales;
    private readonly FrameRateResampler resampler;
    private readonly PlanarLutGrader grader;
    private readonly VideoMatrixCoefficients gradeMatrix;
    private readonly GraderColorRange gradeRange;
    private readonly Queue<VideoFrameData> ready = new Queue<VideoFrameData>();
    private FrameScaler scaler;
    private OwnedFrame pending;
    private bool exhausted;
    private bool disposed;

    /// <summary>Wraps a frame source.</summary>
    /// <param name="inner">The engine's source; disposed with this one.</param>
    /// <param name="shaping">What to do to each frame.</param>
    /// <param name="alphaOnly">True to hand on the alpha plane as Gray frames, never graded.</param>
    /// <exception cref="VideoAuthoringException">The source cannot be shaped as asked.</exception>
    internal ShapedVideoFrameSource(IVideoFrameSource inner, ManagedFrameShaping shaping, bool alphaOnly)
    {
        this.inner = inner;
        this.shaping = shaping;
        this.alphaOnly = alphaOnly;

        VideoSourceInfo source = inner.Info;
        if (source.PixelLayout != VideoPixelLayout.I420)
        {
            throw new VideoAuthoringException(
                "The in-process shaping reads 4:2:0 frames, and the engine's frame source delivers "
                + source.PixelLayout + ".");
        }

        rotation = shaping.RotationFor(source.RotationDegrees);
        PlaneRotator.GetRotatedSize(source.Width, source.Height, rotation, out turnedWidth, out turnedHeight);
        shaping.TargetSize(turnedWidth, turnedHeight, out width, out height);
        scales = !shaping.FrameSize.IsSourceSize;

        FrameRate rate = source.FrameRate;
        if (shaping.ConvertsFrameRate)
        {
            resampler = rate.IsSpecified
                ? new FrameRateResampler(shaping.FrameRateNumerator, shaping.FrameRateDenominator, rate.Numerator, rate.Denominator)
                : new FrameRateResampler(shaping.FrameRateNumerator, shaping.FrameRateDenominator);
            rate = new FrameRate(shaping.FrameRateNumerator, shaping.FrameRateDenominator);
        }

        if (!alphaOnly && shaping.HasGrade)
        {
            if (shaping.Lut == null)
            {
                throw new VideoAuthoringException("The in-process grade has no table loaded; this is a dry-run shaping.");
            }

            grader = new PlanarLutGrader(shaping.Lut);
            gradeMatrix = GradeMatrixFor(source.ColorMatrix);
            gradeRange = source.ColorRange == CoreColorRange.Full ? GraderColorRange.Full : GraderColorRange.Limited;
        }

        long estimated = source.EstimatedFrameCount;
        if (resampler != null && source.Duration > TimeSpan.Zero) estimated = rate.FrameCountFor(source.Duration);

        Info = new VideoSourceInfo
        {
            Width = width,
            Height = height,
            FrameRate = rate,
            Duration = source.Duration,
            EstimatedFrameCount = estimated,
            PixelLayout = alphaOnly ? VideoPixelLayout.Gray : VideoPixelLayout.I420,
            BitDepth = source.BitDepth,
            SourceBitDepth = source.SourceBitDepth,
            SourceHasAlpha = source.SourceHasAlpha,
            HasAlpha = !alphaOnly && source.HasAlpha,
            RotationDegrees = rotation == RotationAngle.None ? source.RotationDegrees : 0,
            ColorRange = alphaOnly ? CoreColorRange.Full : source.ColorRange,
            ColorMatrix = alphaOnly ? CoreColorMatrix.Unspecified : source.ColorMatrix,
        };
    }

    /// <summary>What the shaped frames are: their size, rate and layout after shaping.</summary>
    public VideoSourceInfo Info { get; }

    /// <summary>The turn applied to every frame.</summary>
    internal RotationAngle Rotation => rotation;

    /// <summary>The width after the turn and before scaling.</summary>
    internal int TurnedWidth => turnedWidth;

    /// <summary>The height after the turn and before scaling.</summary>
    internal int TurnedHeight => turnedHeight;

    /// <summary>A shaped source does not seek: it is read once, start to end.</summary>
    public bool CanSeek => false;

    /// <summary>Always refuses: a shaped source is read once, start to end.</summary>
    /// <param name="position">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void Seek(TimeSpan position) =>
        throw new NotSupportedException("The in-process shaping reads its source once, from the start.");

    /// <summary>Reads the next shaped frame.</summary>
    /// <param name="frame">The frame, or the empty frame at the end.</param>
    /// <returns>True for a frame; false at the end.</returns>
    public bool TryReadFrame(out VideoFrameData frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        while (true)
        {
            if (ready.Count > 0)
            {
                frame = ready.Dequeue();
                return true;
            }

            if (exhausted)
            {
                frame = default;
                return false;
            }

            if (!inner.TryReadFrame(out VideoFrameData decoded))
            {
                exhausted = true;
                if (resampler != null && pending != null) Emit(pending, resampler.Complete());
                pending = null;
                continue;
            }

            OwnedFrame shaped = Shape(decoded);

            if (resampler == null)
            {
                ready.Enqueue(Finish(shaped).ToFrame(decoded.Timestamp, decoded.FrameIndex));
                continue;
            }

            FrameRateDecision decision = Push(decoded);
            if (pending != null) Emit(pending, decision);
            pending = shaped;
        }
    }

    /// <summary>Disposes the engine's source.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        inner.Dispose();
    }

    /// <summary>
    /// The matrix the grade converts with: the one the engine says its samples are in. Unspecified is read as
    /// BT.601, which is what FFmpeg's scaler assumes for an untagged picture on its way into <c>lut3d</c>.
    /// </summary>
    /// <param name="matrix">The engine's matrix.</param>
    /// <returns>The grader's matrix.</returns>
    /// <exception cref="VideoAuthoringException">The matrix is one the grader has no constants for.</exception>
    internal static VideoMatrixCoefficients GradeMatrixFor(CoreColorMatrix matrix)
    {
        switch (matrix)
        {
            case CoreColorMatrix.Bt709:
                return VideoMatrixCoefficients.Bt709;
            case CoreColorMatrix.Bt601:
            case CoreColorMatrix.Unspecified:
                return VideoMatrixCoefficients.Smpte170M;
            default:
                throw new VideoAuthoringException(
                    "The in-process colour grade converts BT.709 and BT.601 pictures, and the engine describes this "
                    + "one with the " + matrix + " matrix. Grade it on a machine with the ffmpeg engine, or clear "
                    + "Video.Luts.");
        }
    }

    private FrameRateDecision Push(VideoFrameData decoded)
    {
        FrameRate rate = inner.Info.FrameRate;
        if (rate.IsSpecified && decoded.Timestamp == rate.TimestampOf(decoded.FrameIndex))
        {
            return resampler.Push(decoded.FrameIndex, rate.Denominator, rate.Numerator);
        }

        return resampler.Push(decoded.Timestamp);
    }

    // A decided frame: graded once, then handed on once per output slot it fills. Output n is stamped the way the
    // engine contract stamps a constant-rate frame - FrameRate.TimestampOf(n), n * den / num - so the frames match
    // what any engine would hand over from a file already at that rate.
    private void Emit(OwnedFrame frame, FrameRateDecision decision)
    {
        if (!decision.HasFrame || decision.OutputCount == 0) return;

        OwnedFrame finished = Finish(frame);
        for (long i = 0; i < decision.OutputCount; i++)
        {
            long index = decision.FirstOutputIndex + i;
            ready.Enqueue(finished.ToFrame(Info.FrameRate.TimestampOf(index), index));
        }
    }

    // The grade, last, on a frame that is going to be handed on.
    private OwnedFrame Finish(OwnedFrame frame)
    {
        if (grader == null) return frame;

        OwnedFrame graded = OwnedFrame.Allocate(frame.Width, frame.Height, frame.BitDepth, frame.Gray, frame.Alpha != null);
        grader.Apply(frame.AsReadOnly(), graded.AsWritable(), gradeMatrix, gradeRange);
        return graded;
    }

    // Copies the decoded frame into owned buffers (as Gray from its alpha plane for the alpha pass), turns it and
    // scales it.
    private OwnedFrame Shape(VideoFrameData decoded)
    {
        OwnedFrame frame = alphaOnly ? OwnedFrame.FromAlpha(decoded) : OwnedFrame.FromColour(decoded);

        if (rotation != RotationAngle.None)
        {
            OwnedFrame turned = OwnedFrame.Allocate(turnedWidth, turnedHeight, frame.BitDepth, frame.Gray, frame.Alpha != null);
            try
            {
                FrameRotator.Rotate(frame.AsReadOnly(), turned.AsWritable(), rotation);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                throw new VideoAuthoringException(
                    "AutoRotate could not turn the source the right way up in process: " + ex.Message
                    + " Set Video.AutoRotate to false to encode it as stored, or author on a machine with the "
                    + "ffmpeg engine.",
                    ex);
            }

            frame = turned;
        }

        if (!scales) return frame;

        if (scaler == null)
        {
            scaler = new FrameScaler(
                frame.Gray ? ProcessingFrameLayout.Gray : ProcessingFrameLayout.I420,
                frame.Width,
                frame.Height,
                width,
                height,
                shaping.FilterFor(frame.Width, frame.Height, width, height));
        }

        OwnedFrame scaled = OwnedFrame.Allocate(width, height, frame.BitDepth, frame.Gray, frame.Alpha != null);
        scaler.Scale(frame.AsReadOnly(), scaled.AsWritable());
        return scaled;
    }

    /// <summary>A frame whose samples live in tightly packed arrays this source owns.</summary>
    private sealed class OwnedFrame
    {
        internal int Width;
        internal int Height;
        internal int BitDepth;
        internal bool Gray;
        internal byte[] Y;
        internal byte[] U;
        internal byte[] V;
        internal byte[] Alpha;

        private int Bytes => BitDepth > 8 ? 2 : 1;

        private int ChromaWidth => (Width + 1) / 2;

        private int ChromaHeight => (Height + 1) / 2;

        internal static OwnedFrame Allocate(int width, int height, int bitDepth, bool gray, bool alpha)
        {
            OwnedFrame frame = new OwnedFrame { Width = width, Height = height, BitDepth = bitDepth, Gray = gray };
            int bytes = frame.Bytes;
            frame.Y = new byte[width * height * bytes];
            if (!gray)
            {
                frame.U = new byte[frame.ChromaWidth * frame.ChromaHeight * bytes];
                frame.V = new byte[frame.ChromaWidth * frame.ChromaHeight * bytes];
            }

            if (alpha) frame.Alpha = new byte[width * height * bytes];
            return frame;
        }

        internal static OwnedFrame FromColour(VideoFrameData decoded)
        {
            OwnedFrame frame = Allocate(decoded.Width, decoded.Height, decoded.BitDepth, false, decoded.HasAlpha);
            decoded.Y.CopyTo(frame.Y);
            decoded.U.CopyTo(frame.U);
            decoded.V.CopyTo(frame.V);
            if (frame.Alpha != null) decoded.A.CopyTo(frame.Alpha);
            return frame;
        }

        internal static OwnedFrame FromAlpha(VideoFrameData decoded)
        {
            OwnedFrame frame = Allocate(decoded.Width, decoded.Height, decoded.BitDepth, true, false);
            if (decoded.HasAlpha)
            {
                decoded.A.CopyTo(frame.Y);
            }
            else if (frame.Bytes == 1)
            {
                Array.Fill(frame.Y, (byte)255);
            }
            else
            {
                int maximum = (1 << frame.BitDepth) - 1;
                for (int i = 0; i < frame.Y.Length; i += 2)
                {
                    frame.Y[i] = (byte)maximum;
                    frame.Y[i + 1] = (byte)(maximum >> 8);
                }
            }

            return frame;
        }

        internal ReadOnlyPlanarFrame AsReadOnly()
        {
            int bytes = Bytes;
            if (Gray) return ReadOnlyPlanarFrame.CreateGray(Width, Height, BitDepth, Y, Width * bytes);

            return ReadOnlyPlanarFrame.CreateI420(
                Width, Height, BitDepth, Y, Width * bytes, U, ChromaWidth * bytes, V, ChromaWidth * bytes,
                Alpha ?? default(ReadOnlySpan<byte>), Alpha != null ? Width * bytes : 0);
        }

        internal PlanarFrame AsWritable()
        {
            int bytes = Bytes;
            if (Gray) return PlanarFrame.CreateGray(Width, Height, BitDepth, Y, Width * bytes);

            return PlanarFrame.CreateI420(
                Width, Height, BitDepth, Y, Width * bytes, U, ChromaWidth * bytes, V, ChromaWidth * bytes,
                Alpha ?? default(Span<byte>), Alpha != null ? Width * bytes : 0);
        }

        internal VideoFrameData ToFrame(TimeSpan timestamp, long index)
        {
            int bytes = Bytes;
            VideoPlane y = VideoPlane.FromArray(Y, Width * bytes, Height);
            if (Gray) return VideoFrameData.CreateGray(Width, Height, y, timestamp, index, default, BitDepth);

            return VideoFrameData.CreateI420(
                Width,
                Height,
                y,
                VideoPlane.FromArray(U, ChromaWidth * bytes, ChromaHeight),
                VideoPlane.FromArray(V, ChromaWidth * bytes, ChromaHeight),
                timestamp,
                index,
                Alpha != null ? VideoPlane.FromArray(Alpha, Width * bytes, Height) : default,
                BitDepth);
        }
    }
}
