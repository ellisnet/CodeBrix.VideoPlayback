namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// Which of the two <c>.cbv</c> flavours to write. Both carry the same extension; the reader sniffs the
/// first four bytes and knows which it has.
/// </summary>
public enum VideoAuthoringFlavour
{
    /// <summary>
    /// A constrained WebM file, written by FFmpeg in ONE pass with its seek index moved to the front.
    /// Marketed as "CodeBrix Video Mode1"; any tool that reads WebM reads it unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWO THINGS DO NOT SURVIVE THIS FLAVOUR, and both are reported in the result's notes rather than
    /// hidden. Chapter titles are single-language: FFmpeg's Matroska muxer writes one
    /// <c>ChapterDisplay</c> per chapter and gives it no language, so per-language titles in the chapter file
    /// are collapsed to the untagged one. And a caption track's hearing-impaired flag is lost: a WebM
    /// document has no element for it, so only the default and forced flags are written.
    /// </para>
    /// <para>The bespoke flavour keeps both.</para>
    /// </remarks>
    WebMProfile = 0,

    /// <summary>
    /// The bespoke <c>CBVF</c> container, written by CodeBrix.VideoPlayback's own muxer from FFmpeg's IVF
    /// video and Ogg audio output. Marketed as "CodeBrix Video Mode2".
    /// </summary>
    /// <remarks>
    /// This flavour keeps per-language chapter titles, whole caption tracks in the header region, and the
    /// index in front of the media data by construction.
    /// </remarks>
    Bespoke = 1,
}
