namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// What <see cref="CbvAuthoring.Write" /> produced.
/// </summary>
public sealed class CbvAuthoringResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="path">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="videoTrackId">The video track's identifier, or 0 when there is no video.</param>
    /// <param name="audioTrackId">The audio track's identifier, or 0 when there is no audio.</param>
    /// <param name="videoFrameCount">How many video frames were written.</param>
    /// <param name="audioPacketCount">How many audio packets were written.</param>
    /// <param name="captionTrackCount">How many caption tracks were written.</param>
    /// <param name="captionCueCount">How many caption cues were written across all tracks.</param>
    public CbvAuthoringResult(
        string path,
        long sizeInBytes,
        int videoTrackId,
        int audioTrackId,
        int videoFrameCount,
        int audioPacketCount,
        int captionTrackCount,
        int captionCueCount)
    {
        Path = path;
        SizeInBytes = sizeInBytes;
        VideoTrackId = videoTrackId;
        AudioTrackId = audioTrackId;
        VideoFrameCount = videoFrameCount;
        AudioPacketCount = audioPacketCount;
        CaptionTrackCount = captionTrackCount;
        CaptionCueCount = captionCueCount;
    }

    /// <summary>Creates a result for a file that may carry an alpha-plane track.</summary>
    /// <param name="path">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="videoTrackId">The picture track's identifier, or 0.</param>
    /// <param name="alphaTrackId">The alpha-plane track's identifier, or 0.</param>
    /// <param name="audioTrackId">The audio track's identifier, or 0.</param>
    /// <param name="videoFrameCount">How many picture frames were written.</param>
    /// <param name="alphaFrameCount">How many alpha-plane frames were written.</param>
    /// <param name="audioPacketCount">How many audio packets were written.</param>
    /// <param name="captionTrackCount">How many caption tracks were written.</param>
    /// <param name="captionCueCount">How many caption cues were written.</param>
    /// <param name="formatVersion">The container format version the file was stamped with.</param>
    public CbvAuthoringResult(
        string path,
        long sizeInBytes,
        int videoTrackId,
        int alphaTrackId,
        int audioTrackId,
        int videoFrameCount,
        int alphaFrameCount,
        int audioPacketCount,
        int captionTrackCount,
        int captionCueCount,
        int formatVersion)
        : this(
            path,
            sizeInBytes,
            videoTrackId,
            audioTrackId,
            videoFrameCount,
            audioPacketCount,
            captionTrackCount,
            captionCueCount)
    {
        AlphaTrackId = alphaTrackId;
        AlphaFrameCount = alphaFrameCount;
        FormatVersion = formatVersion;
    }

    /// <summary>Where the file was written.</summary>
    public string Path { get; }

    /// <summary>How big the finished file is, in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>The video track's identifier, or 0 when the file has no video.</summary>
    public int VideoTrackId { get; }

    /// <summary>The audio track's identifier, or 0 when the file has no audio.</summary>
    public int AudioTrackId { get; }

    /// <summary>How many video frames were written.</summary>
    public int VideoFrameCount { get; }

    /// <summary>The alpha-plane track's identifier, or 0 when the file has no alpha.</summary>
    public int AlphaTrackId { get; }

    /// <summary>How many alpha-plane frames were written; 0 when the file has no alpha.</summary>
    public int AlphaFrameCount { get; }

    /// <summary>The container format version the file was stamped with: 0, or 1 for a master (Mode3) file.</summary>
    public int FormatVersion { get; }

    /// <summary>How many audio packets were written.</summary>
    public int AudioPacketCount { get; }

    /// <summary>How many caption tracks were written.</summary>
    public int CaptionTrackCount { get; }

    /// <summary>How many caption cues were written across all tracks.</summary>
    public int CaptionCueCount { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Path}: {SizeInBytes:N0} bytes, {VideoFrameCount} video frames, "
        + (AlphaTrackId > 0 ? $"{AlphaFrameCount} alpha frames, " : string.Empty)
        + $"{AudioPacketCount} audio packets, "
        + $"{CaptionTrackCount} caption track(s) with {CaptionCueCount} cues";
}
