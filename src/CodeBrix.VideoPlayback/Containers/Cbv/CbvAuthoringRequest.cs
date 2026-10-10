using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Containers.Cbv;

/// <summary>
/// The inputs to <see cref="CbvAuthoring.Write" />: the files an encoder produced, and where to put the
/// finished container.
/// </summary>
/// <remarks>
/// <para>
/// The video comes in as an IVF file - the thin wrapper an encoder writes around a coded video stream - and
/// the audio as an Ogg Opus or Ogg Vorbis file. Both are what FFmpeg produces with <c>-f ivf</c> and
/// <c>-f ogg</c>, so authoring needs no tool beyond the encoder that made them.
/// </para>
/// <para>Everything except <see cref="OutputPath" /> is optional; a file may be video-only or audio-only.</para>
/// </remarks>
public sealed class CbvAuthoringRequest
{
    /// <summary>Where to write the finished <c>.cbv</c> file.</summary>
    public string OutputPath { get; set; }

    /// <summary>The path of an IVF file holding the coded video, or null for a file with no video.</summary>
    public string VideoIvfPath { get; set; }

    /// <summary>
    /// The path of a second IVF file holding the picture's ALPHA CHANNEL as a monochrome AV1 stream, or null
    /// for a file with no alpha. It must be in lock step with <see cref="VideoIvfPath" /> - the same size, bit
    /// depth, frame count, timestamps and key-frame positions - and full range. Supplying it makes the file a
    /// master (Mode3, format version 1) file.
    /// </summary>
    public string AlphaIvfPath { get; set; }

    /// <summary>The path of an Ogg Opus or Ogg Vorbis file, or null for a file with no audio.</summary>
    public string AudioOggPath { get; set; }

    /// <summary>
    /// An audio track supplied as ready-made packets - a FLAC stream split into frames - or null. At most one
    /// of this and <see cref="AudioOggPath" /> may be set. FLAC makes the file a master (Mode3, format version 1)
    /// file.
    /// </summary>
    public CbvPacketAudioInput PacketAudio { get; set; }

    /// <summary>The caption files to include, each with its language and flags.</summary>
    public IList<CbvCaptionInput> Captions { get; } = new List<CbvCaptionInput>();

    /// <summary>The path of a chapter file in FFmpeg's metadata format, or null for no chapters.</summary>
    public string ChaptersPath { get; set; }

    /// <summary>A BCP 47 language tag for the audio track, or null.</summary>
    public string AudioLanguage { get; set; }

    /// <summary>A name for the audio track, or null.</summary>
    public string AudioName { get; set; }

    /// <summary>A name for the video track, or null.</summary>
    public string VideoName { get; set; }
}
