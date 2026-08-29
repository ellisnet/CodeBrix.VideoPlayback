namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// Where a frame buffer's samples actually live.
/// </summary>
public enum VideoFrameStorage
{
    /// <summary>Ordinary process memory, pinned so a native decoder can write into it directly.</summary>
    HostMemory = 0,

    /// <summary>
    /// A graphics-device texture or a persistently-mapped upload buffer. Reserved: no pool in this package
    /// produces one yet, and the plane pointers of such a buffer are only valid while it is mapped.
    /// </summary>
    GpuTexture = 1,
}
