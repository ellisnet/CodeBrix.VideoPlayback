using System;
using System.Globalization;
using System.IO;

namespace SimpleCbxVideoPlayer.SkiaVideo.Effects;

/// <summary>One lookup table in the chain, and how much of it to apply.</summary>
public sealed class LutChainEntry
{
    /// <summary>The strength a newly ticked table starts at.</summary>
    public const double DefaultApplyAtPercent = 40;

    /// <summary>The lowest strength that can be asked for.</summary>
    public const double MinimumPercent = 0;

    /// <summary>The highest strength that can be asked for.</summary>
    public const double MaximumPercent = 100;

    /// <summary>Creates the entry, clamping the strength into 0 to 100.</summary>
    /// <param name="filePath">The ".cube" file to apply.</param>
    /// <param name="applyAtPercent">How much of it to apply, 0 to 100.</param>
    /// <exception cref="ArgumentException">filePath is null or blank.</exception>
    public LutChainEntry(string filePath, double applyAtPercent = DefaultApplyAtPercent)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A lookup-table file path is required.", nameof(filePath));
        }

        FilePath = filePath;
        ApplyAtPercent = ClampPercent(applyAtPercent);
    }

    /// <summary>The ".cube" file to apply.</summary>
    public string FilePath { get; }

    /// <summary>How much of the table to apply, 0 to 100.</summary>
    public double ApplyAtPercent { get; }

    /// <summary>The file's own name, for logs and messages.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>What this entry contributes to a chain's signature.</summary>
    public string Signature =>
        FilePath + "@" + ApplyAtPercent.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Clamps a strength into the 0-to-100 range the presenter accepts.</summary>
    /// <param name="percent">The strength asked for.</param>
    /// <returns>The strength, brought into range; 0 when it is not a number.</returns>
    public static double ClampPercent(double percent)
    {
        if (double.IsNaN(percent)) { return MinimumPercent; }

        return Math.Clamp(percent, MinimumPercent, MaximumPercent);
    }

    /// <summary>Reads a strength typed by a person, clamping whatever it turns out to be.</summary>
    /// <param name="text">The text typed into the percent box.</param>
    /// <param name="percent">The strength the text means.</param>
    /// <returns>True when the text was a number; false when it was not, in which case percent is 0.</returns>
    public static bool TryParsePercent(string text, out double percent)
    {
        percent = MinimumPercent;

        if (string.IsNullOrWhiteSpace(text)) { return false; }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            && !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        percent = ClampPercent(parsed);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{FileName} at {ApplyAtPercent:0.#}%";
}
