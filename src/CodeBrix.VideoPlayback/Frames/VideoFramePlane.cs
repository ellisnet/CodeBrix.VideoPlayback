using System;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// One plane of a decoded frame: where its samples start, how far apart its rows are, how many samples it
/// carries, and how wide a sample is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Data" /> is 64-byte aligned and <see cref="Stride" /> is a multiple of 64 bytes, so a plane can
/// be read with the widest vector loads without a scalar prologue and can be uploaded to a graphics device
/// without repacking.
/// </para>
/// <para>
/// <see cref="Width" /> and <see cref="Height" /> are the VISIBLE sample counts. The memory behind the plane
/// is larger - rows are padded out to the buffer's padded dimensions and 64 bytes of slack follow the last
/// row - so a decoder may write past the visible area and a reader may over-read a vector's worth safely.
/// </para>
/// </remarks>
public readonly struct VideoFramePlane
{
    /// <summary>A plane that points at nothing - what the U and V planes of a monochrome frame read.</summary>
    public static readonly VideoFramePlane Empty = default;

    /// <summary>Creates a plane description.</summary>
    /// <param name="data">A 64-byte-aligned pointer to the first sample of the first row.</param>
    /// <param name="stride">The distance in BYTES from one row to the next; a multiple of 64.</param>
    /// <param name="width">The number of visible samples in a row.</param>
    /// <param name="height">The number of visible rows.</param>
    /// <param name="bytesPerSample">1 for 8-bit content, 2 for 10-bit and 12-bit content.</param>
    public VideoFramePlane(IntPtr data, int stride, int width, int height, int bytesPerSample)
    {
        Data = data;
        Stride = stride;
        Width = width;
        Height = height;
        BytesPerSample = bytesPerSample;
    }

    /// <summary>A 64-byte-aligned pointer to the first sample of the first row.</summary>
    public IntPtr Data { get; }

    /// <summary>The distance in BYTES from the start of one row to the start of the next. A multiple of 64.</summary>
    public int Stride { get; }

    /// <summary>The number of visible samples in a row.</summary>
    public int Width { get; }

    /// <summary>The number of visible rows.</summary>
    public int Height { get; }

    /// <summary>
    /// The width of one sample in bytes: 1 for 8-bit content, 2 for 10-bit and 12-bit content, in which case
    /// the samples are little-endian 16-bit words justified towards the least significant bit (10-bit content
    /// therefore occupies 0..1023).
    /// </summary>
    public int BytesPerSample { get; }

    /// <summary>True when this plane points at nothing - the U and V planes of a monochrome frame.</summary>
    public bool IsEmpty => Data == IntPtr.Zero;

    /// <summary>Returns the visible samples of one row as a span of bytes.</summary>
    /// <param name="row">The zero-based row index.</param>
    /// <returns>A span covering <see cref="Width" /> samples of the requested row.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the plane.</exception>
    /// <exception cref="InvalidOperationException">The plane is empty.</exception>
    public unsafe ReadOnlySpan<byte> GetRowBytes(int row)
    {
        if (Data == IntPtr.Zero) throw new InvalidOperationException("This plane carries no samples.");
        if (row < 0 || row >= Height) throw new ArgumentOutOfRangeException(nameof(row));

        return new ReadOnlySpan<byte>((byte*)Data + ((long)row * Stride), Width * BytesPerSample);
    }
}
