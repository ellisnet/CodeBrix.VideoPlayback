namespace SimpleCbxVideoPlayer.SkiaVideo.Assets;

/// <summary>One ".cube" colour lookup table found in the sample corpus.</summary>
public sealed class LutCatalogEntry
{
    /// <summary>Creates the entry.</summary>
    /// <param name="groupName">The corpus group the file belongs to: "generated" or "found".</param>
    /// <param name="fileName">The file's own name, with its extension.</param>
    /// <param name="fullPath">The full path of the file.</param>
    /// <param name="title">The file's TITLE line, or null when it carries none.</param>
    public LutCatalogEntry(string groupName, string fileName, string fullPath, string title)
    {
        GroupName = groupName;
        FileName = fileName;
        FullPath = fullPath;
        Title = title;
    }

    /// <summary>The corpus group the file belongs to: "generated" or "found".</summary>
    public string GroupName { get; }

    /// <summary>The file's own name, with its extension.</summary>
    public string FileName { get; }

    /// <summary>The full path of the file.</summary>
    public string FullPath { get; }

    /// <summary>The file's TITLE line, or null when it carries none.</summary>
    public string Title { get; }

    /// <summary>What the list shows: the table's own title when it has one, its file name when it does not.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? FileName : Title;

    /// <inheritdoc />
    public override string ToString() => $"{GroupName}/{FileName}";
}
