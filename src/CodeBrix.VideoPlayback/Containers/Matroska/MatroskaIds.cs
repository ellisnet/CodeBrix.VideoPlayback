namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// The element identifiers RFC 9559 gives the parts of a Matroska document, and that WebM inherits.
/// </summary>
/// <remarks>
/// Only the elements this library actually reads or deliberately steps over are named. An identifier that is
/// not here is skipped by size like any other unknown element, which is what makes the format extensible.
/// </remarks>
public static class MatroskaIds
{
    /// <summary>The Segment: everything in the file except the EBML header.</summary>
    public const uint Segment = 0x18538067;

    /// <summary>An index of where the other top-level elements are.</summary>
    public const uint SeekHead = 0x114D9B74;

    /// <summary>One entry in a <see cref="SeekHead" />.</summary>
    public const uint Seek = 0x4DBB;

    /// <summary>The identifier of the element a <see cref="Seek" /> points at.</summary>
    public const uint SeekId = 0x53AB;

    /// <summary>Where a <see cref="Seek" />'s target is, counted from the start of the Segment's payload.</summary>
    public const uint SeekPosition = 0x53AC;

    /// <summary>The Segment's own metadata: how time is counted, how long the media is, who wrote it.</summary>
    public const uint Info = 0x1549A966;

    /// <summary>How many nanoseconds one unit of every timestamp in the file represents.</summary>
    public const uint TimestampScale = 0x2AD7B1;

    /// <summary>How long the media lasts, counted in <see cref="TimestampScale" /> units.</summary>
    public const uint Duration = 0x4489;

    /// <summary>The media's title.</summary>
    public const uint Title = 0x7BA9;

    /// <summary>The library that laid the file out.</summary>
    public const uint MuxingApp = 0x4D80;

    /// <summary>The application that produced the file.</summary>
    public const uint WritingApp = 0x5741;

    /// <summary>The list of tracks.</summary>
    public const uint Tracks = 0x1654AE6B;

    /// <summary>One track.</summary>
    public const uint TrackEntry = 0xAE;

    /// <summary>The number that identifies a track inside its blocks.</summary>
    public const uint TrackNumber = 0xD7;

    /// <summary>A track's unique identifier, stable across edits.</summary>
    public const uint TrackUid = 0x73C5;

    /// <summary>What the track carries: 1 video, 2 audio, 17 subtitles.</summary>
    public const uint TrackType = 0x83;

    /// <summary>Zero when a player should ignore the track unless it is asked for.</summary>
    public const uint FlagEnabled = 0xB9;

    /// <summary>Set when the track is the one to use if nothing else is chosen.</summary>
    public const uint FlagDefault = 0x88;

    /// <summary>Set when the track must be shown whatever the viewer selected.</summary>
    public const uint FlagForced = 0x55AA;

    /// <summary>Set when the track is written for viewers who are deaf or hard of hearing.</summary>
    public const uint FlagHearingImpaired = 0x55AB;

    /// <summary>Set when the writer laced this track's frames together.</summary>
    public const uint FlagLacing = 0x9C;

    /// <summary>How long one frame of the track lasts, in nanoseconds.</summary>
    public const uint DefaultDuration = 0x23E383;

    /// <summary>The track's human-readable name.</summary>
    public const uint Name = 0x536E;

    /// <summary>The track's language as a three-letter ISO 639-2 code.</summary>
    public const uint Language = 0x22B59C;

    /// <summary>The track's language as a BCP 47 tag, which wins over <see cref="Language" /> when both are present.</summary>
    public const uint LanguageBcp47 = 0x22B59D;

    /// <summary>The codec the track's frames are in.</summary>
    public const uint CodecId = 0x86;

    /// <summary>Whatever the codec needs to initialise itself.</summary>
    public const uint CodecPrivate = 0x63A2;

    /// <summary>A human-readable name for the codec.</summary>
    public const uint CodecName = 0x258688;

    /// <summary>How much of the start of the decoded audio is codec priming, in nanoseconds.</summary>
    public const uint CodecDelay = 0x56AA;

    /// <summary>How much audio must be decoded and thrown away after a seek, in nanoseconds.</summary>
    public const uint SeekPreRoll = 0x56BB;

    /// <summary>Compression or header stripping applied to a track's frames.</summary>
    public const uint ContentEncodings = 0x6D80;

    /// <summary>A track's video settings.</summary>
    public const uint Video = 0xE0;

    /// <summary>The stored width in pixels.</summary>
    public const uint PixelWidth = 0xB0;

    /// <summary>The stored height in pixels.</summary>
    public const uint PixelHeight = 0xBA;

