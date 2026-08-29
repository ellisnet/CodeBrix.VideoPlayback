using System;

namespace CodeBrix.VideoPlayback.Containers.Ebml;

/// <summary>
/// The identifier and size an EBML element declares, and where its payload begins.
/// </summary>
/// <remarks>
/// <para>
/// A value type, because a demultiplexer reads one of these for every element in a file - hundreds of
/// thousands of them for a feature-length recording - and none of them should reach the garbage collector.
/// </para>
/// <para>
/// An element whose size field is all ones has no declared size: it ends where the next element that cannot
/// be its child begins, or at the end of the file. Live-authored recordings use that for the Segment and
/// sometimes for each Cluster, because the writer does not know the length until it has finished.
/// </para>
/// </remarks>
public readonly struct EbmlElementHeader
{
    /// <summary>Creates a header description.</summary>
    /// <param name="id">The element identifier, marker bits included, as a big-endian value.</param>
    /// <param name="offset">The absolute offset the identifier starts at.</param>
    /// <param name="dataOffset">The absolute offset the payload starts at.</param>
    /// <param name="dataSize">The payload's length in bytes, or -1 when the element declared no size.</param>
    public EbmlElementHeader(uint id, long offset, long dataOffset, long dataSize)
    {
        Id = id;
        Offset = offset;
        DataOffset = dataOffset;
        DataSize = dataSize;
    }

    /// <summary>
    /// The element identifier with its marker bits kept, which is the form every specification writes: the
    /// Segment is 0x18538067, not 0x08538067.
    /// </summary>
    public uint Id { get; }

    /// <summary>The absolute offset the element's identifier starts at.</summary>
    public long Offset { get; }

    /// <summary>The absolute offset the element's payload starts at.</summary>
    public long DataOffset { get; }

    /// <summary>The payload's length in bytes, or -1 when the element declared no size.</summary>
    public long DataSize { get; }

    /// <summary>True when the element declared no size and ends only when something else begins.</summary>
    public bool IsUnknownSize => DataSize < 0;

    /// <summary>The number of bytes the identifier and size fields occupy together.</summary>
    public int HeaderSize => (int)(DataOffset - Offset);

    /// <summary>
    /// The absolute offset just past the element, or -1 when the element declared no size.
    /// </summary>
    public long EndOffset => DataSize < 0 ? -1L : DataOffset + DataSize;

    /// <inheritdoc />
    public override string ToString() =>
        DataSize < 0
            ? $"element 0x{Id:X} at {Offset} (unknown size)"
            : $"element 0x{Id:X} at {Offset}, {DataSize} bytes of payload at {DataOffset}";
}
