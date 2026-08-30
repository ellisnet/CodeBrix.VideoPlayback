using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>A private folder for one test, deleted when the test is done with it.</summary>
public sealed class WorkFolder : IDisposable
{
    /// <summary>Creates and owns a new folder.</summary>
    /// <param name="name">A short name that says which test owns it.</param>
    public WorkFolder(string name)
    {
        Path = AuthoringTestAssets.NewWorkFolder(name);
    }

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>Resolves a file inside the folder.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>Its full path.</returns>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Deletes the folder and everything in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // A test that leaves a file locked is not a reason to fail the test; the system sweeps its own
            // temporary folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
