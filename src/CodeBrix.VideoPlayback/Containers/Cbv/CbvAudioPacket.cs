using System;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>One audio packet for a <see cref="CbvPacketAudioInput" />: its bytes and when it plays.</summary>
public readonly struct CbvAudioPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="data">The packet's bytes. They are written unchanged.</param>
    /// <param name="timestamp">When the packet's first sample plays.</param>
    /// <param name="duration">How long the packet lasts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    public CbvAudioPacket(byte[] data, TimeSpan timestamp, TimeSpan duration)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Timestamp = timestamp;
        Duration = duration;
    }

    /// <summary>The packet's bytes.</summary>
    public byte[] Data { get; }

    /// <summary>When the packet's first sample plays.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How long the packet lasts.</summary>
    public TimeSpan Duration { get; }
}
