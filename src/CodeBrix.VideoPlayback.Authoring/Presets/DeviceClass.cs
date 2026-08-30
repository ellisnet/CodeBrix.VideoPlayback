namespace CodeBrix.VideoPlayback.Authoring.Presets;

/// <summary>
/// The classes of machine this family's device-class table has measured numbers for.
/// </summary>
/// <remarks>
/// These are GUIDANCE, not limits. Nothing in the playback library refuses a frame because of its size - the
/// decoder decodes whatever AV1 allows - and nothing here caps what may be authored. What the table records
/// is where the picture stops being watchable on the hardware named, which is a different and more useful
/// fact than a maximum.
/// </remarks>
public enum DeviceClass
{
    /// <summary>A desktop or laptop with a modern 64-bit processor, or Apple Silicon: 4K is comfortable.</summary>
    Desktop4K = 0,

    /// <summary>A Raspberry-Pi-class 64-bit ARM board: 1080p is the working ceiling.</summary>
    Pi1080p = 1,

    /// <summary>A current 64-bit RISC-V board: 720p, and 480p where the board is older or busier.</summary>
    RiscV720p = 2,
}
