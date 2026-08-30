namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Where a requested frame rate is applied in the FFmpeg command line.</summary>
public enum AuthoringFrameRateMode
{
    /// <summary>
    /// As <c>-r N</c>, the encoder-level output rate. This is what makes the output constant-frame-rate,
    /// and therefore what makes a keyframe interval measured in FRAMES mean a fixed number of seconds.
    /// </summary>
    Encoder = 0,

    /// <summary>
    /// As an <c>fps=N</c> filter inside the one video filter chain. Frames are dropped or duplicated BEFORE
    /// the rest of the chain runs, so a colour grade is not computed for a frame that is about to be thrown
    /// away - which is the reason to prefer this on a chain with a lookup table in it.
    /// </summary>
    Filter = 1,
}
