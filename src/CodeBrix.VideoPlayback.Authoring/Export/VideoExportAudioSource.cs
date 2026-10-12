namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>Where the sound of an exported MP4 file came from.</summary>
public enum VideoExportAudioSource
{
    /// <summary>The file has no audio track: nothing was asked for, or there was no sound to take.</summary>
    None = 0,

    /// <summary>The source file's own sound - the usual case.</summary>
    Ours = 1,

    /// <summary>
    /// The backdrop video's sound, because the source is a master (Mode3) file that is flagged silent or carries
    /// no audio track at all.
    /// </summary>
    Backdrop = 2,
}
