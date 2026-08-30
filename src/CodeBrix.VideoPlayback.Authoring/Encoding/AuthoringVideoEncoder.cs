namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>Which AV1 encoder FFmpeg is asked for.</summary>
/// <remarks>
/// Both write the same bitstream format; they differ in how long they take to write it and what the speed
/// knob is called. Nothing else in this family cares which one made a file.
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
}
