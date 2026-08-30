using SimpleCbxVideoPlayer.SkiaVideo.Assets;

namespace SimpleCbxVideoPlayer.ViewModels;

/// <summary>One row of the video drop-down.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public class VideoListItem
{
    /// <summary>Creates the row from a corpus entry.</summary>
    /// <param name="item">The file the corpus scan found.</param>
    public VideoListItem(VideoCorpusItem item)
    {
        DisplayName = item.DisplayName;
        FolderName = item.FolderName;
        FileName = item.FileName;
        FullPath = item.FullPath;
    }

    /// <summary>What the drop-down shows: the corpus folder and the file name.</summary>
    public string DisplayName { get; }

    /// <summary>The corpus folder the file came from.</summary>
    public string FolderName { get; }

    /// <summary>The file's own name.</summary>
    public string FileName { get; }

    /// <summary>The full path of the file.</summary>
    public string FullPath { get; }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
