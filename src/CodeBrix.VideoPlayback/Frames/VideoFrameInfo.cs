using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// Everything about a decoded frame except the samples themselves - handed to
/// <see cref="VideoFrame.Create" /> when a decoder publishes a frame.
/// </summary>
/// <remarks>
/// Passing the description as one value type keeps <see cref="VideoFrame.Create" /> to a single argument and
/// makes it obvious that the whole description is fixed at the moment of creation: a frame is immutable
/// once the decoder has handed it over.
/// </remarks>
public readonly struct VideoFrameInfo
{
    /// <summary>Creates a frame description.</summary>
    /// <param name="width">The visible width in luma samples.</param>
    /// <param name="height">The visible height in luma samples.</param>
    /// <param name="displayWidth">The width to show the frame at after pixel-aspect correction.</param>
    /// <param name="displayHeight">The height to show the frame at after pixel-aspect correction.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="timestamp">When the frame should be shown, relative to the start of the media.</param>
    /// <param name="presentationTimestamp">The raw presentation timestamp in the producer's own units.</param>
    /// <param name="frameNumber">The frame's zero-based decode order number, or -1 when unknown.</param>
    /// <param name="isKeyFrame">True when the frame was decoded from a key frame.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="hdr">Mastering metadata, or null when the stream carried none.</param>
    public VideoFrameInfo(
        int width,
        int height,
        int displayWidth,
        int displayHeight,
        VideoPixelLayout layout,
        int bitDepth,
        TimeSpan timestamp,
        long presentationTimestamp,
        long frameNumber,
        bool isKeyFrame,
        VideoColorInfo color,
        HdrMetadata hdr)
    {
        Width = width;
        Height = height;
        DisplayWidth = displayWidth > 0 ? displayWidth : width;
        DisplayHeight = displayHeight > 0 ? displayHeight : height;
        Layout = layout;
        BitDepth = bitDepth;
        Timestamp = timestamp;
        PresentationTimestamp = presentationTimestamp;
        FrameNumber = frameNumber;
        IsKeyFrame = isKeyFrame;
        Color = color;
        Hdr = hdr;
    }

    /// <summary>The visible width in luma samples.</summary>
    public int Width { get; }

    /// <summary>The visible height in luma samples.</summary>
    public int Height { get; }

    /// <summary>The width to show the frame at once the pixel aspect ratio has been applied.</summary>
    public int DisplayWidth { get; }

    /// <summary>The height to show the frame at once the pixel aspect ratio has been applied.</summary>
    public int DisplayHeight { get; }

    /// <summary>The plane layout and chroma subsampling.</summary>
    public VideoPixelLayout Layout { get; }

    /// <summary>Bits per sample: 8, 10 or 12.</summary>
    public int BitDepth { get; }

    /// <summary>When the frame should be shown, relative to the start of the media.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>
    /// The raw presentation timestamp in whatever units the producer counts in. Every container reader in
    /// this package counts in 100-nanosecond ticks, so it equals <c>Timestamp.Ticks</c> for frames this
    /// package produced.
    /// </summary>
    public long PresentationTimestamp { get; }

    /// <summary>The frame's zero-based number in decode order, or -1 when the producer does not count them.</summary>
    public long FrameNumber { get; }

    /// <summary>True when the frame was decoded from a key frame - a point playback can start from.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>The colour description: primaries, transfer, matrix, range and chroma siting.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>Mastering metadata for high-dynamic-range content, or null when the stream carried none.</summary>
    public HdrMetadata Hdr { get; }
}
