namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The YCbCr matrix a stream's chroma planes were formed with, numbered exactly as ISO/IEC 23091-2 numbers
/// it - the values AV1's <c>matrix_coefficients</c> and Matroska's <c>MatrixCoefficients</c> element carry.
/// </summary>
/// <remarks>The numeric values ARE the wire values.</remarks>
public enum VideoMatrixCoefficients
{
    /// <summary>The planes are GBR (or RGB) rather than YCbCr - "identity".</summary>
    Identity = 0,

    /// <summary>ITU-R BT.709.</summary>
    Bt709 = 1,

    /// <summary>Unspecified; the consumer applies its own default (this library picks BT.709 or BT.601 by frame height).</summary>
    Unspecified = 2,

    /// <summary>United States FCC Title 47.</summary>
    Fcc = 4,

    /// <summary>ITU-R BT.470 System B/G - numerically the BT.601 matrix.</summary>
    Bt470Bg = 5,

    /// <summary>SMPTE 170M - numerically the BT.601 matrix.</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>YCgCo.</summary>
    YCgCo = 8,

    /// <summary>ITU-R BT.2020 non-constant luminance.</summary>
    Bt2020NonConstantLuminance = 9,

    /// <summary>ITU-R BT.2020 constant luminance.</summary>
    Bt2020ConstantLuminance = 10,

    /// <summary>SMPTE ST 2085.</summary>
    SmpteSt2085 = 11,

    /// <summary>Chromaticity-derived non-constant luminance.</summary>
    ChromaticityDerivedNonConstantLuminance = 12,

    /// <summary>Chromaticity-derived constant luminance.</summary>
    ChromaticityDerivedConstantLuminance = 13,

    /// <summary>ITU-R BT.2100 ICtCp.</summary>
    IctCp = 14,
}
