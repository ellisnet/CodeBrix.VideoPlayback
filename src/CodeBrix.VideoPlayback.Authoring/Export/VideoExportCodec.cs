namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>The picture codec an exported MP4 file is written with.</summary>
public enum VideoExportCodec
{
    /// <summary>H.264 (AVC) - the default; every engine writes it and every player plays it.</summary>
    H264 = 0,

    /// <summary>
    /// HEVC (H.265) - opt-in, and only on an engine that reports
    /// <c>VideoProcessorCapabilities.HevcOutput</c>; any other engine refuses the request, naming itself.
    /// </summary>
    Hevc = 1,
}
