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
    /// <returns>What the chain reduced to.</returns>
    internal static ResolvedLutChain Resolve(
        IList<AuthoringLutInput> luts,
        string outputPath,
        string temporaryFolder,
        bool write,
        IList<string> notes)
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

            return new ResolvedLutChain { FilterPath = applied[0].Path };
        }

        string composedPath = ComposedPathFor(outputPath, temporaryFolder);
        ResolvedLutChain resolved = new ResolvedLutChain
        {
            FilterPath = composedPath,
            TemporaryPath = composedPath,
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

        Directory.CreateDirectory(temporaryFolder);
        CubeLutFile.Write(effective, composedPath, title);

        resolved.Title = title;
        resolved.Size = effective.Size;

        notes?.Add(
            "the colour-grade chain of " + applied.Count.ToString(CultureInfo.InvariantCulture)
            + " table(s) was composed into one " + effective.Size.ToString(CultureInfo.InvariantCulture)
            + "-node table titled \"" + title + "\", which is the one lookup FFmpeg ran.");

        return resolved;
    }

    private static string BuildTitle(IReadOnlyList<AuthoringLutInput> applied)
    {
        List<string> parts = new List<string>(applied.Count);
        foreach (AuthoringLutInput lut in applied) parts.Add(lut.ToString());

        return string.Join(" then ", parts);
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
