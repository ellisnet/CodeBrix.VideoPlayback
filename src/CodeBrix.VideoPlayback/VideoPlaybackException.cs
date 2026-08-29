using System;

namespace CodeBrix.VideoPlayback;

/// <summary>
/// Thrown when something about a media file, a decoder or a playback session cannot be honoured.
/// </summary>
/// <remarks>
/// <para>
/// Every message this library raises names the piece that is missing or wrong, in the words an application
/// developer needs: which codec had no decoder and which package supplies one, which element of a container
/// was malformed, which capability a source lacks. A message that says only "unsupported" is a defect.
/// </para>
/// <para>
/// Failures that happen on a background thread during playback do not throw at the caller - there is nobody
/// there to catch them. They arrive through
/// <see cref="VideoPlaybackSession.MediaFailed" /> carrying an exception of this type instead.
/// </para>
/// </remarks>
public class VideoPlaybackException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong, naming the piece involved.</param>
    public VideoPlaybackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the failure underneath it.</summary>
    /// <param name="message">What went wrong, naming the piece involved.</param>
    /// <param name="innerException">The failure this one is explaining.</param>
    public VideoPlaybackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
