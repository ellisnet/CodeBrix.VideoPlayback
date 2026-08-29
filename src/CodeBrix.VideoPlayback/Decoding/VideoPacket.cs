using System;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// One compressed video access unit on its way into a decoder: the bytes, when it should be shown, and
/// whether decoding may start from it.
/// </summary>
/// <remarks>
/// <para>
/// This is a value type carrying a <see cref="ReadOnlyMemory{T}" />, so passing one costs nothing and
/// produces no garbage. The memory it points at belongs to the caller and only has to stay valid for the
/// duration of the <see cref="IVideoDecoder.SendPacket" /> call - a decoder that needs to keep the bytes
/// copies them or wraps them itself.
/// </para>
/// </remarks>
public readonly struct VideoPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="data">The compressed bytes of exactly one access unit.</param>
    /// <param name="timestamp">When the frame should be shown, relative to the start of the media.</param>
    /// <param name="isKeyFrame">True when decoding may start from this packet.</param>
    public VideoPacket(ReadOnlyMemory<byte> data, TimeSpan timestamp, bool isKeyFrame)
        : this(data, timestamp, isKeyFrame, TimeSpan.Zero, -1)
    {
    }

    /// <summary>Creates a packet, stating its duration and sequence number as well.</summary>
    /// <param name="data">The compressed bytes of exactly one access unit.</param>
    /// <param name="timestamp">When the frame should be shown, relative to the start of the media.</param>
    /// <param name="isKeyFrame">True when decoding may start from this packet.</param>
    /// <param name="duration">How long the frame is shown for, or <see cref="TimeSpan.Zero" /> when unknown.</param>
    /// <param name="sequenceNumber">
    /// The packet's zero-based position in the track, or -1 when the producer does not count them.
    /// </param>
    public VideoPacket(
        ReadOnlyMemory<byte> data,
        TimeSpan timestamp,
        bool isKeyFrame,
        TimeSpan duration,
        long sequenceNumber)
    {
        Data = data;
        Timestamp = timestamp;
        IsKeyFrame = isKeyFrame;
        Duration = duration;
        SequenceNumber = sequenceNumber;
    }

    /// <summary>The compressed bytes of exactly one access unit.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>When the frame this packet carries should be shown, relative to the start of the media.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How long the frame is shown for; <see cref="TimeSpan.Zero" /> when the container did not say.</summary>
    public TimeSpan Duration { get; }

    /// <summary>True when a decoder may start decoding at this packet without having seen any earlier one.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>The packet's zero-based position in its track, or -1 when the producer does not count them.</summary>
    public long SequenceNumber { get; }

    /// <summary>True when the packet carries no bytes.</summary>
    public bool IsEmpty => Data.IsEmpty;
}
