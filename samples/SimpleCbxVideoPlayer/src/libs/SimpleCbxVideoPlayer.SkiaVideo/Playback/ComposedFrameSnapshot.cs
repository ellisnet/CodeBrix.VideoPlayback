using System;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>What was written when the composed picture was captured, and what it looked like.</summary>
/// <remarks>
/// The measurements exist so that an unattended run can ASSERT something about the picture without a
/// person looking at it: that it is not black, that a portrait file came out portrait, and that two runs
/// of the same file at the same moment produced the same pixels (or, with a lookup table applied,
/// deliberately different ones).
/// </remarks>
public sealed class ComposedFrameSnapshot
{
    /// <summary>Creates the record.</summary>
    /// <param name="filePath">Where the PNG was written.</param>
    /// <param name="width">The composed picture's width in pixels.</param>
    /// <param name="height">The composed picture's height in pixels.</param>
    /// <param name="nonBlackPercent">How much of the picture is not black, 0 to 100.</param>
    /// <param name="meanLuminance">The picture's mean luminance, 0 to 255.</param>
    /// <param name="sha256">The SHA-256 of the PNG bytes, in lower-case hexadecimal.</param>
    /// <param name="frameNumber">The frame number that was showing.</param>
    /// <param name="timestamp">The timestamp of the frame that was showing.</param>
    public ComposedFrameSnapshot(
        string filePath,
        int width,
        int height,
        double nonBlackPercent,
        double meanLuminance,
        string sha256,
        long frameNumber,
        TimeSpan timestamp)
    {
        FilePath = filePath;
        Width = width;
        Height = height;
        NonBlackPercent = nonBlackPercent;
        MeanLuminance = meanLuminance;
        Sha256 = sha256;
        FrameNumber = frameNumber;
        Timestamp = timestamp;
    }

    /// <summary>Where the PNG was written.</summary>
    public string FilePath { get; }

    /// <summary>The composed picture's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The composed picture's height in pixels.</summary>
    public int Height { get; }

    /// <summary>How much of the picture is not black, 0 to 100.</summary>
    public double NonBlackPercent { get; }

    /// <summary>The picture's mean luminance, 0 to 255.</summary>
    public double MeanLuminance { get; }

    /// <summary>The SHA-256 of the PNG bytes, in lower-case hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>The frame number that was showing.</summary>
    public long FrameNumber { get; }

    /// <summary>The timestamp of the frame that was showing.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>True when the picture is portrait - taller than it is wide.</summary>
    public bool IsPortrait => Height > Width;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Width}x{Height} frame {FrameNumber} at {Timestamp:mm\\:ss\\.fff}, "
        + $"{NonBlackPercent:0.0}% not black, mean luminance {MeanLuminance:0.0}, sha256 {Sha256}";
}
