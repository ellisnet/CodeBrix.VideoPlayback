using System;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// What a master (Mode3) file is composited over when it is exported to MP4: a solid colour (50% grey by
/// default), a still image, or another video.
/// </summary>
/// <remarks>
/// <para>
/// ONLY A FILE WITH AN ALPHA-PLANE TRACK COMPOSITES. For every other source - a WebM-profile (Mode1) file, a
/// bespoke (Mode2) file, a master file without alpha - the backdrop is ignored and the result says so in a note.
/// </para>
/// <para>
/// The output is always the size of the SOURCE's picture: an image or video of another size is scaled to it with
/// the core's <c>FrameScaler</c> (its default filter), once for an image and frame by frame for a video. Any
/// transparency the backdrop itself carries is flattened over 50% grey first - the backdrop is opaque.
/// </para>
/// <para>
/// A VIDEO backdrop is decoded through the same engine the export writes with. Each output frame takes the
/// backdrop frame shown at or before its time (no resampling). The output lasts as long as the LONGER of the two:
/// once the source has ended the backdrop plays on alone; once the backdrop has ended the source is composited over
/// 50% grey (never over <see cref="Colour" />). Its sound is used only when the source is flagged silent or has no
/// audio track at all.
/// </para>
/// </remarks>
public sealed class ExportBackdrop
{
    /// <summary>The colour every backdrop starts as, and the one a finished video backdrop leaves behind: 50% grey.</summary>
    public const int DefaultColour = 0x808080;

    /// <summary>Creates the default backdrop: a solid 50% grey.</summary>
    public ExportBackdrop()
    {
        Kind = ExportBackdropKind.Colour;
        Colour = DefaultColour;
    }

    /// <summary>Which kind of backdrop this is.</summary>
    public ExportBackdropKind Kind { get; set; }

    /// <summary>
    /// The solid colour as <c>0xRRGGBB</c> - <see cref="DefaultColour" /> (50% grey) unless set. Used when
    /// <see cref="Kind" /> is <see cref="ExportBackdropKind.Colour" />; the top byte is ignored.
    /// </summary>
    public int Colour { get; set; }

    /// <summary>
    /// The image file - JPG, PNG, or any other format CodeBrix.Imaging reads - when <see cref="Kind" /> is
    /// <see cref="ExportBackdropKind.Image" /> and <see cref="ImageRgba" /> is null.
    /// </summary>
    public string ImagePath { get; set; }

    /// <summary>
    /// Already-decoded image pixels - R, G, B, A bytes, straight alpha, rows packed - for a caller with a decoder
    /// of its own. When set it wins over <see cref="ImagePath" />.
    /// </summary>
    public byte[] ImageRgba { get; set; }

    /// <summary>The width of <see cref="ImageRgba" />, in pixels.</summary>
    public int ImageWidth { get; set; }

    /// <summary>The height of <see cref="ImageRgba" />, in pixels.</summary>
    public int ImageHeight { get; set; }

    /// <summary>The video file when <see cref="Kind" /> is <see cref="ExportBackdropKind.Video" />.</summary>
    public string VideoPath { get; set; }

    /// <summary>A solid-colour backdrop.</summary>
    /// <param name="rgb">The colour as <c>0xRRGGBB</c>.</param>
    /// <returns>The backdrop.</returns>
    public static ExportBackdrop FromColour(int rgb) =>
        new ExportBackdrop { Kind = ExportBackdropKind.Colour, Colour = rgb & 0xFFFFFF };

    /// <summary>A backdrop read from an image file with CodeBrix.Imaging.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>The backdrop.</returns>
    public static ExportBackdrop FromImage(string path) =>
        new ExportBackdrop { Kind = ExportBackdropKind.Image, ImagePath = path };

    /// <summary>A backdrop from RGBA pixels the caller decoded itself.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="rgba">R, G, B, A bytes, straight alpha, <c>width * height * 4</c> of them, rows packed.</param>
    /// <returns>The backdrop.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rgba" /> is null.</exception>
    /// <exception cref="ArgumentException">The size and the byte count disagree.</exception>
    public static ExportBackdrop FromImageRgba(int width, int height, byte[] rgba)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length)
        {
            throw new ArgumentException(
                "An RGBA backdrop of " + width + "x" + height + " needs " + ((long)Math.Max(0, width) * Math.Max(0, height) * 4)
                + " bytes; " + rgba.Length + " were given.",
                nameof(rgba));
        }

        return new ExportBackdrop { Kind = ExportBackdropKind.Image, ImageRgba = rgba, ImageWidth = width, ImageHeight = height };
    }

    /// <summary>A backdrop played from another video file through the export's engine.</summary>
    /// <param name="path">The video file.</param>
    /// <returns>The backdrop.</returns>
    public static ExportBackdrop FromVideo(string path) =>
        new ExportBackdrop { Kind = ExportBackdropKind.Video, VideoPath = path };

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        ExportBackdropKind.Image => ImageRgba != null ? "image rgba " + ImageWidth + "x" + ImageHeight : "image " + ImagePath,
        ExportBackdropKind.Video => "video " + VideoPath,
        _ => "colour #" + (Colour & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
    };
}
