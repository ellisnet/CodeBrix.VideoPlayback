namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// What a track in a media file carries.
/// </summary>
public enum MediaTrackKind
{
    /// <summary>Something this library does not read - a font attachment, a data track, a bitmap subtitle.</summary>
    Unknown = 0,

    /// <summary>Compressed video.</summary>
    Video = 1,

    /// <summary>Compressed audio.</summary>
    Audio = 2,

    /// <summary>Text captions.</summary>
    Caption = 3,
}
