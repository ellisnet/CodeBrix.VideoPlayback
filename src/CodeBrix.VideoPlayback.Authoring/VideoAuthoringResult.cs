using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;

namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// What one authoring run produced: the file, the exact commands that made it, what the profile made of it,
/// and anything the run wants the caller to know.
/// </summary>
public sealed class VideoAuthoringResult
{
    private readonly List<AuthoringCommand> commands;
    private readonly List<string> notes;

    /// <summary>Creates a result.</summary>
    /// <param name="outputPath">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="flavour">Which flavour was written.</param>
    /// <param name="ranCommands">The command lines that were run, in order.</param>
    /// <param name="profile">What the streamable profile made of the file, or null when it was not checked.</param>
    /// <param name="mux">The muxer's own summary for a bespoke file, or null for a WebM-profile one.</param>
    /// <param name="runNotes">Anything the run wants the caller to know.</param>
    /// <param name="elapsed">How long the whole run took.</param>
    /// <param name="composedLutTitle">The title of the composed colour table, or null when none was composed.</param>
    /// <param name="composedLutSize">The composed colour table's nodes a side, or 0 when none was composed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ranCommands" /> or <paramref name="runNotes" /> is null.</exception>
    public VideoAuthoringResult(
        string outputPath,
        long sizeInBytes,
        VideoAuthoringFlavour flavour,
        IReadOnlyList<AuthoringCommand> ranCommands,
        StreamableProfileReport profile,
        CbvAuthoringResult mux,
        IReadOnlyList<string> runNotes,
        TimeSpan elapsed,
        string composedLutTitle = null,
        int composedLutSize = 0)
        : this(
            outputPath,
            sizeInBytes,
            flavour,
            ranCommands,
            profile,
            mux,
            runNotes,
            elapsed,
            composedLutTitle,
            composedLutSize,
            null)
    {
    }

    /// <summary>Creates a result that also records where a composed colour table was kept.</summary>
    /// <param name="outputPath">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="flavour">Which flavour was written.</param>
    /// <param name="ranCommands">The command lines that were run, in order.</param>
    /// <param name="profile">What the streamable profile made of the file, or null when it was not checked.</param>
    /// <param name="mux">The muxer's own summary for a bespoke file, or null for a WebM-profile one.</param>
    /// <param name="runNotes">Anything the run wants the caller to know.</param>
    /// <param name="elapsed">How long the whole run took.</param>
    /// <param name="composedLutTitle">The title of the composed colour table, or null when none was composed.</param>
    /// <param name="composedLutSize">The composed colour table's nodes a side, or 0 when none was composed.</param>
    /// <param name="composedLutPath">
    /// Where the composed table was kept, or null when it was a temporary file and has been deleted.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ranCommands" /> or <paramref name="runNotes" /> is null.</exception>
    /// <remarks>
    /// The shorter constructor above is kept as it was, so that a caller written against an earlier version
    /// still compiles: this package's surface only ever grows.
    /// </remarks>
    public VideoAuthoringResult(
        string outputPath,
        long sizeInBytes,
        VideoAuthoringFlavour flavour,
        IReadOnlyList<AuthoringCommand> ranCommands,
        StreamableProfileReport profile,
        CbvAuthoringResult mux,
        IReadOnlyList<string> runNotes,
        TimeSpan elapsed,
        string composedLutTitle,
        int composedLutSize,
        string composedLutPath)
        : this(
            outputPath,
            sizeInBytes,
            flavour,
            ranCommands,
            profile,
            mux,
            runNotes,
            elapsed,
            composedLutTitle,
            composedLutSize,
            composedLutPath,
            null)
    {
    }

    /// <summary>
    /// Creates a result that also carries the managed WebM / Matroska muxer's own summary, for a WebM-profile
    /// file encoded in process.
    /// </summary>
    /// <param name="outputPath">Where the file was written.</param>
    /// <param name="sizeInBytes">How big it is.</param>
    /// <param name="flavour">Which flavour was written.</param>
    /// <param name="ranCommands">The command lines that were run, in order.</param>
    /// <param name="profile">What the streamable profile made of the file, or null when it was not checked.</param>
    /// <param name="mux">The bespoke muxer's own summary for a bespoke or master file, or null.</param>
    /// <param name="runNotes">Anything the run wants the caller to know.</param>
    /// <param name="elapsed">How long the whole run took.</param>
    /// <param name="composedLutTitle">The title of the composed colour table, or null when none was composed.</param>
    /// <param name="composedLutSize">The composed colour table's nodes a side, or 0 when none was composed.</param>
    /// <param name="composedLutPath">
    /// Where the composed table was kept, or null when it was a temporary file and has been deleted.
    /// </param>
    /// <param name="matroskaMux">
    /// The managed WebM / Matroska muxer's own summary for a WebM-profile file encoded in process, or null when
    /// FFmpeg wrote the file or the flavour is another one.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ranCommands" /> or <paramref name="runNotes" /> is null.</exception>
    public VideoAuthoringResult(
        string outputPath,
        long sizeInBytes,
        VideoAuthoringFlavour flavour,
        IReadOnlyList<AuthoringCommand> ranCommands,
        StreamableProfileReport profile,
        CbvAuthoringResult mux,
        IReadOnlyList<string> runNotes,
        TimeSpan elapsed,
        string composedLutTitle,
        int composedLutSize,
        string composedLutPath,
        MatroskaAuthoringResult matroskaMux)
    {
        if (ranCommands == null) throw new ArgumentNullException(nameof(ranCommands));
        if (runNotes == null) throw new ArgumentNullException(nameof(runNotes));

        OutputPath = outputPath;
        SizeInBytes = sizeInBytes;
        Flavour = flavour;
        commands = new List<AuthoringCommand>(ranCommands);
        Profile = profile;
        Mux = mux;
        notes = new List<string>(runNotes);
        Elapsed = elapsed;
        ComposedLutTitle = composedLutTitle;
        ComposedLutSize = composedLutSize;
        ComposedLutPath = composedLutPath;
        MatroskaMux = matroskaMux;
    }