    /// <summary>How many stored pixel columns at the left are not part of the picture.</summary>
    public const uint PixelCropLeft = 0x54CC;

    /// <summary>How many stored pixel columns at the right are not part of the picture.</summary>
    public const uint PixelCropRight = 0x54DD;

    /// <summary>How many stored pixel rows at the top are not part of the picture.</summary>
    public const uint PixelCropTop = 0x54BB;

    /// <summary>How many stored pixel rows at the bottom are not part of the picture.</summary>
    public const uint PixelCropBottom = 0x54AA;

    /// <summary>The width the picture should be shown at.</summary>
    public const uint DisplayWidth = 0x54B0;

    /// <summary>The height the picture should be shown at.</summary>
    public const uint DisplayHeight = 0x54BA;

    /// <summary>What unit the display size is in: 0 pixels, 1 centimetres, 2 inches, 3 an aspect ratio.</summary>
    public const uint DisplayUnit = 0x54B2;

    /// <summary>A four-character code naming the pixel format of an uncompressed video track.</summary>
    public const uint ColourSpace = 0x2EB524;

    /// <summary>The colour description of a video track.</summary>
    public const uint Colour = 0x55B0;

    /// <summary>The YCbCr matrix the chroma planes were formed with.</summary>
    public const uint MatrixCoefficients = 0x55B1;

    /// <summary>Bits per colour channel.</summary>
    public const uint BitsPerChannel = 0x55B2;

    /// <summary>How much the chroma planes are subsampled horizontally: 1 means half width.</summary>
    public const uint ChromaSubsamplingHorz = 0x55B3;

    /// <summary>How much the chroma planes are subsampled vertically: 1 means half height.</summary>
    public const uint ChromaSubsamplingVert = 0x55B4;

    /// <summary>Where a chroma sample sits horizontally: 0 unspecified, 1 left-collocated, 2 half way.</summary>
    public const uint ChromaSitingHorz = 0x55B7;

    /// <summary>Where a chroma sample sits vertically: 0 unspecified, 1 top-collocated, 2 half way.</summary>
    public const uint ChromaSitingVert = 0x55B8;

    /// <summary>Whether the samples cover the full numeric range: 0 unspecified, 1 studio, 2 full.</summary>
    public const uint Range = 0x55B9;

    /// <summary>The transfer characteristic.</summary>
    public const uint TransferCharacteristics = 0x55BA;

    /// <summary>The colour primaries.</summary>
    public const uint Primaries = 0x55BB;

    /// <summary>The maximum content light level, in candela per square metre.</summary>
    public const uint MaxCll = 0x55BC;

    /// <summary>The maximum frame-average light level, in candela per square metre.</summary>
    public const uint MaxFall = 0x55BD;

    /// <summary>The mastering display's primaries and luminance range.</summary>
    public const uint MasteringMetadata = 0x55D0;

    /// <summary>The x coordinate of the mastering display's red primary.</summary>
    public const uint PrimaryRChromaticityX = 0x55D1;

    /// <summary>The y coordinate of the mastering display's red primary.</summary>
    public const uint PrimaryRChromaticityY = 0x55D2;

    /// <summary>The x coordinate of the mastering display's green primary.</summary>
    public const uint PrimaryGChromaticityX = 0x55D3;

    /// <summary>The y coordinate of the mastering display's green primary.</summary>
    public const uint PrimaryGChromaticityY = 0x55D4;

    /// <summary>The x coordinate of the mastering display's blue primary.</summary>
    public const uint PrimaryBChromaticityX = 0x55D5;

    /// <summary>The y coordinate of the mastering display's blue primary.</summary>
    public const uint PrimaryBChromaticityY = 0x55D6;

    /// <summary>The x coordinate of the mastering display's white point.</summary>
    public const uint WhitePointChromaticityX = 0x55D7;

    /// <summary>The y coordinate of the mastering display's white point.</summary>
    public const uint WhitePointChromaticityY = 0x55D8;

    /// <summary>The mastering display's maximum luminance.</summary>
    public const uint LuminanceMax = 0x55D9;

    /// <summary>The mastering display's minimum luminance.</summary>
    public const uint LuminanceMin = 0x55DA;

    /// <summary>How the video should be projected - rotation and spherical layouts.</summary>
    public const uint Projection = 0x7670;

    /// <summary>A track's audio settings.</summary>
    public const uint Audio = 0xE1;

    /// <summary>Samples per second.</summary>
    public const uint SamplingFrequency = 0xB5;

    /// <summary>Samples per second after the codec's own upsampling, when it has any.</summary>
    public const uint OutputSamplingFrequency = 0x78B5;

    /// <summary>How many audio channels.</summary>
    public const uint Channels = 0x9F;

