using System;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing.Core;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Makes the library behave, for the current flow only, as if the application had registered NO engine - through
/// <c>InProcessAuthoring.NothingRegisteredOverride</c>, never by touching the process-wide registry (which cannot
/// take the ffmpeg engine back out once <see cref="FFmpegEngineRegistration" /> has put it there).
/// </summary>
internal sealed class NothingRegisteredScope : IDisposable
{
    private readonly bool previous;
    private readonly IVideoProcessor previousEngine;

    private NothingRegisteredScope()
    {
        previous = InProcessAuthoring.NothingRegisteredOverride.Value;
        previousEngine = InProcessAuthoring.EngineOverride.Value;
        InProcessAuthoring.NothingRegisteredOverride.Value = true;
        InProcessAuthoring.EngineOverride.Value = null;
    }

    /// <summary>Starts behaving as if nothing were registered.</summary>
    /// <returns>The scope.</returns>
    public static NothingRegisteredScope Begin() => new NothingRegisteredScope();

    /// <summary>Puts the registry's answer back.</summary>
    public void Dispose()
    {
        InProcessAuthoring.NothingRegisteredOverride.Value = previous;
        InProcessAuthoring.EngineOverride.Value = previousEngine;
    }
}
