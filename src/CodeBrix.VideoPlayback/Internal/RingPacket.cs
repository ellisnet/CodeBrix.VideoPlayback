using System;

namespace CodeBrix.VideoPlayback.Internal;

/// <summary>
/// A packet handed out of a <see cref="PacketRing" />. Its memory belongs to the ring and stays valid until
/// <see cref="PacketRing.EndRead" /> is called.
/// </summary>
internal readonly struct RingPacket
{
    internal RingPacket(
        ReadOnlyMemory<byte> data,
        TimeSpan timestamp,
        TimeSpan duration,
        bool isKeyFrame,
        TimeSpan discardPadding,
        int generation)
    {
        Data = data;
        Timestamp = timestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
        DiscardPadding = discardPadding;
        Generation = generation;
    }

    internal ReadOnlyMemory<byte> Data { get; }

    internal TimeSpan Timestamp { get; }

    internal TimeSpan Duration { get; }

    internal bool IsKeyFrame { get; }

    internal TimeSpan DiscardPadding { get; }

    internal int Generation { get; }
}
