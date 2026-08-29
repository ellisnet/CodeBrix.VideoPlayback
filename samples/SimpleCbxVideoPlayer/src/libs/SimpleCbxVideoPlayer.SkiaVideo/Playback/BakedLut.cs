namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>What was written when a chain of lookup tables was baked into one file.</summary>
public sealed class BakedLut
{
    /// <summary>Creates the record.</summary>
    /// <param name="filePath">Where the ".cube" file was written.</param>
    /// <param name="title">The TITLE line the file carries.</param>
    /// <param name="size">How many nodes a side the baked table has.</param>
    /// <param name="tableCount">How many tables the chain held.</param>
    public BakedLut(string filePath, string title, int size, int tableCount)
    {
        FilePath = filePath;
        Title = title;
        Size = size;
        TableCount = tableCount;
    }

    /// <summary>Where the ".cube" file was written.</summary>
    public string FilePath { get; }

    /// <summary>The TITLE line the file carries.</summary>
    public string Title { get; }

    /// <summary>How many nodes a side the baked table has.</summary>
    public int Size { get; }

    /// <summary>How many tables the chain held.</summary>
    public int TableCount { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{TableCount} table(s) baked to a {Size}-node table: {FilePath}";
}
