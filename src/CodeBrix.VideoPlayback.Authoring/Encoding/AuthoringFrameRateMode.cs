namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Where a requested frame rate is applied in the one video filter chain.</summary>
/// <remarks>
/// Both modes convert the rate with FFmpeg's <c>fps</c> filter - dropping and duplicating whole frames - so both
/// give the same frames, and the in-process shaping on an engine without FFmpeg gives those same frames too. The
/// mode only decides WHERE in the chain the conversion sits, which matters for what work is spent on a frame that
/// is then thrown away.
/// </remarks>
public enum AuthoringFrameRateMode
{
    /// <summary>
    /// At the output end: an <c>fps=N</c> filter LAST in the chain, after any colour grade and ahead of the final
    /// pixel-format conversion, where FFmpeg's encoder-side rate used to act. The output is constant-frame-rate,
    /// which is what makes a keyframe interval measured in FRAMES mean a fixed number of seconds.
    /// </summary>
    Encoder = 0,

    /// <summary>
    /// As an <c>fps=N</c> filter straight after the scale, inside the one video filter chain. Frames are dropped
    /// or duplicated BEFORE the rest of the chain runs, so a colour grade is not computed for a frame that is
    /// about to be thrown away - which is the reason to prefer this on a chain with a lookup table in it. The
    /// output is constant-frame-rate here too, and holds the same frames as in <see cref="Encoder" /> mode.
    /// </summary>
    Filter = 1,
}