    /// <summary>Bits per audio sample.</summary>
    public const uint AudioBitDepth = 0x6264;

    /// <summary>A group of frames that share a timestamp base.</summary>
    public const uint Cluster = 0x1F43B675;

    /// <summary>The cluster's timestamp, in <see cref="TimestampScale" /> units.</summary>
    public const uint ClusterTimestamp = 0xE7;

    /// <summary>A frame, or a lace of frames, with its flags in the block itself.</summary>
    public const uint SimpleBlock = 0xA3;

    /// <summary>A frame that needs more said about it than a <see cref="SimpleBlock" /> can carry.</summary>
    public const uint BlockGroup = 0xA0;

    /// <summary>The frame inside a <see cref="BlockGroup" />.</summary>
    public const uint Block = 0xA1;

    /// <summary>How long the block's frame lasts, in <see cref="TimestampScale" /> units.</summary>
    public const uint BlockDuration = 0x9B;

    /// <summary>How much of the end of this block's decoded audio to throw away, in nanoseconds.</summary>
    public const uint DiscardPadding = 0x75A2;

    /// <summary>
    /// A frame this one is predicted from. Its ABSENCE from a <see cref="BlockGroup" /> is what marks the
    /// frame as a key frame, because a <see cref="Block" /> has no flag bit of its own for that.
    /// </summary>
    public const uint ReferenceBlock = 0xFB;

    /// <summary>Extra data attached to a block.</summary>
    public const uint BlockAdditions = 0x75A1;

    /// <summary>One piece of extra data.</summary>
    public const uint BlockMore = 0xA6;

    /// <summary>Which kind of extra data a <see cref="BlockMore" /> holds. Defaults to 1 when absent.</summary>
    public const uint BlockAddId = 0xEE;

    /// <summary>The extra data itself.</summary>
    public const uint BlockAdditional = 0xA5;

    /// <summary>The index that makes seeking possible.</summary>
    public const uint Cues = 0x1C53BB6B;

    /// <summary>One indexed moment.</summary>
    public const uint CuePoint = 0xBB;

    /// <summary>The moment a <see cref="CuePoint" /> indexes, in <see cref="TimestampScale" /> units.</summary>
    public const uint CueTime = 0xB3;

    /// <summary>Where one track's frame for that moment is.</summary>
    public const uint CueTrackPositions = 0xB7;

    /// <summary>Which track the positions belong to.</summary>
    public const uint CueTrack = 0xF7;

    /// <summary>Where the cluster is, counted from the start of the Segment's payload.</summary>
    public const uint CueClusterPosition = 0xF1;

    /// <summary>Where the block is inside that cluster, counted from the start of the cluster's payload.</summary>
    public const uint CueRelativePosition = 0xF0;

    /// <summary>How long the indexed frame lasts.</summary>
    public const uint CueDuration = 0xB2;

    /// <summary>The chapter markers.</summary>
    public const uint Chapters = 0x1043A770;

    /// <summary>One list of chapters. A file may carry several; this library reads one.</summary>
    public const uint EditionEntry = 0x45B9;

    /// <summary>The edition's unique identifier.</summary>
    public const uint EditionUid = 0x45BC;

    /// <summary>Set when the edition is the one to use if nothing else is chosen.</summary>
    public const uint EditionFlagDefault = 0x45DB;

    /// <summary>Set when the edition should not be listed to the viewer.</summary>
    public const uint EditionFlagHidden = 0x45BD;

    /// <summary>One chapter.</summary>
    public const uint ChapterAtom = 0xB6;

    /// <summary>The chapter's unique identifier.</summary>
    public const uint ChapterUid = 0x73C4;

    /// <summary>When the chapter begins, in nanoseconds.</summary>
    public const uint ChapterTimeStart = 0x91;

    /// <summary>When the chapter ends, in nanoseconds.</summary>
    public const uint ChapterTimeEnd = 0x92;

    /// <summary>Set when the chapter should not be listed to the viewer.</summary>
    public const uint ChapterFlagHidden = 0x98;

    /// <summary>The chapter's title in one language.</summary>
    public const uint ChapterDisplay = 0x80;

    /// <summary>The title text.</summary>
    public const uint ChapString = 0x85;

    /// <summary>The title's language as a three-letter ISO 639-2 code.</summary>
    public const uint ChapLanguage = 0x437C;

    /// <summary>The title's language as a BCP 47 tag.</summary>
    public const uint ChapLanguageBcp47 = 0x437D;

    /// <summary>Free-form metadata about the file or its tracks.</summary>
    public const uint Tags = 0x1254C367;

    /// <summary>Files carried along inside the media - fonts, cover art.</summary>
    public const uint Attachments = 0x1941A469;
}
