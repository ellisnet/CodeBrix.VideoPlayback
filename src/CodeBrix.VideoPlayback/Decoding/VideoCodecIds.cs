namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The codec identifier strings this library uses to name a video codec, and the matching audio and caption
/// identifiers the containers produce.
/// </summary>
/// <remarks>
/// <para>
/// A codec identifier names the CODEC, never the container: the same string is used whether the packets came
/// out of a Matroska file or a bespoke <c>.cbv</c> file. The video identifiers are the four-character codes
/// the AV1-in-ISOBMFF world already uses, so they are short, stable, and fit the bespoke container's
/// fixed-width codec field.
/// </para>
/// <para>
/// Comparisons are ordinal and case-insensitive everywhere in this library. Decoder factories should compare
/// the same way.
/// </para>
/// </remarks>
public static class VideoCodecIds
{
    /// <summary>AV1. Codec-private data is an <c>av1C</c> configuration record.</summary>
    public const string Av1 = "av01";

    /// <summary>
    /// Uncompressed planar video - the test and utility codec. Each packet is one frame's planes, written
    /// plane by plane with no padding between rows; codec-private data is a small fixed header.
    /// </summary>
    /// <remarks>
    /// A decoder for this codec is not shipped in the package: it exists so container, session, seek and
    /// presenter behaviour can be exercised without pulling in a real codec, and so a raw capture can be
    /// carried through the pipeline. Matroska's <c>V_UNCOMPRESSED</c> maps onto it.
    /// </remarks>
    public const string Raw = "raw";

    /// <summary>VP9. Reserved: no reader in this library produces it yet.</summary>
    public const string Vp9 = "vp09";

    /// <summary>H.264 / AVC. Reserved: no reader in this library produces it yet.</summary>
    public const string Avc = "avc1";

    /// <summary>H.265 / HEVC. Reserved: no reader in this library produces it yet.</summary>
    public const string Hevc = "hev1";

    /// <summary>Opus audio. Codec-private data is an <c>OpusHead</c> identification header.</summary>
    public const string Opus = "opus";

    /// <summary>Vorbis audio. Codec-private data is the three Xiph-laced setup headers.</summary>
    public const string Vorbis = "vorbis";

    /// <summary>
    /// FLAC, the lossless audio codec of the master (Mode3) flavour. The codec-private data is a FLAC stream
    /// header - the <c>fLaC</c> marker and the metadata blocks, STREAMINFO first, exactly Matroska's
    /// <c>A_FLAC</c> convention - and each packet is exactly one complete FLAC frame. Decoding it needs a
    /// CodeBrix.Audio.Core whose shared output serves the <c>flac</c> packet codec.
    /// </summary>
    public const string Flac = "flac";

    /// <summary>WebVTT captions. Cue payloads are UTF-8 text with an optional settings string.</summary>
    public const string WebVtt = "webvtt";

    /// <summary>SubRip-style plain UTF-8 captions - Matroska's <c>S_TEXT/UTF8</c>.</summary>
    public const string SubRip = "srt";

    /// <summary>Advanced SubStation captions - Matroska's <c>S_TEXT/ASS</c>. Text is extracted; styling is ignored.</summary>
    public const string Ass = "ass";
}
