namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Colour primaries, numbered exactly as ISO/IEC 23091-2 (and therefore as AV1's <c>color_primaries</c>
/// and Matroska's <c>Primaries</c> element) numbers them.
/// </summary>
/// <remarks>
/// The numeric values ARE the wire values: a container or decoder may cast a raw byte to this type. Values
/// that are not listed here are legal on the wire and are carried through unchanged.
/// </remarks>
public enum VideoColorPrimaries
{
    /// <summary>ITU-R BT.709 - the sRGB / HD primaries.</summary>
    Bt709 = 1,

    /// <summary>Unspecified; the consumer applies its own default (this library treats it as BT.709).</summary>
    Unspecified = 2,

    /// <summary>ITU-R BT.470 System M.</summary>
    Bt470M = 4,

    /// <summary>ITU-R BT.470 System B/G - the classic PAL/SECAM primaries.</summary>
    Bt470Bg = 5,

    /// <summary>SMPTE 170M - the classic NTSC primaries, identical to BT.601 525-line.</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>Generic film (colour filters using Illuminant C).</summary>
    Film = 8,

    /// <summary>ITU-R BT.2020 - the ultra-high-definition and wide-gamut primaries.</summary>
    Bt2020 = 9,

    /// <summary>SMPTE ST 428-1 (CIE 1931 XYZ).</summary>
    SmpteSt428 = 10,

    /// <summary>SMPTE RP 431-2 (DCI-P3 with a DCI white point).</summary>
    SmpteRp431 = 11,

    /// <summary>SMPTE EG 432-1 (Display P3, with a D65 white point).</summary>
    SmpteEg432 = 12,

    /// <summary>EBU Tech 3213-E.</summary>
    Ebu3213 = 22,
}
