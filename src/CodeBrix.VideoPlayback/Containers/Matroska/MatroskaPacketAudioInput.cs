using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// An audio track handed to <see cref="MatroskaMuxer.Write" /> as ready-made packets rather than as an Ogg
/// file - the shape an in-process encoder hands its output over in.
/// </summary>
/// <remarks>
/// <para>
/// Two codecs are accepted, named by their <see cref="Decoding.VideoCodecIds" /> identifiers:
/// <see cref="Decoding.VideoCodecIds.Opus" />, whose codec-private data is the <c>OpusHead</c> identification
/// header, and <see cref="Decoding.VideoCodecIds.Vorbis" />, whose codec-private data is the three setup headers
/// Xiph-laced into one block (see <see cref="Ogg.OggAudioStream.BuildXiphCodecPrivate" />). For Opus the
/// pre-skip is read out of the <c>OpusHead</c> and written as the track's <c>CodecDelay</c>.
/// </para>
/// <para>Every packet is written as a key frame: an audio packet needs nothing before it to be decoded.</para>
/// </remarks>
public sealed class MatroskaPacketAudioInput
{
    /// <summary>Creates an audio input.</summary>
    /// <param name="codecId">The codec identifier: <c>opus</c> or <c>vorbis</c>.</param>
    /// <param name="codecPrivate">The codec's initialisation data.</param>
    /// <param name="sampleRate">Samples per second. An Opus track is always written as 48000, whatever is stated here.</param>
    /// <param name="channels">How many channels.</param>
    /// <exception cref="ArgumentException"><paramref name="codecId" /> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate or channel count is not positive.</exception>
    public MatroskaPacketAudioInput(string codecId, ReadOnlyMemory<byte> codecPrivate, int sampleRate, int channels)
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

    /// <summary>
    /// How many samples (per channel) at the very end of the last packet are padding to be thrown away, or 0.
    /// A positive value is written as the <c>DiscardPadding</c> of the track's last block.
    /// </summary>
    public int TrailingTrimSamples { get; set; }

    /// <summary>The packets, in timestamp order.</summary>
    public IList<MatroskaAudioPacket> Packets { get; } = new List<MatroskaAudioPacket>();
}
