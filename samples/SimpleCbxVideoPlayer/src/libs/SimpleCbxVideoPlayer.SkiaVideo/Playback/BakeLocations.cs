using System;
using System.Globalization;
using System.IO;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>Where a baked chain goes when nobody was asked where to put it.</summary>
/// <remarks>
/// V1 has no file picker in this panel on purpose: every head would need one, the frame-buffer head's is
/// opt-in, and the button is worth having before the dialogue is. So a bake writes to a "baked-luts" folder
/// beside the running application, under a name stamped with the moment it was made, and the status line
/// shows the FULL path so the file can be found. Nothing is written outside the application's own folder.
/// </remarks>
public static class BakeLocations
{
    /// <summary>The folder a baked table goes into.</summary>
    public const string FolderName = "baked-luts";

    /// <summary>The full path of that folder, beside the running application.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Builds the name a bake made at a given moment is written under.</summary>
    /// <param name="timestamp">The moment the bake was made.</param>
    /// <returns>A file name such as <c>chain-20260829-141530.cube</c>.</returns>
    public static string CreateFileName(DateTime timestamp) =>
        "chain-" + timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + LutFileExtension;

    /// <summary>Builds the full path a bake made at a given moment is written to.</summary>
    /// <param name="timestamp">The moment the bake was made.</param>
    /// <returns>The full path, inside <see cref="DefaultFolder" />.</returns>
    public static string CreateFilePath(DateTime timestamp) =>
        Path.Combine(DefaultFolder, CreateFileName(timestamp));

    /// <summary>The only lookup-table format this family writes.</summary>
    public const string LutFileExtension = ".cube";
}
