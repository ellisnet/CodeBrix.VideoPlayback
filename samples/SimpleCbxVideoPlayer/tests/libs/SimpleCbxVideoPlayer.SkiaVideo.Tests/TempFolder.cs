using System;
using System.IO;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

/// <summary>A throw-away folder that deletes itself, for the scanner tests.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SimpleCbxVideoPlayer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFolder(params string[] parts)
    {
        var folder = Path;

        foreach (var part in parts) { folder = System.IO.Path.Combine(folder, part); }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public string CreateFile(string folder, string fileName, string contents = "")
    {
        var file = System.IO.Path.Combine(folder, fileName);
        File.WriteAllText(file, contents);
        return file;
    }

    /// <summary>Writes a 2-node identity cube, which is the smallest table the reader accepts.</summary>
    public string CreateCube(string folder, string fileName, string title)
    {
        var header = title == null ? string.Empty : $"TITLE \"{title}\"\n";
        var body = header + "LUT_3D_SIZE 2\n"
            + "0.0 0.0 0.0\n1.0 0.0 0.0\n0.0 1.0 0.0\n1.0 1.0 0.0\n"
            + "0.0 0.0 1.0\n1.0 0.0 1.0\n0.0 1.0 1.0\n1.0 1.0 1.0\n";

        return CreateFile(folder, fileName, body);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, true); }
        }
        catch (IOException)
        {
            //A temporary folder that will not delete is not a test failure.
        }
    }
}
