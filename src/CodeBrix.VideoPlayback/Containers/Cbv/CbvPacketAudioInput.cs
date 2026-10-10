using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// An audio track handed to <see cref="CbvAuthoring.Write" /> as ready-made packets rather than as an Ogg
/// file - the shape a FLAC stream arrives in once it has been split into frames.
/// </summary>
/// <remarks>
/// <para>
/// The master (Mode3) flavour's FLAC track is the case this exists for: FFmpeg writes a <c>.flac</c> file, the
/// authoring library splits it into its stream header and one packet per FLAC frame, and this carries them to
/// the muxer unchanged. Each packet becomes exactly one chunk, byte for byte.
/// </para>
/// <para>Every packet is written as a key frame: an audio packet needs nothing before it to be decoded.</para>
/// </remarks>
public sealed class CbvPacketAudioInput
{
    /// <summary>Creates an audio input.</summary>
    /// <param name="codecId">The codec identifier - see <see cref="Decoding.VideoCodecIds" />.</param>
    /// <param name="codecPrivate">The codec's initialisation data, such as a FLAC stream header.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="channels">How many channels.</param>
    /// <exception cref="ArgumentException"><paramref name="codecId" /> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate or channel count is not positive.</exception>
    public CbvPacketAudioInput(string codecId, ReadOnlyMemory<byte> codecPrivate, int sampleRate, int channels)
    {
        if (string.IsNullOrWhiteSpace(codecId)) throw new ArgumentException("A codec identifier is required.", nameof(codecId));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "The channel count must be positive.");

        CodecId = codecId;
        CodecPrivate = codecPrivate;
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>The codec identifier.</summary>
    public string CodecId { get; }

    /// <summary>The codec's initialisation data.</summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; }

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }

    /// <summary>How many channels.</summary>
    public int Channels { get; }

    /// <summary>The packets, in presentation order.</summary>
    public IList<CbvAudioPacket> Packets { get; } = new List<CbvAudioPacket>();
}
