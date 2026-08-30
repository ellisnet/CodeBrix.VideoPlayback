using System;

namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// Thrown when a request cannot be honoured, or when the machine is missing the one tool authoring needs.
/// </summary>
/// <remarks>
/// It derives from <see cref="VideoPlaybackException" /> so that an application handling failures from this
/// family catches one type. Every message names the piece involved - the setting that is wrong, the file
/// that is missing, the executable that was looked for and where.
/// </remarks>
public class VideoAuthoringException : VideoPlaybackException
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong, naming the piece involved.</param>
    public VideoAuthoringException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the failure underneath it.</summary>
    /// <param name="message">What went wrong, naming the piece involved.</param>
    /// <param name="innerException">The failure this one is explaining.</param>
    public VideoAuthoringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
