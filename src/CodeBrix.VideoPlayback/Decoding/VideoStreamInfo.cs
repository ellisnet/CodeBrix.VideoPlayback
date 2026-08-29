using System;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// What a decoder knows about the video it is producing: dimensions, sample layout, bit depth and colour.
/// </summary>
/// <remarks>
/// <para>
/// A decoder fills this in as soon as it has parsed enough of the stream - for AV1, once it has a sequence
/// header. Before that, <see cref="IsKnown" /> reads false and the numeric fields are zero.
/// </para>
/// <para>
/// AV1 allows a stream's frame size to change mid-stream, so this is a description of the stream's CURRENT
/// state, not a promise about every frame. Presenters size themselves from each
/// <see cref="CodeBrix.VideoPlayback.Frames.VideoFrame" /> rather than from this.
/// </para>
/// </remarks>
public sealed class VideoStreamInfo
{
    /// <summary>The value a decoder reports before it has parsed anything: every field zero or unknown.</summary>
    public static readonly VideoStreamInfo Unknown = new VideoStreamInfo();

    /// <summary>Creates an empty description - the state before a sequence header has been parsed.</summary>
    public VideoStreamInfo()
    {
        Color = VideoColorInfo.Unspecified;
    }

    /// <summary>Creates a description of a known stream.</summary>
    /// <param name="width">The coded width in pixels.</param>
    /// <param name="height">The coded height in pixels.</param>
    /// <param name="displayWidth">The width the frame should be shown at, after pixel-aspect correction.</param>
    /// <param name="displayHeight">The height the frame should be shown at, after pixel-aspect correction.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="color">The colour description.</param>
    public VideoStreamInfo(
        int width,
        int height,
        int displayWidth,
        int displayHeight,
        VideoPixelLayout layout,
        int bitDepth,
        VideoColorInfo color)
    {
        Width = width;
        Height = height;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        Layout = layout;
        BitDepth = bitDepth;
        Color = color;
    }

    /// <summary>The coded width in pixels - the number of luma samples per row that carry picture.</summary>
    public int Width { get; set; }

    /// <summary>The coded height in pixels - the number of luma rows that carry picture.</summary>
    public int Height { get; set; }

    /// <summary>
    /// The width the frame should be shown at once the pixel aspect ratio has been applied, or the same as
    /// <see cref="Width" /> when the pixels are square.
    /// </summary>
    public int DisplayWidth { get; set; }

    /// <summary>
    /// The height the frame should be shown at once the pixel aspect ratio has been applied, or the same as
    /// <see cref="Height" /> when the pixels are square.
    /// </summary>
    public int DisplayHeight { get; set; }

    /// <summary>The plane layout and chroma subsampling.</summary>
    public VideoPixelLayout Layout { get; set; }

    /// <summary>Bits per sample: 8, 10 or 12. Zero until the stream has been parsed.</summary>
    public int BitDepth { get; set; }

    /// <summary>The colour description: primaries, transfer, matrix, range and chroma siting.</summary>
    public VideoColorInfo Color { get; set; }

    /// <summary>The mastering metadata a high-dynamic-range stream carries, or null when there is none.</summary>
    public HdrMetadata Hdr { get; set; }

    /// <summary>
    /// How long each frame is shown for when the stream is constant-frame-rate, or
    /// <see cref="TimeSpan.Zero" /> when it varies or is not stated.
    /// </summary>
    public TimeSpan FrameDuration { get; set; }

    /// <summary>True once the decoder has parsed enough of the stream for the other fields to mean anything.</summary>
    public bool IsKnown => Width > 0 && Height > 0 && BitDepth > 0 && Layout != VideoPixelLayout.Unknown;

    /// <summary>The largest value a sample can take at this bit depth - <c>(1 &lt;&lt; BitDepth) - 1</c>.</summary>
    public int MaxSampleValue => BitDepth <= 0 ? 0 : (1 << BitDepth) - 1;

    /// <summary>How far right a luma coordinate shifts to reach the matching chroma coordinate: 1 for 4:2:0 and 4:2:2, else 0.</summary>
    public int ChromaShiftX => Layout == VideoPixelLayout.I420 || Layout == VideoPixelLayout.I422 ? 1 : 0;

    /// <summary>How far down a luma coordinate shifts to reach the matching chroma coordinate: 1 for 4:2:0, else 0.</summary>
    public int ChromaShiftY => Layout == VideoPixelLayout.I420 ? 1 : 0;

    /// <summary>Returns a copy of this description.</summary>
    /// <returns>A new instance carrying the same values, with the HDR metadata copied too.</returns>
    public VideoStreamInfo Clone()
    {
        VideoStreamInfo clone = (VideoStreamInfo)MemberwiseClone();
        if (Hdr != null) clone.Hdr = Hdr.Clone();
        return clone;
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsKnown
            ? $"{Width}x{Height} {Layout} {BitDepth}-bit ({Color})"
            : "video stream (not parsed yet)";
}
