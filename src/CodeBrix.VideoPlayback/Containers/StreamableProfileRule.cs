using System;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// One rule of the streamable profile, and what the file made of it.
/// </summary>
/// <remarks>
/// <see cref="ToString" /> renders the rule exactly as the <c>cbvinfo</c> tool prints it, which is the form
/// the authoring pipeline records in its manifest.
/// </remarks>
public sealed class StreamableProfileRule
{
    /// <summary>Creates a rule result.</summary>
    /// <param name="rule">The rule, stated as the thing that must be true.</param>
    /// <param name="outcome">What the file made of it.</param>
    /// <param name="detail">What was actually found, or null when the rule needs no elaboration.</param>
    /// <exception cref="ArgumentException"><paramref name="rule" /> is null or blank.</exception>
    public StreamableProfileRule(string rule, StreamableProfileOutcome outcome, string detail = null)
    {
        if (string.IsNullOrWhiteSpace(rule)) throw new ArgumentException("A rule needs a name.", nameof(rule));

        Rule = rule;
        Outcome = outcome;
        Detail = detail ?? string.Empty;
    }

    /// <summary>The rule, stated as the thing that must be true.</summary>
    public string Rule { get; }

    /// <summary>What the file made of the rule.</summary>
    public StreamableProfileOutcome Outcome { get; }

    /// <summary>What was actually found, or an empty string when the rule needs no elaboration.</summary>
    public string Detail { get; }

    /// <summary>True when the rule is satisfied.</summary>
    public bool Passed => Outcome == StreamableProfileOutcome.Pass;

    /// <summary>The four-letter tag that opens the rendered line: <c>pass</c>, <c>warn</c> or <c>FAIL</c>.</summary>
    public string Tag
    {
        get
        {
            switch (Outcome)
            {
                case StreamableProfileOutcome.Warn: return "warn";
                case StreamableProfileOutcome.Fail: return "FAIL";
                default: return "pass";
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        "[" + Tag + "] " + Rule + (Detail.Length == 0 ? string.Empty : " - " + Detail);
}
