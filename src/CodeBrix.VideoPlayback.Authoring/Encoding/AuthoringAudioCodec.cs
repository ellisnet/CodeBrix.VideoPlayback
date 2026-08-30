namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Which audio encoder FFmpeg is asked for.</summary>
/// <remarks>
/// <para>
/// Both containers carry either codec, and the choice has one consequence for the APPLICATION that plays the
/// file: Vorbis plays with the core playback package alone, while Opus needs the application to reference
/// CodeBrix.Audio.Opus and call its <c>Register()</c>. That is why the two flavours default differently.
/// </para>
/// <para>
/// FFmpeg's BUILT-IN <c>vorbis</c> encoder is never used. It is experimental and poor; <c>libvorbis</c> is
/// the one this library names.
/// </para>
/// </remarks>
public enum AuthoringAudioCodec
{
    /// <summary>
    /// Let the flavour choose: <see cref="LibOpus" /> for a WebM-profile file, <see cref="LibVorbis" /> for
    /// a bespoke one.
    /// </summary>
    Default = 0,

    /// <summary>Opus (<c>libopus</c>), rate-controlled by a bit rate.</summary>
    LibOpus = 1,

    /// <summary>
    /// Vorbis (<c>libvorbis</c>), rate-controlled by a bit rate or by a quality number. The convention for
    /// the bespoke flavour, so that playing one of its files never needs the Opus package.
    /// </summary>
    LibVorbis = 2,
}
