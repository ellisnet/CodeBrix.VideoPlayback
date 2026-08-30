using System;
using System.Globalization;

namespace CodeBrix.VideoPlayback.Authoring.Commands;

/// <summary>
/// How far an authoring run has got, reported as each pass advances.
/// </summary>
/// <remarks>
/// Progress is only reported when the request states the source's duration - FFmpeg reports a position, and
/// turning a position into a percentage needs to know what it is a percentage of.
/// </remarks>
public sealed class AuthoringProgress
{
    /// <summary>Creates a progress report.</summary>
    /// <param name="label">The pass this report is about.</param>
    /// <param name="passNumber">Which pass it is, counting from one.</param>
    /// <param name="passCount">How many passes the run has.</param>
    /// <param name="percent">How far this pass has got, 0 to 100.</param>
    public AuthoringProgress(string label, int passNumber, int passCount, int percent)
    {
        Label = label ?? string.Empty;
        PassNumber = passNumber;
        PassCount = passCount;
        Percent = percent < 0 ? 0 : percent > 100 ? 100 : percent;
    }

    /// <summary>The pass this report is about.</summary>
    public string Label { get; }

    /// <summary>Which pass it is, counting from one.</summary>
    public int PassNumber { get; }

    /// <summary>How many passes the run has - one for a WebM-profile file, two for a bespoke one.</summary>
    public int PassCount { get; }

    /// <summary>How far this pass has got, 0 to 100.</summary>
    public int Percent { get; }

    /// <inheritdoc />
    public override string ToString() =>
        Label + " (" + PassNumber.ToString(CultureInfo.InvariantCulture) + " of "
        + PassCount.ToString(CultureInfo.InvariantCulture) + "): "
        + Percent.ToString(CultureInfo.InvariantCulture) + "%";
}
