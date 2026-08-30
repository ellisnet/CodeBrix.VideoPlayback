using System;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// Everything a layer needs to know about the frame it is being drawn over: where the video sits, which frame
/// it is, and what is drawing it.
/// </summary>
/// <remarks>
/// <para>
/// The coordinates are those of the OFF-SCREEN composition surface, which is the frame's coded pixel size.
/// The surface is later blitted into whatever rectangle the application asked for, so a layer draws once, in
/// video pixels, and is scaled and letterboxed along with the picture it is drawn on.
/// </para>
/// <para>
/// The rectangle is a <see cref="VideoRectangle" /> and not any drawing library's own type, because this
/// description travels from a presenter to a layer through packages that have no drawing dependency. A
/// presenter converts it to whatever its canvas speaks at the edge.
/// </para>
/// <para>
/// <see cref="DisplayWidth" /> and <see cref="DisplayHeight" /> are the size the frame will be SHOWN at once
/// its pixel aspect ratio has been applied; they differ from <see cref="FrameWidth" /> and
/// <see cref="FrameHeight" /> only for anamorphic content. A layer that cares about the final shape - one
/// drawing a circle, say - should scale by their ratio.
/// </para>
/// </remarks>
public readonly struct VideoCompositionContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="videoRect">Where the video sits on the composition surface.</param>
    /// <param name="frameWidth">The frame's coded width in pixels.</param>
    /// <param name="frameHeight">The frame's coded height in pixels.</param>
    /// <param name="displayWidth">The width the frame should be shown at.</param>
    /// <param name="displayHeight">The height the frame should be shown at.</param>
    /// <param name="timestamp">When the frame should be shown, relative to the start of the media.</param>
    /// <param name="frameNumber">The frame's number, or -1 when its producer does not count them.</param>
    /// <param name="backend">The render path that composed the frame.</param>
    /// <param name="effectsActive">True when the effect chain was applied to this frame.</param>
    public VideoCompositionContext(
        VideoRectangle videoRect,
        int frameWidth,
        int frameHeight,
        int displayWidth,
        int displayHeight,
        TimeSpan timestamp,
        long frameNumber,
        VideoRenderBackend backend,
        bool effectsActive)
    {
        VideoRect = videoRect;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        Timestamp = timestamp;
        FrameNumber = frameNumber;
        Backend = backend;
        EffectsActive = effectsActive;
    }

    /// <summary>
    /// Where the video sits on the composition surface. It starts at the origin and covers the whole surface,
    /// which is the frame's coded size.
    /// </summary>
    public VideoRectangle VideoRect { get; }

    /// <summary>The frame's coded width in pixels.</summary>
    public int FrameWidth { get; }

    /// <summary>The frame's coded height in pixels.</summary>
    public int FrameHeight { get; }

    /// <summary>The width the frame should be shown at once its pixel aspect ratio has been applied.</summary>
    public int DisplayWidth { get; }

    /// <summary>The height the frame should be shown at once its pixel aspect ratio has been applied.</summary>
    public int DisplayHeight { get; }

    /// <summary>When the frame should be shown, relative to the start of the media.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>The frame's zero-based number, or -1 when its producer does not count them.</summary>
    public long FrameNumber { get; }

    /// <summary>The render path that composed the frame under this layer.</summary>
    public VideoRenderBackend Backend { get; }

    /// <summary>True when the presenter's effect chain was applied to this frame.</summary>
    public bool EffectsActive { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"frame {FrameNumber} at {Timestamp}, {FrameWidth}x{FrameHeight} on {Backend}"
        + (EffectsActive ? " with effects" : string.Empty);
}
