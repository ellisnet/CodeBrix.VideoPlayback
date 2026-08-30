using System;
using CodeBrix.VideoPlayback.Rendering;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.Skia.Composition;

/// <summary>
/// Hands the composition surface to an application that would rather draw over the video with an event
/// handler than with a <see cref="IVideoLayer" />.
/// </summary>
/// <remarks>
/// The event is raised after the video base layer and every registered layer have been drawn, and before the
/// surface is blitted. The canvas's state is saved and restored around the call, so a handler may transform
/// or clip it freely. Neither the canvas nor these arguments outlive the call.
/// </remarks>
public sealed class VideoComposingEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="canvas">The composition surface's canvas.</param>
    /// <param name="context">Where the video is, which frame it is, and what composed it.</param>
    public VideoComposingEventArgs(SKCanvas canvas, VideoCompositionContext context)
    {
        Canvas = canvas;
        Context = context;
    }

    /// <summary>
    /// The composition surface's canvas, in video pixels. Valid only for the duration of the event.
    /// </summary>
    public SKCanvas Canvas { get; }

    /// <summary>Where the video is, which frame it is, and what composed it.</summary>
    public VideoCompositionContext Context { get; }
}
