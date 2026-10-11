namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>The document type a <see cref="MatroskaMuxer" /> stamps into the file's EBML header.</summary>
public enum MatroskaDocType
{
    /// <summary>
    /// <c>webm</c>: the WebM profile of Matroska, and the one a CodeBrix Video Mode1 file uses. Every element
    /// the muxer writes is one the WebM element list names, with the single exception described on
    /// <see cref="MatroskaCaptionInput" /> for hearing-impaired caption tracks.
    /// </summary>
    WebM = 0,

    /// <summary>
    /// <c>matroska</c>: the full format. The layout is the same; the difference is that a language tag a
    /// three-letter code cannot express is also stated in full as a BCP 47 tag.
    /// </summary>
    Matroska = 1,
}
