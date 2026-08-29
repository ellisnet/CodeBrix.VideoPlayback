using System;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// Works out how much audio an Opus packet carries, from the one byte at the front of it.
/// </summary>
/// <remarks>
/// Every Opus packet begins with a table-of-contents byte that states the coding mode, the frame size and how
/// many frames follow. That is enough to know a packet's duration exactly without decoding it, which is what
/// lets a muxer give every packet a correct timestamp.
/// </remarks>
public static class OpusPacketDuration
{
    /// <summary>Opus always decodes at this rate, whatever the source material was sampled at.</summary>
    public const int SampleRate = 48000;

    /// <summary>Counts the samples per channel an Opus packet produces.</summary>
    /// <param name="packet">The packet's bytes.</param>
    /// <returns>
    /// The number of samples per channel at 48 kHz, or 0 when the packet is empty or its frame count is
    /// invalid.
    /// </returns>
    public static int GetSampleCount(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1) return 0;

        byte toc = packet[0];
        int config = toc >> 3;
        int code = toc & 0x03;

        int frameSamples;
        if (config < 12)
        {
            frameSamples = (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 };
        }
        else if (config < 16)
        {
            frameSamples = (config & 1) == 0 ? 480 : 960;
        }
        else
        {
            frameSamples = (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 };
        }

        int frames = code switch
        {
            0 => 1,
            1 => 2,
            2 => 2,
            _ => packet.Length < 2 ? 0 : packet[1] & 0x3F,
        };

        if (frames <= 0 || frames > 48) return 0;

        int samples = frames * frameSamples;
        return samples > SampleRate * 120 / 1000 ? 0 : samples;
    }

    /// <summary>Turns a sample count at 48 kHz into a duration.</summary>
    /// <param name="sampleCount">The number of samples per channel.</param>
    /// <returns>How long that many samples last.</returns>
    public static TimeSpan ToDuration(long sampleCount) =>
        TimeSpan.FromTicks(sampleCount * TimeSpan.TicksPerSecond / SampleRate);
}
