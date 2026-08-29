using System;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// One audio packet lifted out of an Ogg file, with the timing worked out for it.
/// </summary>
public readonly struct OggAudioPacket
{
    /// <summary>Creates a timed audio packet.</summary>
    /// <param name="data">The codec packet's bytes.</param>
    /// <param name="timestamp">When it starts, counted from the first decoded sample of the stream.</param>
    /// <param name="duration">How much audio it produces.</param>
    /// <param name="sampleCount">How many samples per channel it produces.</param>
    public OggAudioPacket(ReadOnlyMemory<byte> data, TimeSpan timestamp, TimeSpan duration, int sampleCount)
    {
        Data = data;
        Timestamp = timestamp;
        Duration = duration;
        SampleCount = sampleCount;
    }

    /// <summary>The codec packet's bytes, exactly as a container should store them.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// When the packet starts, counted from the stream's first decoded sample - which INCLUDES any codec
    /// priming, exactly as a media container counts it.
    /// </summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How much audio the packet produces.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How many samples per channel the packet produces.</summary>
    public int SampleCount { get; }
}
