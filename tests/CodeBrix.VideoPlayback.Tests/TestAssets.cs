using System;
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

    /// <summary>Creates a directory under the system temporary folder for a test to write into.</summary>
    /// <param name="label">A short label that appears in the folder's name.</param>
    /// <returns>The new directory's path.</returns>
    public static string CreateTemporaryDirectory(string label)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"codebrix-videoplayback-{label}-{Guid.NewGuid():N}");

        System.IO.Directory.CreateDirectory(path);
        return path;
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
