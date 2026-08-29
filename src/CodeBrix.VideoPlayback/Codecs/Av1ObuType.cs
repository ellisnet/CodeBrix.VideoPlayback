namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// The kinds of unit an AV1 bitstream is built from, numbered as the AV1 specification numbers them.
/// </summary>
public enum Av1ObuType
{
    /// <summary>Reserved; a stream should not contain one.</summary>
    Reserved0 = 0,

    /// <summary>A sequence header: everything that is true of the whole stream.</summary>
    SequenceHeader = 1,

    /// <summary>A temporal delimiter, which separates one temporal unit from the next. It carries no payload.</summary>
    TemporalDelimiter = 2,

    /// <summary>A frame header on its own.</summary>
    FrameHeader = 3,

    /// <summary>A group of coded tiles.</summary>
    TileGroup = 4,

    /// <summary>Metadata, such as high-dynamic-range mastering information.</summary>
    Metadata = 5,

    /// <summary>A frame header and its tile group together - the usual shape.</summary>
    Frame = 6,

    /// <summary>A redundant copy of a frame header, for error resilience.</summary>
    RedundantFrameHeader = 7,

    /// <summary>A tile list, for large-scale tile decoding.</summary>
    TileList = 8,

    /// <summary>Padding.</summary>
    Padding = 15,
}
