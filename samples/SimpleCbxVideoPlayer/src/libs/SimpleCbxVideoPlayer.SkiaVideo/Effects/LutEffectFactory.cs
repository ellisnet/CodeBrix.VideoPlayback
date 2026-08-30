using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Effects;

namespace SimpleCbxVideoPlayer.SkiaVideo.Effects;

/// <summary>Turns a chain of ".cube" files into the effects the presenter composes.</summary>
public static class LutEffectFactory
{
    /// <summary>Reads every table in a chain and wraps it as an effect.</summary>
    /// <param name="entries">The chain, in the order the tables are applied.</param>
    /// <param name="failures">
    /// The files that could not be read, each with the reason; empty when everything read.
    /// </param>
    /// <returns>The effects, in the same order as the chain. A table at 0 per cent is still included -
    /// the presenter skips it when it composes, and keeping it makes the order visible.</returns>
    /// <remarks>
    /// A file that will not parse is REPORTED rather than thrown: one broken table in a list of twenty
    /// should grey out its own row, not stop the video.
    /// </remarks>
    public static IReadOnlyList<IVideoFrameEffect> Build(
        IReadOnlyList<LutChainEntry> entries,
        out IReadOnlyList<string> failures)
    {
        List<IVideoFrameEffect> effects = [];
        List<string> problems = [];

        if (entries != null)
        {
            foreach (var entry in entries)
            {
                if (entry == null) { continue; }

                try
                {
                    effects.Add(LutEffect.FromCubeFile(entry.FilePath, entry.ApplyAtPercent));
                }
                catch (Exception exception) when (exception is IOException or FormatException or InvalidDataException
                                                      or UnauthorizedAccessException or ArgumentException)
                {
                    problems.Add($"{entry.FileName}: {exception.Message}");
                }
            }
        }

        failures = problems;
        return effects;
    }
}
