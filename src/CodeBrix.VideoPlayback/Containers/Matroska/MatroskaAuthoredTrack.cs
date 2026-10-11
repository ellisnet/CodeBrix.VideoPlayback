namespace CodeBrix.VideoPlayback.Containers.Matroska;

/// <summary>One track as <see cref="MatroskaMuxer.Write" /> wrote it.</summary>
public sealed class MatroskaAuthoredTrack
{
    /// <summary>Creates a track summary.</summary>
    /// <param name="number">The track number.</param>
    /// <param name="kind">What the track carries.</param>
    /// <param name="codecId">The Matroska codec identifier written, such as <c>V_AV1</c>.</param>
    /// <param name="name">The track's name, or an empty string.</param>
    /// <param name="language">The three-letter language code written, such as <c>eng</c> or <c>und</c>.</param>
    /// <param name="blockCount">How many blocks were written for the track.</param>
    public MatroskaAuthoredTrack(int number, MediaTrackKind kind, string codecId, string name, string language, int blockCount)
    {
        Number = number;
        Kind = kind;
        CodecId = codecId ?? string.Empty;
        Name = name ?? string.Empty;
        Language = language ?? string.Empty;
        BlockCount = blockCount;
    }

    /// <summary>The track number.</summary>
    public int Number { get; }

    /// <summary>What the track carries.</summary>
    public MediaTrackKind Kind { get; }

    /// <summary>The Matroska codec identifier written.</summary>
    public string CodecId { get; }

    /// <summary>The track's name, or an empty string.</summary>
    public string Name { get; }

    /// <summary>The three-letter language code written.</summary>
    public string Language { get; }

    /// <summary>How many blocks were written for the track: frames, packets or caption cues.</summary>
    public int BlockCount { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"track {Number}: {Kind} {CodecId} '{Name}' [{Language}], {BlockCount} block(s)";
}
