using System;
using System.Collections.Generic;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;

namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// Everything one authored file needs: where the picture and the sound come from, how they are encoded, what
/// text rides along with them, and where the result goes.
/// </summary>
/// <remarks>
/// <para>
/// One request shape serves both flavours. <see cref="Flavour" /> decides how many times FFmpeg runs and who
/// writes the container; almost everything else means the same thing either way, and the handful of settings
/// that do not are marked in their own documentation.
/// </para>
/// <para>
/// A request can be rendered without being run - see <see cref="CbvAuthor.RenderCommands" /> - which is how
/// a pipeline shows its work, and how the command lines can be tested on a machine with no FFmpeg at all.
/// </para>
/// </remarks>
public sealed class VideoAuthoringRequest
{
    /// <summary>Which flavour of <c>.cbv</c> to write. The WebM-profile one by default.</summary>
    public VideoAuthoringFlavour Flavour { get; set; } = VideoAuthoringFlavour.WebMProfile;

    /// <summary>The media file the picture and the sound are read from.</summary>
    public string SourcePath { get; set; }

    /// <summary>Where the finished file is written.</summary>
    public string OutputPath { get; set; }

    /// <summary>How the picture is encoded.</summary>
    public AuthoringVideoSettings Video { get; } = new AuthoringVideoSettings();

    /// <summary>How the sound is encoded.</summary>
    public AuthoringAudioSettings Audio { get; } = new AuthoringAudioSettings();

    /// <summary>The caption files to carry, each with its language, name and flags. Empty by default.</summary>
    /// <remarks>
    /// In the WebM-profile flavour these become extra INPUTS and are copied into the container with
    /// <c>-c:s copy</c> - never re-encoded, because FFmpeg's WebVTT encoder discards cue identifiers and
    /// positioning settings. In the bespoke flavour they are read by the managed muxer and stored whole in
    /// the header region.
    /// <para>
    /// ONE FLAG DOES NOT SURVIVE THE WEBM-PROFILE FLAVOUR: hearing-impaired. A WebM document has no element
    /// for it - Matroska gained one, WebM's element list never did - so FFmpeg's WebM muxer writes the
    /// default and forced flags and drops that one. The result carries a note naming the tracks it happened
    /// to. The bespoke flavour keeps all three, and so does <see cref="AuthoringContainerFormat.Matroska" />.
    /// </para>
    /// </remarks>
    public IList<AuthoringCaptionInput> Captions { get; } = new List<AuthoringCaptionInput>();

    /// <summary>
    /// The path of a chapter file in FFmpeg's metadata format, or null for no chapters.
    /// </summary>
    /// <remarks>
    /// ONE file serves both flavours - but not identically, and the difference is not papered over.
    /// The bespoke flavour honours per-language <c>title-&lt;bcp47&gt;=</c> keys. The WebM-profile flavour
    /// cannot: FFmpeg's Matroska muxer writes one untagged <c>ChapterDisplay</c> per chapter, so every
    /// per-language title collapses into the untagged <c>title=</c> and the rest are lost. When that happens
    /// the result carries a note naming the languages that were dropped.
    /// </remarks>
    public string ChaptersPath { get; set; }

    /// <summary>
    /// Which Matroska-family muxer the WebM-profile pass uses. WebM by default. Ignored by the bespoke
    /// flavour.
    /// </summary>
    public AuthoringContainerFormat Container { get; set; } = AuthoringContainerFormat.WebM;

    /// <summary>
    /// True to move the seek index in FRONT of the media data (<c>-cues_to_front 1</c>). True by default,
    /// and it is the rule the streamable profile is built around: a reader holding the first few kilobytes
    /// of the file already holds the whole index. Ignored by the bespoke flavour, which is index-first by
    /// construction.
    /// </summary>
    public bool CuesToFront { get; set; } = true;

