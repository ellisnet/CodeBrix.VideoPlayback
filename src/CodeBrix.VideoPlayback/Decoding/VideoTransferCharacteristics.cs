namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The transfer characteristic ("gamma curve"), numbered exactly as ISO/IEC 23091-2 numbers it - the values
/// AV1's <c>transfer_characteristics</c> and Matroska's <c>TransferCharacteristics</c> element carry.
/// </summary>
/// <remarks>
/// The numeric values ARE the wire values. High-dynamic-range curves are recognised - see
/// <see cref="VideoColorInfo.IsHighDynamicRange" /> - but this library does not tone-map them; a frame
/// carrying one is converted as if it were BT.709 and the fact is logged once.
/// </remarks>
public enum VideoTransferCharacteristics
{
    /// <summary>ITU-R BT.709.</summary>
    Bt709 = 1,

    /// <summary>Unspecified; the consumer applies its own default (this library treats it as BT.709).</summary>
    Unspecified = 2,

    /// <summary>ITU-R BT.470 System M - gamma 2.2.</summary>
    Gamma22 = 4,

    /// <summary>ITU-R BT.470 System B/G - gamma 2.8.</summary>
    Gamma28 = 5,

    /// <summary>SMPTE 170M - the BT.601 curve.</summary>
    Smpte170M = 6,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 7,

    /// <summary>Linear light.</summary>
    Linear = 8,

    /// <summary>Logarithmic, 100:1 range.</summary>
    Log100 = 9,

    /// <summary>Logarithmic, 100 * sqrt(10) : 1 range.</summary>
    Log100Sqrt10 = 10,

    /// <summary>IEC 61966-2-4 (xvYCC).</summary>
    Iec61966 = 11,

    /// <summary>ITU-R BT.1361 extended colour gamut.</summary>
    Bt1361 = 12,

    /// <summary>IEC 61966-2-1 - the sRGB / sYCC curve.</summary>
    Srgb = 13,

    /// <summary>ITU-R BT.2020 with 10-bit precision.</summary>
    Bt2020TenBit = 14,

    /// <summary>ITU-R BT.2020 with 12-bit precision.</summary>
    Bt2020TwelveBit = 15,

    /// <summary>SMPTE ST 2084 - the "PQ" high-dynamic-range curve.</summary>
    SmpteSt2084 = 16,

    /// <summary>SMPTE ST 428-1.</summary>
    SmpteSt428 = 17,

    /// <summary>ARIB STD-B67 - the "HLG" hybrid log-gamma high-dynamic-range curve.</summary>
    AribStdB67 = 18,
}
