using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Where the test inputs are, and where a test may write.
/// </summary>
/// <remarks>
/// The caption, chapter and lookup-table inputs are copied beside the test assembly by the project file. The
/// MEDIA is not: every clip these tests author from is generated on the spot from FFmpeg's own synthetic
/// sources, so a checkout that has never built the sample-video corpus still runs the whole suite.
/// </remarks>
public static class AuthoringTestAssets
{
    /// <summary>The folder holding the caption, chapter and lookup-table inputs.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "assets");

    /// <summary>The folder holding the ".cube" lookup tables.</summary>
    public static string LutFolder { get; } = Path.Combine(Folder, "LUTs");

    /// <summary>Resolves one of the copied input files.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>Its full path.</returns>
    public static string Path_(string name) => Path.Combine(Folder, name);

    /// <summary>Resolves one of the copied lookup tables.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>Its full path.</returns>
    public static string Lut(string name) => Path.Combine(LutFolder, name);

    /// <summary>Makes a private folder for one test to write into.</summary>
    /// <param name="name">A short name that says which test owns it.</param>
    /// <returns>The folder, created and empty.</returns>
    public static string NewWorkFolder(string name)
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "codebrix-videoplayback-authoring-tests",
            name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        Directory.CreateDirectory(folder);
        return folder;
    }
}