    /// <summary>
    /// True to name the wanted streams explicitly (<c>-map 0:v:0 -map 0:a:0</c>) rather than leaving the
    /// choice to FFmpeg's default stream selection. True by default, because a phone recording carries a
    /// third, timed-metadata stream that nothing downstream wants.
    /// </summary>
    public bool SelectStreamsExplicitly { get; set; } = true;

    /// <summary>
    /// True to carry the source's own global metadata into the output. False by default, which emits
    /// <c>-map_metadata -1</c> and drops the recording's creation time and device strings.
    /// </summary>
    /// <remarks>
    /// A chapter file overrides this outright: when <see cref="ChaptersPath" /> is set, the metadata mapping
    /// names the chapter file's input instead, because that is where the chapters have to come from.
    /// </remarks>
    public bool CopySourceMetadata { get; set; }

    /// <summary>
    /// True to refuse a request whose audio would need the application to add a package. False by default.
    /// </summary>
    /// <remarks>
    /// Opus audio plays only when the application references CodeBrix.Audio.Opus and calls its
    /// <c>Register()</c>; Vorbis plays with the core playback package alone. An application that ships clips
    /// inside itself and wants its published output to contain no Opus binary at all sets this true, and a
    /// request that would have produced an Opus track fails at once rather than at run time on a customer's
    /// machine.
    /// </remarks>
    public bool RequireNoExtraPlaybackPackages { get; set; }

    /// <summary>
    /// True to read the finished file back and check it against the streamable profile. True by default.
    /// </summary>
    public bool ValidateProfile { get; set; } = true;

    /// <summary>
    /// True to fail the whole request when the finished file does not pass the profile. True by default.
    /// </summary>
    /// <remarks>
    /// Set it false to author a file that is deliberately NOT a profile file - an ordinary Matroska with its
    /// cues at the end, say - and still keep the report of why.
    /// </remarks>
    public bool FailWhenProfileFails { get; set; } = true;

    /// <summary>
    /// The folder the intermediate files are written into, or null for the system's temporary folder.
    /// </summary>
    /// <remarks>
    /// The bespoke flavour writes an IVF and an Ogg file here and deletes them afterwards, whether the run
    /// succeeded or failed. A composed lookup table is written here too. Put it on the same volume as the
    /// output when the media is large.
    /// </remarks>
    public string TemporaryFolder { get; set; }

    /// <summary>
    /// The source's duration, so progress can be reported as a percentage, or zero for no progress at all.
    /// </summary>
    public TimeSpan SourceDuration { get; set; }

    /// <summary>
    /// Called as each pass advances, or null. Needs <see cref="SourceDuration" /> to be set.
    /// </summary>
    public Action<AuthoringProgress> ProgressCallback { get; set; }

    /// <summary>
    /// The token that stops the run. <see cref="System.Threading.CancellationToken.None" /> by default, which
    /// never stops anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CbvAuthor.Write" /> observes it BETWEEN stages and DURING every FFmpeg pass, so a request
    /// cancelled while an encode is running stops within moments rather than at the end of the encode.
    /// </para>
    /// <para>
    /// WHAT CANCELLING DOES. The child process is killed OUTRIGHT rather than asked to finish tidily. FFmpeg
    /// offers a graceful "q" quit, and it is not used here: on the machine this library is developed on, that
    /// quit HANGS, which would turn a cancel into a wait of unbounded length - and the file being written is
    /// being thrown away in any case, so there is nothing for a tidy finish to protect. The partly written
    /// output file is deleted, the intermediate files are deleted exactly as they are after any run, and
    /// <see cref="OperationCanceledException" /> is thrown - the ordinary .NET contract, so an application
    /// that already handles a cancelled task handles this with no new catch block. An effective colour table
    /// the request asked to KEEP is left where it is: it was written whole before any encoding started, and
    /// it is not a partial anything.
    /// </para>
    /// <para>
    /// <see cref="CbvAuthor.RenderCommands" /> checks it once, before it renders, and never again: a dry run
    /// starts no process, touches no disk and returns in microseconds, so there is nothing to interrupt.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken { get; set; }
}
