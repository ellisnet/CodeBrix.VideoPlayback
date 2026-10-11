using System.Collections.Generic;
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
}
