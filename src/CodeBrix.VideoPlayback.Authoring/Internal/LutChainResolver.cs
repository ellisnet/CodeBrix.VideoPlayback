using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Color.Luts;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Reduces an authoring request's colour-grade chain to the ONE ".cube" file FFmpeg's lut3d filter takes.
/// </summary>
/// <remarks>
/// <para>
/// One table applied at 100 percent is handed over as it stands - there is nothing to compose and no reason
/// to rewrite a file. Anything else is folded by the core's <see cref="LutComposer" />, with its defaults
/// (tetrahedral sampling, the automatic output size, the 0-to-1 domain), and written to a temporary file.
/// </para>
/// <para>
/// A request that asks for the effective table to be KEPT gets one either way, and the two ways differ in a
/// way the caller can see: when the chain composes, the kept file IS the file FFmpeg's lookup read; when one
/// table is used as it stands, the kept file is a byte-for-byte COPY of that table and the command line goes
/// on naming the caller's own file. Either way the kept path holds the table this video was graded with.
/// </para>
/// <para>
/// That composer is the same code the playback presenter uses to fold ITS chain, which is the whole point:
/// a grade baked into a file and the same grade applied live are the same arithmetic, so they are the same
/// colour.
/// </para>
/// </remarks>
internal static class LutChainResolver
{
    /// <summary>Works out the temporary path a composed table would be written to.</summary>
    /// <param name="outputPath">The file being authored.</param>
    /// <param name="temporaryFolder">The folder intermediate files go in.</param>
    /// <returns>The path, which is deterministic so that a dry run and a real run render the same command.</returns>
    internal static string ComposedPathFor(string outputPath, string temporaryFolder)
    {
        string name = string.IsNullOrWhiteSpace(outputPath)
            ? "authoring"
            : Path.GetFileNameWithoutExtension(outputPath);

        return Path.Combine(temporaryFolder, name + ".effective.cube");
    }

    /// <summary>Reduces the chain, writing a composed table only when <paramref name="write" /> is true.</summary>
    /// <param name="luts">The chain, in order.</param>
    /// <param name="outputPath">The file being authored, which names the temporary table.</param>
    /// <param name="temporaryFolder">The folder intermediate files go in.</param>
    /// <param name="write">
    /// True to actually read the tables and write the composed one; false to render the path only, which is
    /// what a dry run wants.
    /// </param>
    /// <param name="notes">Collects a line describing what was composed.</param>
    /// <param name="keepAtPath">
    /// Where the effective table is to be KEPT, or null to write a composed one beside the other intermediate
    /// files and delete it with them. A COMPOSED table is written to that exact path and the lookup reads it
    /// from there, so what is left on the disk is the file the encode consumed; a single table at 100 percent
    /// is COPIED there instead, because FFmpeg reads the caller's own file in that case.
    /// </param>
    /// <returns>What the chain reduced to.</returns>
    internal static ResolvedLutChain Resolve(
        IList<AuthoringLutInput> luts,
        string outputPath,
        string temporaryFolder,
        bool write,
        IList<string> notes,
        string keepAtPath = null)
    {
        List<AuthoringLutInput> applied = new List<AuthoringLutInput>();

        if (luts != null)
        {
            foreach (AuthoringLutInput lut in luts)
            {
                if (lut != null && lut.HasEffect) applied.Add(lut);
            }
        }

        if (applied.Count == 0) return ResolvedLutChain.None;

        if (applied.Count == 1 && applied[0].ApplyAtPercent >= 100d)
        {
            // A dry run touches no disk at all, so the file is only insisted on when it is about to be read.
            if (write) RequireFile(applied[0].Path);

            ResolvedLutChain single = new ResolvedLutChain { FilterPath = applied[0].Path };

            if (string.IsNullOrWhiteSpace(keepAtPath)) return single;

            // NOTHING IS COMPOSED HERE - FFmpeg reads the caller's own table as it stands, and that is still
            // what the command line names. But the kept path means "the table this file was graded with", and
            // that is exactly what this one table is, so a COPY of it goes there. The copy is what makes the
            // property uniformly populated whenever any grade exists.
            string copyPath = Path.GetFullPath(keepAtPath);
            single.KeptPath = copyPath;

            if (!write) return single;

            Directory.CreateDirectory(FolderOf(copyPath));

            bool alreadyThere = string.Equals(
                copyPath, Path.GetFullPath(applied[0].Path), StringComparison.Ordinal);

            if (!alreadyThere) File.Copy(applied[0].Path, copyPath, true);

            notes?.Add(
                "the colour-grade chain was the single table '" + applied[0].Path
                + "' at 100 percent, which FFmpeg read as it stands; "
                + (alreadyThere
                    ? "it is already the file the request asked to keep."
                    : "a COPY of it was KEPT at '" + copyPath + "'."));

            return single;
        }

        // A kept table is written where the request asked for it and read from there, so the file left
        // behind is the very file the lookup consumed rather than a copy of it.
        bool keep = !string.IsNullOrWhiteSpace(keepAtPath);
        string composedPath = keep
            ? Path.GetFullPath(keepAtPath)
            : ComposedPathFor(outputPath, temporaryFolder);

        ResolvedLutChain resolved = new ResolvedLutChain
        {
            FilterPath = composedPath,
            TemporaryPath = keep ? null : composedPath,
            KeptPath = keep ? composedPath : null,
            WasComposed = true,
        };

        if (!write) return resolved;

        List<LutLayer> layers = new List<LutLayer>(applied.Count);
        foreach (AuthoringLutInput lut in applied)
        {
            RequireFile(lut.Path);
            layers.Add(LutLayer.FromCubeFile(lut.Path, lut.ApplyAtPercent));
        }

        Lut3D effective = LutComposer.Compose(layers);
        string title = BuildTitle(applied);

        Directory.CreateDirectory(keep ? FolderOf(composedPath) : temporaryFolder);
        CubeLutFile.Write(effective, composedPath, title);

        resolved.Title = title;
        resolved.Size = effective.Size;

        notes?.Add(
            "the colour-grade chain of " + applied.Count.ToString(CultureInfo.InvariantCulture)
            + " table(s) was composed into one " + effective.Size.ToString(CultureInfo.InvariantCulture)
            + "-node table titled \"" + title + "\", which is the one lookup FFmpeg ran."
            + (keep ? " It was KEPT at '" + composedPath + "'." : string.Empty));

        return resolved;
    }

    private static string BuildTitle(IReadOnlyList<AuthoringLutInput> applied)
    {
        List<string> parts = new List<string>(applied.Count);
        foreach (AuthoringLutInput lut in applied) parts.Add(lut.ToString());

        return string.Join(" then ", parts);
    }

    // A kept table may name a folder that does not exist yet - "grades/effective.cube" beside an output that
    // is itself about to be created - and a bare file name has no folder at all.
    private static string FolderOf(string path)
    {
        string folder = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(folder) ? Directory.GetCurrentDirectory() : folder;
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new VideoAuthoringException(
                "There is no colour lookup table at '" + path + "'. A grade chain names \".cube\" files that "
                + "already exist; nothing is downloaded and nothing is generated.");
        }
    }
}
