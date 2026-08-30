using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;

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
    }

    /// <summary>Where the file was written.</summary>
    public string OutputPath { get; }

    /// <summary>How big the finished file is, in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>Which flavour was written.</summary>
    public VideoAuthoringFlavour Flavour { get; }

    /// <summary>
    /// The command lines that were run, in order: one for a WebM-profile file, two for a bespoke one.
    /// </summary>
    public IReadOnlyList<AuthoringCommand> Commands => commands;

    /// <summary>
    /// What the streamable profile made of the finished file, or null when the request switched the check
    /// off.
    /// </summary>
    public StreamableProfileReport Profile { get; }

    /// <summary>The muxer's own summary for a bespoke file, or null for a WebM-profile one.</summary>
    public CbvAuthoringResult Mux { get; }

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
    /// The composed table itself is a temporary file and is deleted with the rest of them; what is worth
    /// keeping is the fact that composing happened and what it produced, which is what this and
    /// <see cref="ComposedLutSize" /> record.
    /// </remarks>
    public string ComposedLutTitle { get; }

    /// <summary>The composed colour table's nodes a side, or 0 when no table was composed.</summary>
    public int ComposedLutSize { get; }

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
