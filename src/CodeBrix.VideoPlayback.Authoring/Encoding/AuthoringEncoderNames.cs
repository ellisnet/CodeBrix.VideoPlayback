namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>
/// The FFmpeg encoder names this library asks for, as they appear on the command line.
/// </summary>
/// <remarks>
/// They are constants rather than looked-up catalogue objects on purpose: naming an encoder as a string is
/// what lets a command line be RENDERED on a machine that has no FFmpeg installed at all, which is what the
/// dry run and the argument tests need.
/// </remarks>
public static class AuthoringEncoderNames
{
    /// <summary>SVT-AV1, the default AV1 encoder.</summary>
    public const string LibSvtAv1 = "libsvtav1";

    /// <summary>libaom, the reference AV1 encoder.</summary>
    public const string LibAomAv1 = "libaom-av1";

    /// <summary>The Opus encoder.</summary>
    public const string LibOpus = "libopus";

    /// <summary>
    /// The Vorbis encoder. Note the <c>lib</c>: FFmpeg's own built-in <c>vorbis</c> encoder is experimental
    /// and poor, and this library never names it.
    /// </summary>
    public const string LibVorbis = "libvorbis";

    /// <summary>
    /// The subtitle "encoder" caption tracks are given: a straight copy. FFmpeg's <c>webvtt</c> ENCODER
    /// discards cue identifiers and positioning settings, so a caption track is never re-encoded here.
    /// </summary>
    public const string SubtitleCopy = "copy";
}
