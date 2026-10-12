namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>What a master (Mode3) file's transparent pixels are composited over when it is exported.</summary>
public enum ExportBackdropKind
{
    /// <summary>One solid colour - 50% grey unless another is chosen.</summary>
    Colour = 0,

    /// <summary>A still picture - a JPG, PNG or other image file, or RGBA pixels the caller decoded itself.</summary>
    Image = 1,

    /// <summary>Another video, decoded through the same video-processing engine the export writes with.</summary>
    Video = 2,
}
