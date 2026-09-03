using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Finds the golden container corpus, wherever the test assembly happens to be running from.
/// </summary>
/// <remarks>
/// The assets are copied beside the test assembly by the project file. The walk up the directory tree is the
/// fallback for a run that did not copy them - a single-test debugging run from an editor, typically.
/// </remarks>
public static class TestAssets
{
    private static readonly string Directory = Find();

    /// <summary>The folder holding the golden corpus.</summary>
    public static string Root => Directory;

    /// <summary>Resolves an asset by name, skipping the test when the corpus has not been generated.</summary>
    /// <param name="name">The asset's file name.</param>
    /// <returns>The full path of the asset.</returns>
    public static string Path(string name)
    {
        string path = System.IO.Path.Combine(Directory, name);
        Assert.SkipUnless(File.Exists(path), $"The golden asset '{name}' is not present at '{path}'.");
        return path;
    }

    /// <summary>Reports whether an asset is present, without skipping the test.</summary>
    /// <param name="name">The asset's file name.</param>
    /// <returns>True when the file exists.</returns>
    public static bool Exists(string name) => File.Exists(System.IO.Path.Combine(Directory, name));

    private static readonly object TemporaryGate = new object();
    private static readonly List<string> TemporaryDirectories = new List<string>();

    /// <summary>Creates a directory under the system temporary folder for a test to write into.</summary>
    /// <param name="label">A short label that appears in the folder's name.</param>
    /// <returns>The new directory's path.</returns>
    /// <remarks>
    /// Every folder made here is remembered and deleted when the test run ends, by
    /// <see cref="TemporaryDirectoryCleanup" />. A test may still delete its own folder sooner; most of the
    /// synthetic-media tests do not, and before the sweep existed a single run left about seventy of them
    /// behind under the system temporary folder (found 2026-09-02).
    /// </remarks>
    public static string CreateTemporaryDirectory(string label)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"codebrix-videoplayback-{label}-{Guid.NewGuid():N}");

        System.IO.Directory.CreateDirectory(path);
        lock (TemporaryGate) TemporaryDirectories.Add(path);
        return path;
    }

    /// <summary>Deletes every folder <see cref="CreateTemporaryDirectory" /> made, as far as it can.</summary>
    /// <remarks>
    /// Best effort: a folder a test already removed is simply not there, and one that cannot be removed - a
    /// file still open, most likely - is left for the system's own sweep rather than turned into a failure.
    /// </remarks>
    public static void DeleteTemporaryDirectories()
    {
        string[] paths;
        lock (TemporaryGate)
        {
            paths = TemporaryDirectories.ToArray();
            TemporaryDirectories.Clear();
        }

        foreach (string path in paths)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Find()
    {
        string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "assets");
        if (System.IO.Directory.Exists(beside)) return beside;

        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = System.IO.Path.Combine(directory.FullName, "tests", "assets");
            if (System.IO.Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return beside;
    }
}
