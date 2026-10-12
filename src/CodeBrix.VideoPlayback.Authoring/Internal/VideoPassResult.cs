using System.Collections.Generic;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core.Frames;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>What the video pass of a master file wrote.</summary>
internal sealed class VideoPassResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="info">What the frame source said about the picture.</param>
    /// <param name="width">The width encoded - the decoded width, cropped to even.</param>
    /// <param name="height">The height encoded - the decoded height, cropped to even.</param>
    /// <param name="keyFrames">Whether each frame is a key frame, in frame order.</param>
    /// <param name="filled">Whether the transparent-pixel fill ran.</param>
    /// <param name="filledFrames">How many frames it changed.</param>
    internal VideoPassResult(
        VideoSourceInfo info,
        int width,
        int height,
        IReadOnlyList<bool> keyFrames,
        bool filled,
        int filledFrames)
    {
        Info = info;
        Width = width;
        Height = height;
        KeyFrames = keyFrames;
        Filled = filled;
        FilledFrames = filledFrames;
    }

    /// <summary>What the frame source said about the picture: size, rate, alpha.</summary>
    internal VideoSourceInfo Info { get; }

    /// <summary>The width encoded - the decoded width, cropped to even.</summary>
    internal int Width { get; }

    /// <summary>The height encoded - the decoded height, cropped to even.</summary>
    internal int Height { get; }

    /// <summary>Whether each frame is a key frame, in frame order.</summary>
    internal IReadOnlyList<bool> KeyFrames { get; }

    /// <summary>Whether the transparent-pixel fill ran.</summary>
    internal bool Filled { get; }

    /// <summary>How many frames it changed.</summary>
    internal int FilledFrames { get; }

    /// <summary>
    /// The in-process shaping the picture was decoded through, or null - the alpha pass puts the same shaping
    /// round its own decode so the two stay in lock step.
    /// </summary>
    internal ManagedFrameShaping Shaping { get; init; }

    /// <summary>The turn the in-process shaping applied; none when there was no shaping.</summary>
    internal RotationAngle Rotation { get; init; }

    /// <summary>The width after the turn and before any scaling.</summary>
    internal int TurnedWidth { get; init; }

    /// <summary>The height after the turn and before any scaling.</summary>
    internal int TurnedHeight { get; init; }

    /// <summary>True when the engine handed over 8-bit frames for a 10-bit master and they were widened here.</summary>
    internal bool Widened { get; init; }
}
