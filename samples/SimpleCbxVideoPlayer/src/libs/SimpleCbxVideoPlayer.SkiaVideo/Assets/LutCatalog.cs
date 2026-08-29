using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimpleCbxVideoPlayer.SkiaVideo.Assets;

/// <summary>
/// Lists the ".cube" colour lookup tables the sample offers, and reads the title out of one.
/// </summary>
/// <remarks>
/// Two of the corpus's three folders are read, in this order: <c>generated/</c> then <c>found/</c>, each
/// searched recursively because <c>found/</c> keeps one folder per upstream project. The third folder,
/// <c>invalid/</c>, is NEVER read: the files in it are malformed on purpose, as negative test fixtures, and
/// offering one would offer a parse failure.
/// </remarks>
public static class LutCatalog
{
    /// <summary>The corpus groups that are read, in the order the list shows them.</summary>
    public static readonly IReadOnlyList<string> GroupFolderNames = ["generated", "found"];

    /// <summary>The corpus folder that is never read - deliberately malformed negative fixtures.</summary>
    public const string ExcludedFolderName = "invalid";

    /// <summary>The only lookup-table format this family reads.</summary>
    public const string CubeExtension = ".cube";

    /// <summary>How many lines of a file are read while looking for its TITLE.</summary>
    public const int TitleSearchLineLimit = 40;

    /// <summary>Reads the lookup-table corpus.</summary>
    /// <param name="lutsFolder">The <c>tests/assets/LUTs</c> folder.</param>
    /// <returns>
    /// Every ".cube" file under <c>generated/</c> and <c>found/</c>, grouped in that order and sorted by
    /// display name within each group; an empty list when the folder is missing.
    /// </returns>
    public static IReadOnlyList<LutCatalogEntry> Scan(string lutsFolder)
    {
        List<LutCatalogEntry> entries = [];

        if (string.IsNullOrWhiteSpace(lutsFolder) || !Directory.Exists(lutsFolder)) { return entries; }

        foreach (var groupName in GroupFolderNames)
        {
            var groupFolder = Path.Combine(lutsFolder, groupName);

            if (!Directory.Exists(groupFolder)) { continue; }

            List<LutCatalogEntry> group = [];

            foreach (var file in Directory.GetFiles(groupFolder, "*" + CubeExtension, SearchOption.AllDirectories))
            {
                group.Add(new LutCatalogEntry(groupName, Path.GetFileName(file), file, ReadTitle(file)));
            }

            entries.AddRange(group.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        return entries;
    }

    /// <summary>The group an entry gets when it names a file from outside the corpus.</summary>
    public const string ExternalGroupName = "external";

    /// <summary>Describes a ".cube" file that is not part of the corpus, so that it can be applied too.</summary>
    /// <param name="filePath">The file to describe.</param>
    /// <returns>An entry for the file, or null when there is no readable ".cube" file at that path.</returns>
    /// <remarks>
    /// This is what lets a baked chain be fed straight back in - the round trip that proves a baked table
    /// reproduces the chain it came from - without the file having to live in the corpus first.
    /// </remarks>
    public static LutCatalogEntry CreateExternalEntry(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { return null; }

        var trimmed = filePath.Trim();

        if (!string.Equals(Path.GetExtension(trimmed), CubeExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(trimmed)) { return null; }

        return new LutCatalogEntry(ExternalGroupName, Path.GetFileName(trimmed), trimmed, ReadTitle(trimmed));
    }

    /// <summary>Finds the table a name asks for, the way the command line and a search box would.</summary>
    /// <param name="entries">The catalogue to look in.</param>
    /// <param name="name">A file name, or part of a file name or title.</param>
    /// <returns>
    /// The index of the table in entries, or -1 when nothing matches. An exact file name wins; failing
    /// that, the first entry whose file name or title CONTAINS the text.
    /// </returns>
    /// <remarks>
    /// This returns an index rather than an entry so that a caller holding a parallel list of its own -
    /// a list of rows in a panel, say - can find the row that goes with the table.
    /// </remarks>
    public static int MatchIndex(IReadOnlyList<LutCatalogEntry> entries, string name)
    {
        if (entries == null || string.IsNullOrWhiteSpace(name)) { return -1; }

        var wanted = name.Trim();

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] != null
                && string.Equals(entries[index].FileName, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        for (var index = 0; index < entries.Count; index++)
        {
            LutCatalogEntry entry = entries[index];

            if (entry == null) { continue; }

            if (entry.FileName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || entry.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Reads the TITLE line out of a ".cube" file without parsing the table.</summary>
    /// <param name="cubeFilePath">The file to read.</param>
    /// <returns>The title, or null when the file has none, is unreadable, or names no title in its header.</returns>
    /// <remarks>
    /// The catalogue shows a title for two dozen files and a 65-node table is a megabyte of numbers, so the
    /// header is read on its own rather than through the full parser. The whole file is still parsed - by
    /// the presenter's effect chain - when a table is actually applied.
    /// </remarks>
    public static string ReadTitle(string cubeFilePath)
    {
        if (string.IsNullOrWhiteSpace(cubeFilePath) || !File.Exists(cubeFilePath)) { return null; }

        try
        {
            using StreamReader reader = new StreamReader(cubeFilePath);

            for (var line = 0; line < TitleSearchLineLimit; line++)
            {
                var text = reader.ReadLine();

                if (text == null) { break; }

                var title = ParseTitle(text);

                if (title != null) { return title; }
            }
        }
        catch (IOException)
        {
            //A catalogue is a convenience: a file that will not open simply has no title.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Reads a title out of one line of a ".cube" file.</summary>
    /// <param name="line">The line to read.</param>
    /// <returns>The title the line declares, or null when it declares none.</returns>
    public static string ParseTitle(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) { return null; }

        var trimmed = line.Trim();

        if (!trimmed.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase)) { return null; }

        var remainder = trimmed.Substring("TITLE".Length).Trim();

        if (remainder.Length == 0) { return null; }

        if (remainder[0] == '"')
        {
            var closing = remainder.IndexOf('"', 1);

            return closing > 1 ? remainder.Substring(1, closing - 1) : null;
        }

        //An unquoted title is not the IRIDAS spelling, but files in the wild carry it; take it to the comment.
        var comment = remainder.IndexOf('#');

        if (comment >= 0) { remainder = remainder.Substring(0, comment); }

        remainder = remainder.Trim();

        return remainder.Length == 0 ? null : remainder;
    }
}
