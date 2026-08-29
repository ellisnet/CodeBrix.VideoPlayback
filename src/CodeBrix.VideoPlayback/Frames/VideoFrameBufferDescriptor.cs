using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// What a decoder is asking a <see cref="IVideoFrameBufferPool" /> for: a frame of these dimensions, this
/// plane layout and this bit depth.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor states the VISIBLE size. The pool decides the allocated size, which is always larger:
/// dimensions are rounded up to a multiple of 128 samples and 64 bytes of slack follow each plane. The
/// padded values are readable through <see cref="PaddedWidth" /> and <see cref="PaddedHeight" /> so that a
/// caller can reason about the allocation without knowing the pool's implementation.
/// </para>
/// <para>
/// Two descriptors that produce the same allocation compare equal, which is what lets a pool bucket its
/// buffers by descriptor.
/// </para>
/// </remarks>
public readonly struct VideoFrameBufferDescriptor : IEquatable<VideoFrameBufferDescriptor>
{
    /// <summary>The alignment, in bytes, of every plane pointer and every row stride a pool hands out.</summary>
    public const int PlaneAlignment = 64;

    /// <summary>The multiple that both dimensions of an allocation are rounded up to.</summary>
    public const int DimensionMultiple = 128;

    /// <summary>The number of bytes of slack a pool leaves after the last row of every plane.</summary>
    public const int TailPadding = 64;

    /// <summary>Creates a descriptor.</summary>
    /// <param name="width">The visible width in luma samples. Must be greater than zero.</param>
    /// <param name="height">The visible height in luma samples. Must be greater than zero.</param>
    /// <param name="layout">The plane layout and chroma subsampling. Must not be <see cref="VideoPixelLayout.Unknown" />.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the bit depth is not 8, 10 or 12.</exception>
    /// <exception cref="ArgumentException">The layout is <see cref="VideoPixelLayout.Unknown" />.</exception>
    public VideoFrameBufferDescriptor(int width, int height, VideoPixelLayout layout, int bitDepth)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be greater than zero.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be greater than zero.");
        if (layout == VideoPixelLayout.Unknown) throw new ArgumentException("The pixel layout must be known.", nameof(layout));
        if (bitDepth != 8 && bitDepth != 10 && bitDepth != 12)
        {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "The bit depth must be 8, 10 or 12.");
        }

        Width = width;
        Height = height;
        Layout = layout;
        BitDepth = bitDepth;
    }

    /// <summary>The visible width in luma samples.</summary>
    public int Width { get; }

    /// <summary>The visible height in luma samples.</summary>
    public int Height { get; }

    /// <summary>The plane layout and chroma subsampling.</summary>
    public VideoPixelLayout Layout { get; }

    /// <summary>Bits per sample: 8, 10 or 12.</summary>
    public int BitDepth { get; }

    /// <summary>1 for 8-bit content, 2 for 10-bit and 12-bit content.</summary>
    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    /// <summary><see cref="Width" /> rounded up to a multiple of <see cref="DimensionMultiple" />.</summary>
    public int PaddedWidth => RoundUp(Width, DimensionMultiple);

    /// <summary><see cref="Height" /> rounded up to a multiple of <see cref="DimensionMultiple" />.</summary>
    public int PaddedHeight => RoundUp(Height, DimensionMultiple);

    /// <summary>How far right a luma coordinate shifts to reach the matching chroma coordinate.</summary>
    public int ChromaShiftX => Layout == VideoPixelLayout.I420 || Layout == VideoPixelLayout.I422 ? 1 : 0;

    /// <summary>How far down a luma coordinate shifts to reach the matching chroma coordinate.</summary>
    public int ChromaShiftY => Layout == VideoPixelLayout.I420 ? 1 : 0;

    /// <summary>True when the layout has no chroma planes at all.</summary>
    public bool IsMonochrome => Layout == VideoPixelLayout.Gray;

    /// <summary>The distance in bytes between rows of the luma plane.</summary>
    public int LumaStride => PaddedWidth * BytesPerSample;

    /// <summary>The distance in bytes between rows of each chroma plane; zero for a monochrome layout.</summary>
    public int ChromaStride => IsMonochrome ? 0 : (PaddedWidth >> ChromaShiftX) * BytesPerSample;

    /// <summary>The number of rows in the luma plane's allocation.</summary>
    public int LumaAllocationRows => PaddedHeight;

    /// <summary>The number of rows in each chroma plane's allocation; zero for a monochrome layout.</summary>
    public int ChromaAllocationRows => IsMonochrome ? 0 : PaddedHeight >> ChromaShiftY;

    /// <summary>
    /// The total number of bytes an allocation for this descriptor occupies, including the per-plane tail
    /// padding.
    /// </summary>
    public long AllocationBytes =>
        ((long)LumaStride * LumaAllocationRows + TailPadding)
        + (IsMonochrome ? 0 : 2 * ((long)ChromaStride * ChromaAllocationRows + TailPadding));

    /// <summary>The number of visible samples in a row of the luma plane.</summary>
    public int LumaVisibleWidth => Width;

    /// <summary>The number of visible rows of the luma plane.</summary>
    public int LumaVisibleHeight => Height;

    /// <summary>The number of visible samples in a row of a chroma plane; zero for a monochrome layout.</summary>
    public int ChromaVisibleWidth => IsMonochrome ? 0 : (Width + (1 << ChromaShiftX) - 1) >> ChromaShiftX;

    /// <summary>The number of visible rows of a chroma plane; zero for a monochrome layout.</summary>
    public int ChromaVisibleHeight => IsMonochrome ? 0 : (Height + (1 << ChromaShiftY) - 1) >> ChromaShiftY;

    /// <inheritdoc />
    public bool Equals(VideoFrameBufferDescriptor other) =>
        Width == other.Width && Height == other.Height && Layout == other.Layout && BitDepth == other.BitDepth;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is VideoFrameBufferDescriptor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Width, Height, (int)Layout, BitDepth);

    /// <summary>Compares two descriptors for equality.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns>True when every field matches.</returns>
    public static bool operator ==(VideoFrameBufferDescriptor left, VideoFrameBufferDescriptor right) => left.Equals(right);

    /// <summary>Compares two descriptors for inequality.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns>True when any field differs.</returns>
    public static bool operator !=(VideoFrameBufferDescriptor left, VideoFrameBufferDescriptor right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Width}x{Height} {Layout} {BitDepth}-bit (allocated {PaddedWidth}x{PaddedHeight}, {AllocationBytes} bytes)";

    private static int RoundUp(int value, int multiple) => ((value + multiple - 1) / multiple) * multiple;
}
