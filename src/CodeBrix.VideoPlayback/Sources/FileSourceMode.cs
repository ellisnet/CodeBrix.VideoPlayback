namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// How a local file should be read.
/// </summary>
public enum FileSourceMode
{
    /// <summary>
    /// Read it as playback needs it, seeking with the file system. The default, and the right answer for a
    /// long file.
    /// </summary>
    Streaming = 0,

    /// <summary>
    /// Map it into the address space and let the operating system decide what stays resident. Seeks cost
    /// nothing; pages that are never played are never read.
    /// </summary>
    MemoryMapped = 1,

    /// <summary>
    /// Read the whole file into a pooled buffer first. The right answer for a short clip that ships with the
    /// application and will be played repeatedly; see <see cref="PreloadedClip" /> for the form that survives
    /// between plays.
    /// </summary>
    Preloaded = 2,
}
