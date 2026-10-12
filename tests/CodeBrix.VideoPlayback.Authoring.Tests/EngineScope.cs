using System;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoProcessing.Core;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Hands the library an engine for the current flow only, through <c>InProcessAuthoring.EngineOverride</c>, and
/// takes it back when disposed - never a process-wide registration, which could not be undone.
/// </summary>
internal sealed class EngineScope : IDisposable
{
    private readonly IVideoProcessor previous;

    private EngineScope(IVideoProcessor engine)
    {
        previous = InProcessAuthoring.EngineOverride.Value;
        InProcessAuthoring.EngineOverride.Value = engine;
    }

    /// <summary>Uses an engine until the scope is disposed.</summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The scope.</returns>
    public static EngineScope Use(IVideoProcessor engine) => new EngineScope(engine);

    /// <summary>Puts the previous engine back.</summary>
    public void Dispose() => InProcessAuthoring.EngineOverride.Value = previous;
}
