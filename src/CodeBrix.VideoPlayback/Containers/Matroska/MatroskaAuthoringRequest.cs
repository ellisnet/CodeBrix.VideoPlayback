using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>
/// The inputs to <see cref="MatroskaMuxer.Write" />: the files an encoder produced, and where to put the
/// finished WebM or Matroska file.
/// </summary>
/// <remarks>
/// <para>
/// The video comes in as an IVF file holding AV1 - what FFmpeg writes with <c>-f ivf</c>, and what an in-process
/// AV1 encoder can write with <see cref="Ivf.IvfWriter" /> - and the audio either as an Ogg Opus or Ogg Vorbis
/// file or as a list of ready-made packets. Captions come as <c>.vtt</c> or <c>.srt</c> files and chapters as
/// an FFmpeg metadata file, the same inputs the bespoke container's authoring path takes.
/// </para>
/// <para>
/// Everything except <see cref="OutputPath" /> and at least one of video and audio is optional. The defaults
/// produce a CodeBrix Video Mode1 file: the WebM document type with the index in front of the media.
/// </para>
/// </remarks>
public sealed class MatroskaAuthoringRequest
{
    /// <summary>Where to write the finished file.</summary>
    public string OutputPath { get; set; }

    /// <summary>The document type to stamp into the file. Defaults to <see cref="MatroskaDocType.WebM" />.</summary>
    public MatroskaDocType DocType { get; set; } = MatroskaDocType.WebM;

    /// <summary>The path of an IVF file holding AV1 video, or null for a file with no video.</summary>
    public string VideoIvfPath { get; set; }

    /// <summary>A name for the video track, or null.</summary>
    public string VideoName { get; set; }

    /// <summary>A BCP 47 language tag for the video track, or null for none.</summary>
    public string VideoLanguage { get; set; }

    /// <summary>The path of an Ogg Opus or Ogg Vorbis file, or null.</summary>
    public string AudioOggPath { get; set; }

    /// <summary>
    /// An audio track supplied as ready-made packets, or null. At most one of this and
    /// <see cref="AudioOggPath" /> may be set.
    /// </summary>
    public MatroskaPacketAudioInput PacketAudio { get; set; }

    /// <summary>A name for the audio track, or null.</summary>
    public string AudioName { get; set; }

    /// <summary>A BCP 47 language tag for the audio track, or null for none.</summary>
    public string AudioLanguage { get; set; }

    /// <summary>The caption files to include, each with its language, name and flags.</summary>
    public IList<MatroskaCaptionInput> Captions { get; } = new List<MatroskaCaptionInput>();

    /// <summary>The path of a chapter file in FFmpeg's metadata format, or null for no chapters.</summary>
    public string ChaptersPath { get; set; }

    /// <summary>
    /// The media's title, written as the <c>Info</c> element's <c>Title</c>, or null to take the file-level
    /// <c>title=</c> of <see cref="ChaptersPath" /> when it has one - what FFmpeg does with a chapter file it
    /// takes its metadata from - and otherwise to write none. An empty string writes none either way.
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// True (the default) to place the index - the <c>Cues</c> element - in front of the first cluster, which is
    /// what the streamable profile asks for; false to place it after the last cluster, where an ordinary muxer
    /// puts it.
    /// </summary>
    public bool CuesToFront { get; set; } = true;

    /// <summary>
    /// True to open the finished file and judge it against the streamable profile, putting the report on the
    /// result. Defaults to false; the authoring library asks for it.
    /// </summary>
    public bool EvaluateProfile { get; set; }
}
