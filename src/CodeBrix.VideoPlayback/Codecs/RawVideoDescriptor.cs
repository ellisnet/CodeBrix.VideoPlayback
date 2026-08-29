using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// What an uncompressed video track's frames look like: their size, their plane layout, their bit depth and
/// their colour.
/// </summary>
/// <remarks>
/// An uncompressed track has no bitstream to state any of this, so a container has to. See
/// <see cref="RawVideoFormat" /> for how it is stored.
/// </remarks>
public readonly struct RawVideoDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    /// <param name="width">The frame width in luma samples.</param>
    /// <param name="height">The frame height in luma samples.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="color">The colour description.</param>
    public RawVideoDescriptor(int width, int height, int bitDepth, VideoPixelLayout layout, VideoColorInfo color)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Layout = layout;
        Color = color;
    }

    /// <summary>The frame width in luma samples.</summary>
    public int Width { get; }

    /// <summary>The frame height in luma samples.</summary>
    public int Height { get; }

    /// <summary>Bits per sample: 8, 10 or 12.</summary>
    public int BitDepth { get; }

    /// <summary>The plane layout and chroma subsampling.</summary>
    public VideoPixelLayout Layout { get; }

    /// <summary>The colour description.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>1 for 8-bit content, 2 for 10-bit and 12-bit content.</summary>
    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    /// <summary>True when the fields describe a frame that can actually be decoded.</summary>
    public bool IsValid =>
        Width > 0
        && Height > 0
        && Layout != VideoPixelLayout.Unknown
        && (BitDepth == 8 || BitDepth == 10 || BitDepth == 12);

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height} {Layout} {BitDepth}-bit uncompressed";
}
