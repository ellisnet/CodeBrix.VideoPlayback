namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Which AV1 encoder writes the picture: one FFmpeg is asked for, or the in-process encoder package.</summary>
/// <remarks>
/// All three write the same bitstream format; they differ in how long they take to write it and what the
/// speed knob is called. Nothing else in this family cares which one made a file.
/// </remarks>
public enum AuthoringVideoEncoder
{
    /// <summary>
    /// SVT-AV1 (<c>libsvtav1</c>) - the default. Its speed knob is <c>-preset</c>, 0 (slowest, best) to 13
    /// (fastest, worst), and it scales across cores, which is what keeps a corpus regeneration to minutes.
    /// </summary>
    LibSvtAv1 = 0,

    /// <summary>
    /// libaom (<c>libaom-av1</c>) - the reference encoder. Its speed knob is <c>-cpu-used</c>, 0 (slowest)
    /// to 8 (fastest), and it also needs <c>-b:v 0</c> for the rate factor to mean anything.
    /// </summary>
    LibAomAv1 = 1,

    /// <summary>
    /// The in-process AV1 encoder package (libaom inside the package, no FFmpeg encoder involved). The bespoke
    /// flavour then decodes its source through the registered video-processing engine and encodes the picture
    /// and its Vorbis sound in process; the WebM-profile flavour encodes the picture and its Opus sound in
    /// process and writes the file with the playback library's managed WebM muxer. Its speed knob is libaom's
    /// cpu-used - <see cref="AuthoringVideoSettings.SpeedPreset" /> capped at 8 - and its rate factor is
    /// libaom's cq-level, the same 0 to 63 scale. Slower than SVT-AV1 on a desktop. The master flavour always
    /// encodes this way.
    /// </summary>
    Av1Package = 2,
}
