using System;

namespace CodeBrix.VideoPlayback.Containers.Ogg;

/// <summary>
/// One packet reassembled out of an Ogg stream's pages.
/// </summary>
/// <remarks>
/// A packet owns its bytes, so it stays valid for as long as it is held. Reassembly happens at authoring
/// time, on a handful of megabytes, so the copy is worth the simplicity; nothing in playback reads Ogg.
/// </remarks>
public readonly struct OggPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="data">The packet's bytes.</param>
    /// <param name="serialNumber">The serial number of the logical stream it belongs to.</param>
    /// <param name="granulePosition">
    /// The granule position of the page the packet finished on, or -1 when the packet did not finish a page.
    /// </param>
    /// <param name="endsPage">True when this packet was the last one to finish on its page.</param>
    /// <param name="isFirstOfStream">True when the packet came from a page marked as the start of the stream.</param>
    /// <param name="isLastOfStream">True when the packet came from a page marked as the end of the stream.</param>
    public OggPacket(
        ReadOnlyMemory<byte> data,
        uint serialNumber,
        long granulePosition,
        bool endsPage,
        bool isFirstOfStream,
        bool isLastOfStream)
    {
        Data = data;
        SerialNumber = serialNumber;
        GranulePosition = granulePosition;
        EndsPage = endsPage;
        IsFirstOfStream = isFirstOfStream;
        IsLastOfStream = isLastOfStream;
    }

    /// <summary>The packet's bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The serial number of the logical stream the packet belongs to.</summary>
    public uint SerialNumber { get; }

    /// <summary>
    /// The granule position of the page this packet finished on, or -1 when the packet did not finish a page.
    /// What a granule counts is the codec's business: for Opus it is samples at 48 kHz including the codec's
    /// own priming, for Vorbis it is samples at the stream's rate.
    /// </summary>
    public long GranulePosition { get; }

    /// <summary>True when this packet was the last one to finish on the page it came from.</summary>
    public bool EndsPage { get; }

    /// <summary>True when the packet came from a page flagged as the beginning of its logical stream.</summary>
    public bool IsFirstOfStream { get; }

    /// <summary>True when the packet came from a page flagged as the end of its logical stream.</summary>
    public bool IsLastOfStream { get; }
}
