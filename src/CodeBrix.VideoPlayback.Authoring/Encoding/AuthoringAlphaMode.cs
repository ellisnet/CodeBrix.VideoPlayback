namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Whether a master (Mode3) file carries the source's alpha channel as an alpha-plane track.</summary>
/// <remarks>Only <see cref="VideoAuthoringFlavour.Master" /> carries alpha; the other flavours ignore this.</remarks>
public enum AuthoringAlphaMode
{
    /// <summary>
    /// Carry alpha when the source has it - when FFprobe reports a pixel format with an alpha channel
    /// (<c>yuva420p</c>, <c>rgba</c>, <c>gbrap</c> and the like) - and leave it out otherwise. The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always carry an alpha-plane track. A source with no alpha channel then yields a fully opaque plane, which
    /// costs a little space and nothing else.
    /// </summary>
    Include = 1,

    /// <summary>Never carry an alpha-plane track: the file plays like a Mode2 file with lossless sound.</summary>
    Exclude = 2,
}
