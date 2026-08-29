using System;
using System.IO;

namespace SimpleCbxVideoPlayer.SkiaVideo.Assets;

/// <summary>
/// Finds the sample corpus that ships with the CodeBrix.VideoPlayback repository.
/// </summary>
/// <remarks>
/// This is a DEVELOPMENT-REPOSITORY sample: it plays the video and lookup-table files that live under the
/// repository's own <c>tests/assets</c> folder rather than carrying media of its own. The folders are found
/// by walking up from the running application's folder until a directory holding
/// <c>tests/assets/authoring</c> appears, so the sample works from any head's <c>bin</c> folder without a
/// configuration file. A published copy of the application, moved away from the repository, finds nothing -
/// which is why the user interface says so instead of failing.
/// </remarks>
public static class RepositoryAssets
{
    /// <summary>The video corpus, relative to the repository root.</summary>
    public const string AuthoringRelativePath = "tests/assets/authoring";

    /// <summary>The colour-lookup-table corpus, relative to the repository root.</summary>
    public const string LutsRelativePath = "tests/assets/LUTs";

    /// <summary>How many folders up from the starting folder the search is willing to look.</summary>
    public const int MaximumSearchDepth = 12;

    /// <summary>Finds the repository root by walking up from the running application's folder.</summary>
    /// <returns>The repository root, or null when no folder above the application holds the corpus.</returns>
    public static string FindRepositoryRoot() => FindRepositoryRoot(AppContext.BaseDirectory);

    /// <summary>Finds the repository root by walking up from a folder of your choosing.</summary>
    /// <param name="startFolder">The folder to start from; its own ancestors are searched in turn.</param>
    /// <returns>The repository root, or null when no folder at or above startFolder holds the corpus.</returns>
    public static string FindRepositoryRoot(string startFolder)
    {
        if (string.IsNullOrWhiteSpace(startFolder)) { return null; }

        DirectoryInfo folder = new DirectoryInfo(startFolder);

        for (var depth = 0; folder != null && depth <= MaximumSearchDepth; depth++)
        {
            if (Directory.Exists(GetAuthoringFolder(folder.FullName))) { return folder.FullName; }

            folder = folder.Parent;
        }

        return null;
    }

    /// <summary>Gives the video-corpus folder inside a repository root.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The full path of the authoring folder, whether or not it exists.</returns>
    public static string GetAuthoringFolder(string repositoryRoot) =>
        Combine(repositoryRoot, AuthoringRelativePath);

    /// <summary>Gives the lookup-table folder inside a repository root.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The full path of the LUTs folder, whether or not it exists.</returns>
    public static string GetLutsFolder(string repositoryRoot) => Combine(repositoryRoot, LutsRelativePath);

    private static string Combine(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root)) { throw new ArgumentException("A repository root is required.", nameof(root)); }

        var parts = relativePath.Split('/');
        var path = root;

        foreach (var part in parts) { path = Path.Combine(path, part); }

        return path;
    }
}
