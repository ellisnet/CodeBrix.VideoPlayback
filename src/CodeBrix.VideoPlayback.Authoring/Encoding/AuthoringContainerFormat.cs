namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>
/// Which Matroska-family muxer the WebM-profile pass is asked for. Ignored by the bespoke flavour, whose
/// container is written by managed code.
/// </summary>
/// <remarks>
/// The output's EXTENSION never decides this: a WebM-profile <c>.cbv</c> file is a WebM file, and the muxer
/// is always named on the command line.
/// </remarks>
public enum AuthoringContainerFormat
{
    /// <summary>
    /// <c>-f webm</c> - the WebM document type, which is what the streamable profile is defined over.
    /// </summary>
    WebM = 0,

    /// <summary>
    /// <c>-f matroska</c> - the full Matroska document type. A file written this way is an ordinary
    /// <c>.mkv</c>; it is what a corpus wants as a NEGATIVE control, not what a <c>.cbv</c> is.
    /// </summary>
    Matroska = 1,
}
