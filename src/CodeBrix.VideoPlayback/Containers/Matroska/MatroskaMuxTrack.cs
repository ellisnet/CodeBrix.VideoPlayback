using System;
using CodeBrix.VideoPlayback.Codecs;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>Everything the muxer knows about one track while it lays the file out.</summary>
internal sealed class MatroskaMuxTrack
{
    /// <summary>The Matroska <c>TrackType</c> for video.</summary>
    internal const int TrackTypeVideo = 1;

    /// <summary>The Matroska <c>TrackType</c> for audio.</summary>
    internal const int TrackTypeAudio = 2;

    /// <summary>The Matroska <c>TrackType</c> for subtitles.</summary>
    internal const int TrackTypeSubtitle = 17;

    internal int Number;
    internal MediaTrackKind Kind;
    internal string CodecId = string.Empty;
    internal string Name = string.Empty;
    internal string LanguageTag = string.Empty;
    internal string LanguageCode = MatroskaLanguageCodes.Undetermined;
    internal bool LanguageIsExact = true;
    internal bool IsDefault = true;
    internal bool IsForced;
    internal bool IsHearingImpaired;
    internal byte[] CodecPrivate = Array.Empty<byte>();
    internal long DefaultDurationNs;
    internal long CodecDelayNs;
    internal long SeekPreRollNs;

    internal int PixelWidth;
    internal int PixelHeight;
    internal Av1SequenceHeader SequenceHeader;

    internal int SampleRate;
    internal int Channels;

    internal int BlockCount;

    /// <summary>The track's Matroska <c>TrackType</c>.</summary>
    internal int TrackType =>
        Kind switch
        {
            MediaTrackKind.Video => TrackTypeVideo,
            MediaTrackKind.Audio => TrackTypeAudio,
            _ => TrackTypeSubtitle,
        };

    /// <summary>The order this track's blocks take among blocks with the same timestamp: video, then audio, then captions.</summary>
    internal int KindOrder =>
        Kind switch
        {
            MediaTrackKind.Video => 0,
            MediaTrackKind.Audio => 1,
            _ => 2,
        };

    /// <summary>Sets the language from a BCP 47 tag.</summary>
    /// <param name="tag">The tag, or null.</param>
    internal void SetLanguage(string tag)
    {
        LanguageTag = string.IsNullOrWhiteSpace(tag) ? string.Empty : tag.Trim();
        LanguageCode = MatroskaLanguageCodes.ToMatroska(LanguageTag, out LanguageIsExact);
    }
}
