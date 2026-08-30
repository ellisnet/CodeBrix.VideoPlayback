using System;
using System.Globalization;

namespace SimpleCbxVideoPlayer.SkiaVideo.Playback;

/// <summary>What a baked chain is called before the person saving it says otherwise.</summary>
/// <remarks>
/// There is no default FOLDER here, and that is the point: a bake goes where the person saving it says it
/// goes, chosen in their own platform's save dialog, or it does not happen at all. This class supplies only
/// the name the dialog opens with - a stamped one, so two bakes in a row do not silently propose the same
/// file - and the extension this family writes.
/// </remarks>
public static class BakeLocations
{
    /// <summary>The only lookup-table format this family writes.</summary>
    public const string LutFileExtension = ".cube";

    /// <summary>Builds the name a save dialog opens with for a bake made at a given moment.</summary>
    /// <param name="timestamp">The moment the bake was made.</param>
    /// <returns>A file name such as <c>chain-20260829-141530.cube</c>.</returns>
    public static string CreateFileName(DateTime timestamp) =>
        "chain-" + timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + LutFileExtension;
}
