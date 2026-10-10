namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// Which <c>.cbv</c> flavour to write. The first two carry the <c>.cbv</c> extension and the third
/// <c>.cbvmaster</c>; whatever the extension says, the reader sniffs the first four bytes and knows what it has.
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

    /// <summary>
    /// The bespoke <c>CBVF</c> container in its master form (format version 1), written with the
    /// <c>.cbvmaster</c> extension. Marketed as "CodeBrix Video Mode3".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the bespoke flavour keeps, plus two things it cannot carry: the source's ALPHA CHANNEL, as a
    /// second AV1 track holding a monochrome, full-range stream in lock step with the picture (see
    /// <see cref="Encoding.AuthoringVideoSettings.Alpha" />), and LOSSLESS sound - FLAC, the only codec this
    /// flavour takes. Alpha is stored straight, not premultiplied.
    /// </para>
    /// <para>
    /// The picture is encoded exactly as the bespoke flavour encodes it. The alpha plane is encoded by
    /// <c>libaom-av1</c> - the only encoder here that writes a monochrome AV1 stream - with its key frames forced
    /// onto the picture's, and the run checks that the two came out in lock step before muxing. Playing the
    /// sound needs a CodeBrix.Audio.Core whose shared output serves the <c>flac</c> packet codec.
    /// </para>
    /// </remarks>
    Master = 2,
}
