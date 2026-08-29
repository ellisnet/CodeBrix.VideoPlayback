using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// Everything a container says about one of its tracks: what codec it holds, what to initialise that codec
/// with, what language it is in, and the shape of its content.
/// </summary>
/// <remarks>
/// One type covers video, audio and captions, because most of the fields are shared and because an
/// application listing a file's tracks wants one list, not three. The fields that only make sense for one
/// kind read zero or empty for the others.
/// </remarks>
public sealed class MediaTrackInfo
{
    /// <summary>Creates a track description.</summary>
    public MediaTrackInfo()
    {
        CodecId = string.Empty;
        Language = string.Empty;
        Name = string.Empty;
        Color = VideoColorInfo.Unspecified;
        IsEnabled = true;
    }

    /// <summary>The track's identifier within the file.</summary>
    public int Id { get; set; }

    /// <summary>What the track carries.</summary>
    public MediaTrackKind Kind { get; set; }

    /// <summary>The codec identifier, as named in <see cref="VideoCodecIds" />. Never null.</summary>
    public string CodecId { get; set; }

    /// <summary>
    /// The codec's initialisation data, exactly as the container carried it: an <c>av1C</c> record for AV1, an
    /// <c>OpusHead</c> for Opus, the three Xiph-laced setup headers for Vorbis, empty when the codec needs
    /// none.
    /// </summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; set; }

    /// <summary>The BCP 47 language tag, normalised from whatever the container carried. Never null.</summary>
    public string Language { get; set; }

    /// <summary>The track's name as the file gave it, or an empty string. Never null.</summary>
    public string Name { get; set; }

    /// <summary>True when the file marks the track as the one to use if nothing else is chosen.</summary>
    public bool IsDefault { get; set; }

    /// <summary>True when the file marks the track as forced - shown whatever the viewer selected.</summary>
    public bool IsForced { get; set; }

    /// <summary>True when the file marks the track as written for viewers who are deaf or hard of hearing.</summary>
    public bool IsHearingImpaired { get; set; }

    /// <summary>False when the file marks the track as one a player should ignore unless asked.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// How long each unit of the track lasts when that is fixed - a video frame's duration, say - or
    /// <see cref="TimeSpan.Zero" /> when the file does not state it.
    /// </summary>
    public TimeSpan DefaultDuration { get; set; }

    /// <summary>Video only: the coded width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Video only: the coded height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Video only: the width to show frames at, after pixel-aspect correction.</summary>
    public int DisplayWidth { get; set; }

    /// <summary>Video only: the height to show frames at, after pixel-aspect correction.</summary>
    public int DisplayHeight { get; set; }

    /// <summary>Video only: bits per sample, when the container states it. Zero means "ask the decoder".</summary>
    public int BitDepth { get; set; }

    /// <summary>Video only: the plane layout, when the container states it.</summary>
    public VideoPixelLayout Layout { get; set; }

    /// <summary>Video only: the colour description the container carried.</summary>
    public VideoColorInfo Color { get; set; }

    /// <summary>Video only: mastering metadata for high-dynamic-range content, or null.</summary>
    public HdrMetadata Hdr { get; set; }

    /// <summary>Audio only: samples per second.</summary>
    public int SampleRate { get; set; }

    /// <summary>Audio only: how many channels.</summary>
    public int Channels { get; set; }

    /// <summary>
    /// Audio only: how much of the start of the decoded audio is codec priming that must be thrown away -
    /// Matroska's <c>CodecDelay</c>.
    /// </summary>
    public TimeSpan CodecDelay { get; set; }

    /// <summary>
    /// Audio only: how much audio must be decoded and discarded before a seek sounds right - Matroska's
    /// <c>SeekPreRoll</c>. About 80 milliseconds for Opus; zero for Vorbis, which needs only one packet.
    /// </summary>
    public TimeSpan SeekPreRoll { get; set; }

    /// <summary>
    /// Audio only: how many samples per channel of codec priming the codec itself declares - Opus's
    /// pre-skip. Zero when the codec has none.
    /// </summary>
    public int PreSkipSamples { get; set; }

    /// <summary>
    /// Audio only: how many samples per channel to drop from the very end of the track, when the authoring
    /// tool recorded it. Zero when it did not.
    /// </summary>
    public int TrailingTrimSamples { get; set; }

    /// <summary>Captions only: the text format the cues are written in.</summary>
    public CodeBrix.VideoPlayback.Captions.CaptionFormat CaptionFormat { get; set; }

    /// <inheritdoc />
    public override string ToString()
    {
        string label = string.IsNullOrEmpty(Name) ? CodecId : $"{Name} ({CodecId})";
        return Kind switch
        {
            MediaTrackKind.Video => $"track {Id} video: {label}, {Width}x{Height}",
            MediaTrackKind.Audio => $"track {Id} audio: {label}, {SampleRate} Hz, {Channels} ch, {Language}",
            MediaTrackKind.Caption => $"track {Id} captions: {label}, {Language}",
            _ => $"track {Id}: {label}",
        };
    }
}
