using System;
using System.Collections.Generic;
using System.Text;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// What every streamable-profile rule made of one file, and the verdict that follows from them.
/// </summary>
/// <remarks>
/// A report PASSES when no rule failed. Warnings never cost a file its pass - they name a choice that makes
/// a player work harder, not a file it cannot open - but they are carried into the verdict text so that a
/// pipeline recording the report keeps the whole story.
/// </remarks>
public sealed class StreamableProfileReport
{
    /// <summary>The heading the rendered report opens with.</summary>
    public const string Heading = "streamable profile";

    private readonly List<StreamableProfileRule> rules;

    /// <summary>Creates a report from the rules that were evaluated.</summary>
    /// <param name="evaluatedRules">The rules, in the order they were checked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluatedRules" /> is null.</exception>
    public StreamableProfileReport(IReadOnlyList<StreamableProfileRule> evaluatedRules)
    {
        if (evaluatedRules == null) throw new ArgumentNullException(nameof(evaluatedRules));

        rules = new List<StreamableProfileRule>(evaluatedRules.Count);

        foreach (StreamableProfileRule rule in evaluatedRules)
        {
            if (rule == null) continue;

            rules.Add(rule);
            if (rule.Outcome == StreamableProfileOutcome.Fail) Failed++;
            if (rule.Outcome == StreamableProfileOutcome.Warn) Warnings++;
        }
    }

    /// <summary>Every rule that was evaluated, in the order it was checked.</summary>
    public IReadOnlyList<StreamableProfileRule> Rules => rules;

    /// <summary>How many rules failed.</summary>
    public int Failed { get; }

    /// <summary>How many rules warned.</summary>
    public int Warnings { get; }

    /// <summary>True when no rule failed.</summary>
    public bool Passes => Failed == 0;

    /// <summary>The one-line verdict, in the words the tools print.</summary>
    public string Verdict =>
        Passes
            ? Warnings > 0 ? "passes the profile, with warnings" : "passes the profile"
            : "DOES NOT pass the profile";

    /// <summary>The rules that did not pass, for a caller that only wants the bad news.</summary>
    /// <returns>Every failing rule, in order.</returns>
    public IEnumerable<StreamableProfileRule> FailedRules()
    {
        foreach (StreamableProfileRule rule in rules)
        {
            if (rule.Outcome == StreamableProfileOutcome.Fail) yield return rule;
        }
    }

    /// <summary>
    /// The whole report as the <c>cbvinfo</c> tool prints it: the heading, one indented line per rule, a
    /// blank line, and the verdict. There is no trailing line break.
    /// </summary>
    /// <returns>The rendered report.</returns>
    public override string ToString()
    {
        StringBuilder text = new StringBuilder();
        text.Append(Heading);

        foreach (StreamableProfileRule rule in rules)
        {
            text.Append(Environment.NewLine).Append("  ").Append(rule.ToString());
        }

        text.Append(Environment.NewLine).Append(Environment.NewLine);
        text.Append("result      ").Append(Verdict);
        return text.ToString();
    }
}
