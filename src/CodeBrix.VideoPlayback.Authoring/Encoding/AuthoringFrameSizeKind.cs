namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>How an <see cref="AuthoringFrameSize" /> states the size it wants.</summary>
public enum AuthoringFrameSizeKind
{
    /// <summary>Whatever the source is. No scale filter is emitted at all.</summary>
    Source = 0,

    /// <summary>An exact width and height, in pixels.</summary>
    Exact = 1,

    /// <summary>The LONGER side of the frame, with the aspect ratio kept and the other side made even.</summary>
    LongSide = 2,

    /// <summary>The SHORTER side of the frame, with the aspect ratio kept and the other side made even.</summary>
    ShortSide = 3,
}
