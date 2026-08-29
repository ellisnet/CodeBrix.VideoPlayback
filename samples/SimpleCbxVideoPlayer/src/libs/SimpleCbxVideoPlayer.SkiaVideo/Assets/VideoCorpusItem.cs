namespace SimpleCbxVideoPlayer.SkiaVideo.Assets;

/// <summary>One playable file found in the sample corpus.</summary>
public sealed class VideoCorpusItem
{
    /// <summary>Creates the item.</summary>
    /// <param name="folderName">The corpus sub-folder the file was found in, such as "MKV".</param>
    /// <param name="fileName">The file's own name, with its extension.</param>
    /// <param name="fullPath">The full path of the file.</param>
    public VideoCorpusItem(string folderName, string fileName, string fullPath)
    {
        FolderName = folderName;
        FileName = fileName;
        FullPath = fullPath;
    }

    /// <summary>The corpus sub-folder the file was found in.</summary>
    public string FolderName { get; }

    /// <summary>The file's own name, with its extension.</summary>
    public string FileName { get; }

    /// <summary>The full path of the file.</summary>
    public string FullPath { get; }

    /// <summary>What the drop-down shows: the folder and the file name.</summary>
    public string DisplayName => $"{FolderName}/{FileName}";

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
