using System;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// One unit of compressed data as it comes out of a container reader: which track it belongs to, its bytes,
/// and when it is for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The memory is borrowed.</b> <see cref="Data" /> points into the reader's own buffer and stays valid only
/// until the next call that reads from the same reader. A consumer that keeps a packet - putting it in a
/// queue for another thread, for instance - must copy the bytes first.
/// </para>
/// </remarks>
public readonly struct MediaPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="trackId">The identifier of the track the packet belongs to.</param>
    /// <param name="data">The compressed bytes, borrowed from the reader.</param>
    /// <param name="timestamp">When the packet is for, relative to the start of the media.</param>
    /// <param name="duration">How long it lasts, or <see cref="TimeSpan.Zero" /> when the container did not say.</param>
    /// <param name="isKeyFrame">True when decoding may start here.</param>
    /// <param name="discardPadding">
    /// How much of the END of this packet's decoded audio should be thrown away - Matroska's
    /// <c>DiscardPadding</c>. Zero for everything else.
    /// </param>
    public MediaPacket(
        int trackId,
        ReadOnlyMemory<byte> data,
        TimeSpan timestamp,
        TimeSpan duration,
        bool isKeyFrame,
        TimeSpan discardPadding = default)
    {
        TrackId = trackId;
        Data = data;
        Timestamp = timestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
        DiscardPadding = discardPadding;
    }

    /// <summary>The identifier of the track the packet belongs to.</summary>
    public int TrackId { get; }

    /// <summary>The compressed bytes. Borrowed from the reader - copy them to keep them.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>When the packet is for, relative to the start of the media.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How long it lasts, or <see cref="TimeSpan.Zero" /> when the container did not say.</summary>
    public TimeSpan Duration { get; }

    /// <summary>True when decoding may start at this packet without having seen any earlier one.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// How much of the end of this packet's decoded audio should be thrown away, which is how a container
    /// trims an encoder's padding off the last packet of a track.
    /// </summary>
    public TimeSpan DiscardPadding { get; }

    /// <summary>True when the packet carries no bytes.</summary>
    public bool IsEmpty => Data.IsEmpty;
}
