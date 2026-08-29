using System;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The high-dynamic-range mastering metadata a stream may carry beside its frames: the mastering display's
/// primaries and luminance range, and the content light levels.
/// </summary>
/// <remarks>
/// <para>
/// This library reads the values and hands them on unchanged; it does not tone-map. A presenter or an
/// application that wants to map high-dynamic-range content onto a standard display has everything it needs
/// here.
/// </para>
/// <para>
/// The chromaticity fields are CIE 1931 x/y coordinates in the 0..1 range - already converted from the
/// integer forms the containers use. A luminance or light-level field reads 0 when the stream did not
/// state it.
/// </para>
/// </remarks>
public sealed class HdrMetadata
{
    /// <summary>Gets or sets the x coordinate of the mastering display's red primary.</summary>
    public double RedPrimaryX { get; set; }

    /// <summary>Gets or sets the y coordinate of the mastering display's red primary.</summary>
    public double RedPrimaryY { get; set; }

    /// <summary>Gets or sets the x coordinate of the mastering display's green primary.</summary>
    public double GreenPrimaryX { get; set; }

    /// <summary>Gets or sets the y coordinate of the mastering display's green primary.</summary>
    public double GreenPrimaryY { get; set; }

    /// <summary>Gets or sets the x coordinate of the mastering display's blue primary.</summary>
    public double BluePrimaryX { get; set; }

    /// <summary>Gets or sets the y coordinate of the mastering display's blue primary.</summary>
    public double BluePrimaryY { get; set; }

    /// <summary>Gets or sets the x coordinate of the mastering display's white point.</summary>
    public double WhitePointX { get; set; }

    /// <summary>Gets or sets the y coordinate of the mastering display's white point.</summary>
    public double WhitePointY { get; set; }

    /// <summary>Gets or sets the mastering display's maximum luminance, in candela per square metre.</summary>
    public double MaxLuminance { get; set; }

    /// <summary>Gets or sets the mastering display's minimum luminance, in candela per square metre.</summary>
    public double MinLuminance { get; set; }

    /// <summary>Gets or sets the maximum content light level, in candela per square metre.</summary>
    public int MaxContentLightLevel { get; set; }

    /// <summary>Gets or sets the maximum frame-average light level, in candela per square metre.</summary>
    public int MaxFrameAverageLightLevel { get; set; }

    /// <summary>Returns a copy of this metadata.</summary>
    /// <returns>A new instance carrying the same values.</returns>
    public HdrMetadata Clone() => (HdrMetadata)MemberwiseClone();

    /// <inheritdoc />
    public override string ToString() =>
        $"HDR mastering {MinLuminance:0.####}-{MaxLuminance:0.##} cd/m2, MaxCLL {MaxContentLightLevel}, MaxFALL {MaxFrameAverageLightLevel}";
}
