using System;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// The input-range arithmetic the two lookup-table types share.
/// </summary>
/// <remarks>
/// A ".cube" file may declare the range of input its table covers - <c>DOMAIN_MIN</c> and <c>DOMAIN_MAX</c>,
/// three numbers each. Every table therefore carries a domain, the overwhelmingly common one being 0 to 1;
/// this class validates one, recognises the default, and does the normalisation a lookup starts with.
/// </remarks>
internal static class LutDomain
{
    /// <summary>The number of channels a domain bound states.</summary>
    internal const int Channels = 3;

    /// <summary>Copies and checks a domain bound, filling in a default when none was given.</summary>
    /// <param name="values">The three numbers, or null.</param>
    /// <param name="fallback">The value every channel takes when <paramref name="values" /> is null.</param>
    /// <param name="parameterName">The name to blame in an exception.</param>
    /// <returns>A private array of three finite numbers.</returns>
    /// <exception cref="ArgumentException">The array is not three numbers, or one is not finite.</exception>
    internal static float[] Validate(float[] values, float fallback, string parameterName)
    {
        if (values == null) return new[] { fallback, fallback, fallback };

        if (values.Length != Channels)
        {
            throw new ArgumentException(
                $"A lookup table's input domain states one number per channel - three of them; this one "
                + $"holds {values.Length}.",
                parameterName);
        }

        float[] copy = new float[Channels];
        for (int channel = 0; channel < Channels; channel++)
        {
            float value = values[channel];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException(
                    $"A lookup table's input domain must be finite numbers; channel {channel} holds "
                    + value.ToString("R", CultureInfo.InvariantCulture) + ".",
                    parameterName);
            }

            copy[channel] = value;
        }

        return copy;
    }

    /// <summary>Checks that every channel's maximum is above its minimum.</summary>
    /// <param name="minimum">The lower bound of each channel.</param>
    /// <param name="maximum">The upper bound of each channel.</param>
    /// <param name="parameterName">The name to blame in an exception.</param>
    /// <exception cref="ArgumentException">A channel's maximum is not above its minimum.</exception>
    internal static void EnsureOrdered(float[] minimum, float[] maximum, string parameterName)
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            if (maximum[channel] > minimum[channel]) continue;

            throw new ArgumentException(
                "A lookup table's input domain must rise: channel "
                + channel.ToString(CultureInfo.InvariantCulture) + " runs from "
                + minimum[channel].ToString("R", CultureInfo.InvariantCulture) + " to "
                + maximum[channel].ToString("R", CultureInfo.InvariantCulture) + ".",
                parameterName);
        }
    }

    /// <summary>True when the domain is the usual 0 to 1 on all three channels.</summary>
    /// <param name="minimum">The lower bound of each channel.</param>
    /// <param name="maximum">The upper bound of each channel.</param>
    /// <returns>True when nothing needs remapping before a lookup.</returns>
    internal static bool IsDefault(float[] minimum, float[] maximum)
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            if (minimum[channel] != 0f || maximum[channel] != 1f) return false;
        }

        return true;
    }

    /// <summary>Maps a value through a channel's domain onto 0 to 1, clamping what falls outside.</summary>
    /// <param name="value">The input value.</param>
    /// <param name="minimum">The value that maps to 0.</param>
    /// <param name="maximum">The value that maps to 1.</param>
    /// <returns>The normalised position, between 0 and 1.</returns>
    internal static float Normalise(float value, float minimum, float maximum) =>
        Clamp01((value - minimum) / (maximum - minimum));

    /// <summary>Holds a value between 0 and 1.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, clamped. A not-a-number becomes 0.</returns>
    internal static float Clamp01(float value)
    {
        if (value > 0f) return value > 1f ? 1f : value;
        return 0f;
    }

    /// <summary>Writes a domain out for a diagnostic string.</summary>
    /// <param name="minimum">The lower bound of each channel.</param>
    /// <param name="maximum">The upper bound of each channel.</param>
    /// <returns>A short phrase naming the range, beginning with a space.</returns>
    internal static string Describe(float[] minimum, float[] maximum) =>
        string.Format(
            CultureInfo.InvariantCulture,
            " over [{0}, {1}] [{2}, {3}] [{4}, {5}]",
            minimum[0],
            maximum[0],
            minimum[1],
            maximum[1],
            minimum[2],
            maximum[2]);
}
