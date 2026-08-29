using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using System;
using System.Globalization;

namespace SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;

/// <summary>One <c>--lut</c> switch: which table to tick, and at what percentage.</summary>
/// <remarks>
/// <para>
/// The value is <c>name</c> or <c>name@percent</c> - <c>sepia_33.cube</c>, <c>sepia_33.cube@40</c> -
/// and an <c>=</c> is accepted in place of the <c>@</c>. A value with no percentage takes the panel's own
/// default of 40.
/// </para>
/// <para>
/// THE SEPARATOR IS NOT OPTIONAL ONCE IT IS THERE. The text after the LAST <c>@</c> or <c>=</c> must be a
/// number, and a value whose tail is not one is REFUSED rather than quietly read as part of the name. A
/// <c>--lut</c> value therefore cannot name a file that itself contains one of those two characters -
/// nothing in the corpus does, and a silently misread name is how a smoke run comes to pass while applying
/// no table at all.
/// </para>
/// </remarks>
public sealed class SmokeLutRequest
{
    /// <summary>The character that separates the table's name from its percentage.</summary>
    public const char PercentSeparator = '@';

    /// <summary>Also accepted in place of <see cref="PercentSeparator" />.</summary>
    public const char AlternatePercentSeparator = '=';

    /// <summary>Creates the request.</summary>
    /// <param name="name">The table's file name, or part of its title.</param>
    /// <param name="applyAtPercent">How much of it to apply; clamped into 0 to 100.</param>
    public SmokeLutRequest(string name, double applyAtPercent)
    {
        Name = name ?? string.Empty;
        ApplyAtPercent = LutChainEntry.ClampPercent(applyAtPercent);
    }

    /// <summary>The table's file name, or part of its title.</summary>
    public string Name { get; }

    /// <summary>How much of it to apply, 0 to 100.</summary>
    public double ApplyAtPercent { get; }

    /// <summary>Reads a <c>--lut</c> value of the form <c>name</c> or <c>name@percent</c>.</summary>
    /// <param name="text">The value as typed.</param>
    /// <param name="request">The request the value means, or null when it means nothing.</param>
    /// <param name="error">What was wrong with the value, or an empty string when nothing was.</param>
    /// <returns>True when the value was understood.</returns>
    public static bool TryParse(string text, out SmokeLutRequest request, out string error)
    {
        request = null;
        error = string.Empty;

        var value = (text ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            error = "--lut needs the name of a lookup table, such as --lut sepia_33.cube@40.";
            return false;
        }

        var separator = Math.Max(value.LastIndexOf(PercentSeparator), value.LastIndexOf(AlternatePercentSeparator));

        if (separator < 0)
        {
            request = new SmokeLutRequest(value, LutChainEntry.DefaultApplyAtPercent);
            return true;
        }

        var name = value.Substring(0, separator).Trim();
        var percentText = value.Substring(separator + 1).Trim();

        if (name.Length == 0)
        {
            error = $"'{value}' names no lookup table before its '{value[separator]}'. "
                + "Say --lut sepia_33.cube@40.";
            return false;
        }

        if (!double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            error = $"'{percentText}' is not a percentage, so '{value}' cannot be read. "
                + "Say --lut sepia_33.cube@40, or --lut sepia_33.cube for the default of "
                + $"{LutChainEntry.DefaultApplyAtPercent:0.#}.";
            return false;
        }

        request = new SmokeLutRequest(name, percent);
        return true;
    }

    /// <summary>The value that would produce this request, so a log line shows the accepted syntax.</summary>
    /// <returns>The request as <c>name@percent</c>.</returns>
    public override string ToString() =>
        $"{Name}{PercentSeparator}{ApplyAtPercent.ToString("0.#", CultureInfo.InvariantCulture)}";
}
