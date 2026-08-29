using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Finds the ".cube" corpus under <c>tests/assets/LUTs</c>, wherever the test assembly is running from.
/// </summary>
/// <remarks>
/// The corpus is seventeen megabytes of lookup tables - nine collected from six licence-cleared open
/// projects and twelve written by the generator beside them - so a checkout need not have it. Every test
/// that reads it skips itself, naming the folder, when it is not present, exactly as the authoring-corpus
/// tests do.
/// </remarks>
public static class LutTestAssets
{
    private static readonly string RootFolder = Find();

    /// <summary>The folder holding the corpus, whether or not it exists.</summary>
    public static string Root => RootFolder;

    /// <summary>True when the corpus has been fetched into this checkout.</summary>
    public static bool IsPresent => Directory.Exists(RootFolder);

    /// <summary>
    /// Every ".cube" file the corpus holds that a reader must ACCEPT - everything but the <c>invalid</c>
    /// folder - path relative to the corpus root, sorted.
    /// </summary>
    public static TheoryData<string> EveryCubeFile => Enumerate(null);

    /// <summary>Every ".cube" file under <c>invalid</c>, which no reader should accept.</summary>
    public static TheoryData<string> EveryInvalidCubeFile => Enumerate("invalid");

    /// <summary>Resolves a corpus file, skipping the test when the corpus is not there.</summary>
    /// <param name="relativePath">The path under the corpus root, with forward slashes.</param>
    /// <returns>The full path of the file.</returns>
    public static string Path(string relativePath)
    {
        string path = System.IO.Path.Combine(
            RootFolder,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        Assert.SkipUnless(
            File.Exists(path),
            $"The .cube corpus file '{relativePath}' is not present at '{path}'.");

        return path;
    }

    /// <summary>Skips the running test when the corpus - or one folder of it - is absent.</summary>
    /// <param name="folder">A folder under the corpus root, or null for the root itself.</param>
    public static void SkipWhenAbsent(string folder)
    {
        string path = folder == null ? RootFolder : System.IO.Path.Combine(RootFolder, folder);

        Assert.SkipUnless(
            Directory.Exists(path),
            $"The .cube corpus is not present at '{path}'; see tests/assets/LUTs/README.txt.");
    }

    private static TheoryData<string> Enumerate(string folder)
    {
        TheoryData<string> data = new TheoryData<string>();

        string root = folder == null ? RootFolder : System.IO.Path.Combine(RootFolder, folder);
        if (!Directory.Exists(root))
        {
            // A theory with no rows fails; one row that skips itself reports the absence instead.
            data.Add(folder == null ? "(the corpus is absent)" : $"{folder}/(the folder is absent)");
            return data;
        }

        List<string> found = new List<string>(
            Directory.GetFiles(root, "*.cube", SearchOption.AllDirectories));

        found.Sort(StringComparer.Ordinal);

        foreach (string file in found)
        {
            // The invalid folder is the negative corpus and has its own theory.
            if (folder == null && IsUnderInvalid(root, file)) continue;

            data.Add(System.IO.Path.GetRelativePath(RootFolder, file).Replace(
                System.IO.Path.DirectorySeparatorChar,
                '/'));
        }

        if (data.Count == 0)
        {
            data.Add(folder == null ? "(the corpus is empty)" : $"{folder}/(the folder is empty)");
        }

        return data;
    }

    private static bool IsUnderInvalid(string root, string file) =>
        System.IO.Path.GetRelativePath(root, file)
            .Replace(System.IO.Path.DirectorySeparatorChar, '/')
            .StartsWith("invalid/", StringComparison.Ordinal);

    private static string Find()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = System.IO.Path.Combine(directory.FullName, "tests", "assets", "LUTs");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "LUTs");
    }
}
