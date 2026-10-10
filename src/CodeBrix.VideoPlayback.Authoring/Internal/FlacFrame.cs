namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>Where one FLAC frame sits in its file, and which samples it carries.</summary>
internal readonly struct FlacFrame
{
    /// <summary>Creates a frame description.</summary>
    /// <param name="offset">The byte offset of the frame's sync code.</param>
    /// <param name="length">The frame's length in bytes, through its CRC-16.</param>
    /// <param name="firstSample">The index of its first sample, per channel.</param>
    /// <param name="blockSize">How many samples per channel it carries.</param>
    internal FlacFrame(int offset, int length, long firstSample, int blockSize)
    {
        Offset = offset;
        Length = length;
        FirstSample = firstSample;
        BlockSize = blockSize;
    }

    /// <summary>The byte offset of the frame's sync code.</summary>
    internal int Offset { get; }

    /// <summary>The frame's length in bytes, through its CRC-16.</summary>
    internal int Length { get; }

    /// <summary>The index of its first sample, per channel.</summary>
    internal long FirstSample { get; }

    /// <summary>How many samples per channel it carries.</summary>
    internal int BlockSize { get; }
}