    /// <summary>Where the file was written.</summary>
    public string OutputPath { get; }

    /// <summary>How big the finished file is, in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>Which flavour was written.</summary>
    public VideoAuthoringFlavour Flavour { get; }

    /// <summary>
    /// The command lines that were run, in order: one for a WebM-profile file FFmpeg wrote, two for a bespoke
    /// one; for a file encoded in process, one entry per in-process pass - and, for a WebM-profile file, the mux
    /// last - each with <see cref="AuthoringCommand.RunsFFmpeg" /> false.
    /// </summary>
    public IReadOnlyList<AuthoringCommand> Commands => commands;

    /// <summary>
    /// What the streamable profile made of the finished file, or null when the request switched the check
    /// off.
    /// </summary>
    public StreamableProfileReport Profile { get; }

    /// <summary>
    /// The bespoke muxer's own summary for a bespoke or master file, or null for a WebM-profile one - see
    /// <see cref="MatroskaMux" /> for that.
    /// </summary>
    public CbvAuthoringResult Mux { get; }

    /// <summary>
    /// The managed WebM / Matroska muxer's own summary - tracks, clusters, cue points, chapters, its notices and
    /// its profile report - for a WebM-profile file encoded in process, or null when FFmpeg wrote the file in its
    /// one pass, and for the other two flavours.
    /// </summary>
    public MatroskaAuthoringResult MatroskaMux { get; }

    /// <summary>
    /// Anything the run wants the caller to know that is not a failure - most often the chapter-title
    /// languages the WebM-profile flavour had to collapse. Empty when there was nothing to say.
    /// </summary>
    public IReadOnlyList<string> Notes => notes;

    /// <summary>How long the whole run took, encoding, muxing and validation together.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The title of the ONE colour table the grade chain was folded into, or null when the chain was a single
    /// table used as it stands, or empty altogether.
    /// </summary>
    /// <remarks>
    /// The composed table is a temporary file and is deleted with the rest of them unless the request asked
    /// for it to be kept - see <see cref="ComposedLutPath" />. What is always recorded is the fact that
    /// composing happened and what it produced, which is what this and <see cref="ComposedLutSize" /> say.
    /// </remarks>
    public string ComposedLutTitle { get; }

    /// <summary>The composed colour table's nodes a side, or 0 when no table was composed.</summary>
    public int ComposedLutSize { get; }

    /// <summary>
    /// Where the effective colour table was KEPT - the table this file was graded with - or null when the
    /// request asked to keep nothing, or had no grade at all.
    /// </summary>
    /// <remarks>
    /// It is set by <c>request.Video.ComposedLutPath</c> and it is populated whenever that path is set and
    /// the request carried a grade. When the chain was COMPOSED the file here is the very file FFmpeg's
    /// lookup read, not a copy written afterwards. When ONE table was used at full strength FFmpeg read the
    /// caller's own file and this is a byte-for-byte copy of it - <see cref="ComposedLutSize" /> is 0 and
    /// <see cref="ComposedLutTitle" /> is null in that case, which is how the two are told apart.
    /// </remarks>
    public string ComposedLutPath { get; }

    /// <summary>
    /// True when the file was checked and passes the streamable profile. False both when it fails and when
    /// it was never checked - <see cref="Profile" /> tells the two apart.
    /// </summary>
    public bool PassesProfile => Profile != null && Profile.Passes;

    /// <inheritdoc />
    public override string ToString() =>
        OutputPath + ": " + SizeInBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        + " bytes, " + Flavour + ", "
        + (Profile == null ? "profile not checked" : Profile.Verdict)
        + (notes.Count == 0 ? string.Empty : ", " + notes.Count + " note(s)");
}
