using System;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>
/// The frame size an authored file is scaled to.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes, and the difference between them is whether the SOURCE has to be measured first.
/// <see cref="Exact" /> states both numbers, so the command line can be rendered without probing anything.
/// <see cref="LongSide" /> and <see cref="ShortSide" /> state one number and let FFmpeg work the other out
/// from the picture it is handed, which is what a rung table wants: "1080 on the long side" is one row for
/// both a landscape clip and a portrait one.
/// </para>
/// <para>
/// The aspect-preserving forms render an FFmpeg expression rather than a number, so they still need no
/// probe: <c>-2</c> tells the scaler "work this side out from the other and make it even", and the
/// <c>gte(iw,ih)</c> test decides which side the given number belongs to. Even dimensions are not a style
/// choice - 4:2:0 chroma has half as many samples per axis, so an odd dimension has no home.
/// </para>
/// <para>Instances are immutable.</para>
/// </remarks>
public sealed class AuthoringFrameSize
{
    private static readonly AuthoringFrameSize SourceSize = new AuthoringFrameSize(AuthoringFrameSizeKind.Source, 0, 0, 0);

    private AuthoringFrameSize(AuthoringFrameSizeKind kind, int width, int height, int pixels)
    {
        Kind = kind;
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Leave the picture at whatever size the source is. No scale filter is emitted.</summary>
    public static AuthoringFrameSize Source => SourceSize;

    /// <summary>How this instance states the size it wants.</summary>
    public AuthoringFrameSizeKind Kind { get; }

    /// <summary>The exact width in pixels, or 0 when the kind is not <see cref="AuthoringFrameSizeKind.Exact" />.</summary>
    public int Width { get; }

    /// <summary>The exact height in pixels, or 0 when the kind is not <see cref="AuthoringFrameSizeKind.Exact" />.</summary>
    public int Height { get; }

    /// <summary>
    /// The long or short side in pixels, or 0 when the kind is neither
    /// <see cref="AuthoringFrameSizeKind.LongSide" /> nor <see cref="AuthoringFrameSizeKind.ShortSide" />.
    /// </summary>
    public int Pixels { get; }

    /// <summary>True when nothing is scaled and no filter is emitted.</summary>
    public bool IsSourceSize => Kind == AuthoringFrameSizeKind.Source;

    /// <summary>States both dimensions.</summary>
    /// <param name="width">The width in pixels. Must be positive and even.</param>
    /// <param name="height">The height in pixels. Must be positive and even.</param>
    /// <returns>The size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not a positive even number.</exception>
    public static AuthoringFrameSize Exact(int width, int height)
    {
        RequirePositiveEven(width, nameof(width));
        RequirePositiveEven(height, nameof(height));

        return new AuthoringFrameSize(AuthoringFrameSizeKind.Exact, width, height, 0);
    }

    /// <summary>States the LONGER side and lets the shorter one follow from the source's aspect ratio.</summary>
    /// <param name="pixels">The long side in pixels. Must be positive and even.</param>
    /// <returns>The size.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixels" /> is not a positive even number.</exception>
    public static AuthoringFrameSize LongSide(int pixels)
    {
        RequirePositiveEven(pixels, nameof(pixels));

        return new AuthoringFrameSize(AuthoringFrameSizeKind.LongSide, 0, 0, pixels);
    }

    /// <summary>States the SHORTER side and lets the longer one follow from the source's aspect ratio.</summary>
    /// <param name="pixels">The short side in pixels. Must be positive and even.</param>
    /// <returns>The size.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixels" /> is not a positive even number.</exception>
    public static AuthoringFrameSize ShortSide(int pixels)
    {
        RequirePositiveEven(pixels, nameof(pixels));

        return new AuthoringFrameSize(AuthoringFrameSizeKind.ShortSide, 0, 0, pixels);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        switch (Kind)
        {
            case AuthoringFrameSizeKind.Exact:
                return Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
            case AuthoringFrameSizeKind.LongSide:
                return Pixels.ToString(CultureInfo.InvariantCulture) + " on the long side";
            case AuthoringFrameSizeKind.ShortSide:
                return Pixels.ToString(CultureInfo.InvariantCulture) + " on the short side";
            default:
                return "the source's own size";
        }
    }

    /// <summary>The value the scale filter's <c>w</c> option is given.</summary>
    /// <returns>A number or an FFmpeg expression.</returns>
    internal string RenderWidth()
    {
        switch (Kind)
        {
            case AuthoringFrameSizeKind.Exact:
                return Width.ToString(CultureInfo.InvariantCulture);
            case AuthoringFrameSizeKind.LongSide:
                return "if(gte(iw,ih)," + Pixels.ToString(CultureInfo.InvariantCulture) + ",-2)";
            case AuthoringFrameSizeKind.ShortSide:
                return "if(gte(iw,ih),-2," + Pixels.ToString(CultureInfo.InvariantCulture) + ")";
            default:
                return string.Empty;
        }
    }

    /// <summary>The value the scale filter's <c>h</c> option is given.</summary>
    /// <returns>A number or an FFmpeg expression.</returns>
    internal string RenderHeight()
    {
        switch (Kind)
        {
            case AuthoringFrameSizeKind.Exact:
                return Height.ToString(CultureInfo.InvariantCulture);
            case AuthoringFrameSizeKind.LongSide:
                return "if(gte(iw,ih),-2," + Pixels.ToString(CultureInfo.InvariantCulture) + ")";
            case AuthoringFrameSizeKind.ShortSide:
                return "if(gte(iw,ih)," + Pixels.ToString(CultureInfo.InvariantCulture) + ",-2)";
            default:
                return string.Empty;
        }
    }

    private static void RequirePositiveEven(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A frame dimension is a positive number of pixels.");
        }

        if ((value & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A frame dimension must be EVEN: 4:2:0 chroma carries half as many samples per axis, so an odd "
                + "dimension has nowhere to put the last one.");
        }
    }
}
