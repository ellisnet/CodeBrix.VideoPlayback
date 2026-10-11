using System;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>One audio packet for a <see cref="MatroskaPacketAudioInput" />: its bytes and when it plays.</summary>
public readonly struct MatroskaAudioPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="data">The packet's bytes. They are written unchanged.</param>
    /// <param name="timestamp">
    /// Where the packet's first sample sits on the stream's own timeline. For Opus that timeline INCLUDES the
    /// encoder's pre-skip - the first packet is at zero - exactly as an Ogg granule position counts it; the
    /// muxer states the pre-skip separately, as the track's <c>CodecDelay</c>.
    /// </param>
    /// <param name="duration">How long the packet lasts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    public MatroskaAudioPacket(byte[] data, TimeSpan timestamp, TimeSpan duration)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Timestamp = timestamp;
        Duration = duration;
    }

    /// <summary>The packet's bytes.</summary>
    public byte[] Data { get; }

    /// <summary>Where the packet's first sample sits on the stream's own timeline.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How long the packet lasts.</summary>
    public TimeSpan Duration { get; }
}
