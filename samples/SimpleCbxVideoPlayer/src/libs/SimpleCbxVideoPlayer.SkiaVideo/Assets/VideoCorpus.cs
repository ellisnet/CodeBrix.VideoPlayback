using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimpleCbxVideoPlayer.SkiaVideo.Assets;

/// <summary>
/// Lists every video in the sample corpus that this application can actually play.
/// </summary>
/// <remarks>
/// The rule is deliberately a rule rather than a list of folders: EVERY sub-folder of the corpus is read,
/// except the ones named in <see cref="ExcludedFolderNames" />, and every file whose extension is in
/// <see cref="PlayableExtensions" /> is offered. So MKV/, WebM/ and CodeBrix-Mode1/ are listed today, MP4/
/// is not - nothing in this family reads an MP4 - and a CodeBrix-Mode2/ folder added tomorrow appears in
/// the drop-down without a line of code changing.
/// </remarks>
public static class VideoCorpus
{
    /// <summary>The extensions this application can open: Matroska, WebM and the bespoke container.</summary>
    public static readonly IReadOnlyList<string> PlayableExtensions = [".mkv", ".webm", ".cbv"];

    /// <summary>Corpus folders that are skipped whatever they hold.</summary>
    /// <remarks>
    /// MP4 is the corpus's ORIGINALS folder - H.264 video in an ISOBMFF container, which this family reads
    /// no part of. Offering those files would offer a failure.
    /// </remarks>
    public static readonly IReadOnlyList<string> ExcludedFolderNames = ["MP4"];

    /// <summary>Reads the corpus.</summary>
    /// <param name="authoringFolder">The <c>tests/assets/authoring</c> folder.</param>
    /// <returns>
    /// Every playable file, ordered by folder and then by file name; an empty list when the folder is
    /// missing or holds nothing playable.
    /// </returns>
    public static IReadOnlyList<VideoCorpusItem> Scan(string authoringFolder)
    {
        List<VideoCorpusItem> items = [];

        if (string.IsNullOrWhiteSpace(authoringFolder) || !Directory.Exists(authoringFolder)) { return items; }

        foreach (var folder in Directory.GetDirectories(authoringFolder))
        {
            var folderName = Path.GetFileName(folder);

            if (IsExcludedFolder(folderName)) { continue; }

            foreach (var file in Directory.GetFiles(folder))
            {
                if (!IsPlayable(file)) { continue; }

                items.Add(new VideoCorpusItem(folderName, Path.GetFileName(file), file));
            }
        }

        return items
            .OrderBy(item => item.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Says whether a folder is one the corpus scan steps over.</summary>
    /// <param name="folderName">The folder's own name, without a path.</param>
    /// <returns>True when the folder is excluded.</returns>
    public static bool IsExcludedFolder(string folderName) =>
        folderName != null
        && ExcludedFolderNames.Any(excluded => string.Equals(excluded, folderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Says whether a file name carries an extension this application can open.</summary>
    /// <param name="fileName">A file name or path.</param>
    /// <returns>True when the extension is playable.</returns>
    public static bool IsPlayable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) { return false; }

        var extension = Path.GetExtension(fileName);

        return PlayableExtensions.Any(playable => string.Equals(playable, extension, StringComparison.OrdinalIgnoreCase));
    }
}
