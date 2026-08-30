namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>What a colour-grade chain reduced to: at most one ".cube" file for FFmpeg to look up.</summary>
internal sealed class ResolvedLutChain
{
    internal static readonly ResolvedLutChain None = new ResolvedLutChain();

    /// <summary>The file handed to the lut3d filter, or null when there is no grade at all.</summary>
    internal string FilterPath { get; set; }

    /// <summary>The temporary file that has to be deleted afterwards, or null when none was written.</summary>
    internal string TemporaryPath { get; set; }

    /// <summary>True when the chain was folded into a new table rather than used as it stands.</summary>
    internal bool WasComposed { get; set; }

    /// <summary>The composed table's title, for the result's notes.</summary>
    internal string Title { get; set; }

    /// <summary>The composed table's size in nodes a side, for the result's notes.</summary>
    internal int Size { get; set; }

    /// <summary>True when a lut3d filter belongs in the chain.</summary>
    internal bool HasGrade => !string.IsNullOrEmpty(FilterPath);
}
