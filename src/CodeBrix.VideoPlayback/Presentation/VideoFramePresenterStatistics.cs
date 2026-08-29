namespace CodeBrix.VideoPlayback.Presentation;

/// <summary>
/// A snapshot of what a <see cref="VideoFramePresenter" /> has been doing - how many frames reached the
/// screen and how many did not.
/// </summary>
/// <remarks>
/// <para>
/// The two failure counts mean different things and are worth keeping apart. <see cref="Superseded" /> is
/// the display falling behind the video: a newer frame arrived before the old one was collected, which is
/// normal and correct at, say, 60 frames per second on a 30 Hz refresh. <see cref="Late" /> is the DECODER
/// falling behind: a frame whose moment had already passed when it was produced, which the session drops
/// rather than showing at the wrong time.
/// </para>
/// </remarks>
public readonly struct VideoFramePresenterStatistics
{
    /// <summary>Creates a statistics snapshot.</summary>
    /// <param name="posted">How many frames were posted to the presenter.</param>
    /// <param name="presented">How many frames were collected for display.</param>
    /// <param name="superseded">How many frames were replaced before they were collected.</param>
    /// <param name="late">How many frames were dropped for arriving after their moment.</param>
    public VideoFramePresenterStatistics(long posted, long presented, long superseded, long late)
    {
        Posted = posted;
        Presented = presented;
        Superseded = superseded;
        Late = late;
    }

    /// <summary>How many frames were posted to the presenter.</summary>
    public long Posted { get; }

    /// <summary>How many frames were collected for display.</summary>
    public long Presented { get; }

    /// <summary>How many frames were replaced by a newer one before anybody collected them.</summary>
    public long Superseded { get; }

    /// <summary>How many frames the session dropped because their moment had already passed.</summary>
    public long Late { get; }

    /// <summary>How many frames did not reach the screen, for either reason.</summary>
    public long Dropped => Superseded + Late;

    /// <inheritdoc />
    public override string ToString() =>
        $"posted {Posted}, presented {Presented}, superseded {Superseded}, late {Late}";
}
