namespace CodeBrix.VideoPlayback.Playback;

/// <summary>
/// How precisely a seek should land.
/// </summary>
public enum VideoSeekMode
{
    /// <summary>
    /// Land on the requested moment. The reader positions itself at the key frame before it and the decoder
    /// works forward, throwing frames away until the target is reached. The default, and what a scrubber
    /// wants.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Land on the key frame at or before the requested moment and start there. Nothing is decoded and
    /// discarded, so it is instant - which is what a slow device, or a fast scrub through a long file, wants.
    /// </summary>
    KeyFrameOnly = 1,
}
