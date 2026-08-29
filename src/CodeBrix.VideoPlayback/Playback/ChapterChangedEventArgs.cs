using System;
using CodeBrix.VideoPlayback.Chapters;

namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// Says that playback has crossed into a different chapter.
/// </summary>
public sealed class ChapterChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="chapter">The chapter playback is now inside, or null when it is not inside one.</param>
    /// <param name="previousChapter">The chapter playback has just left, or null.</param>
    public ChapterChangedEventArgs(Chapter chapter, Chapter previousChapter)
    {
        Chapter = chapter;
        PreviousChapter = previousChapter;
    }

    /// <summary>The chapter playback is now inside, or null when it is not inside one.</summary>
    public Chapter Chapter { get; }

    /// <summary>The chapter playback has just left, or null.</summary>
    public Chapter PreviousChapter { get; }
}
