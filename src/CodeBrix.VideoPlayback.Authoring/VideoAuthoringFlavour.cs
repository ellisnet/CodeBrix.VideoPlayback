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
    /// <para>
    /// This flavour keeps per-language chapter titles, whole caption tracks in the header region, and the
    /// index in front of the media data by construction.
    /// </para>
    /// <para>
    /// It can also be encoded IN PROCESS - picture by the in-process AV1 encoder, sound by the in-process
    /// Vorbis encoder, from frames and samples the registered video-processing engine decodes: always with
    /// <see cref="Encoding.AuthoringVideoEncoder.Av1Package" />, and as the fallback, with a note, when an
    /// FFmpeg encoder was asked for and the ffmpeg path is not available.
    /// </para>
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
    /// This flavour encodes IN PROCESS rather than through FFmpeg command lines: the source is decoded through
    /// the registered video-processing engine - any engine, Android's included; the desktop ffmpeg engine when
    /// none is registered - and the picture, the alpha plane and the sound are encoded by in-process AV1 and
    /// FLAC encoders. The alpha plane's key frames are forced onto the picture's, and the run checks that the two
    /// came out in lock step before muxing. Fully transparent pixels take the colour of the nearest visible one
    /// (<see cref="Encoding.AuthoringVideoSettings.FillTransparentPixels" />). Scaling, a frame rate, a colour
    /// grade and turning a rotated source upright run as an FFmpeg prepare pass on the ffmpeg engine and in
    /// process, through the playback core's managed processing primitives, on any other. The picture may be
    /// 10-bit (<see cref="Encoding.AuthoringVideoSettings.BitDepth" />). Playing the sound needs a
    /// CodeBrix.Audio.Core whose shared output serves the <c>flac</c> packet codec.
    /// </para>
    /// </remarks>
    Master = 2,
}
