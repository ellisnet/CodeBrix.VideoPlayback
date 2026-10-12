namespace CodeBrix.VideoPlayback.Processing;

/// <summary>A quarter-turn rotation, measured CLOCKWISE as the picture is viewed.</summary>
/// <remarks>
/// The numeric values are the clockwise angle in degrees. To turn the display-matrix angle an FFmpeg probe
/// reports (counter-clockwise, so -90 means "show this turned 90 degrees clockwise") into the rotation that
/// makes the stored picture upright, use <see cref="PlaneRotator.FromDisplayMatrixRotation" />.
/// </remarks>
public enum RotationAngle
{
    /// <summary>No rotation.</summary>
    None = 0,

    /// <summary>A quarter turn clockwise: the width and height swap.</summary>
    Clockwise90 = 90,

    /// <summary>A half turn.</summary>
    Rotate180 = 180,

    /// <summary>Three quarter turns clockwise - a quarter turn counter-clockwise: the width and height swap.</summary>
    Clockwise270 = 270,
}
