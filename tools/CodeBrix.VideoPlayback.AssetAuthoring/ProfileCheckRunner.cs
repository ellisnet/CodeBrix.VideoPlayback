using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Containers;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>The streamable-profile verdict for one finished file.</summary>
public sealed class ProfileCheckResult
{
    /// <summary>True when the check could be run at all.</summary>
    public bool Ran { get; set; }

    /// <summary>Why the check could not be run, when <see cref="Ran" /> is false.</summary>
    public string Unavailable { get; set; }

    /// <summary>The report the library produced, or null when the check could not be run.</summary>
    public StreamableProfileReport Report { get; set; }

    /// <summary>One line per rule, exactly as <c>cbvinfo</c> prints it.</summary>
    public List<string> Rules { get; } = new List<string>();

    /// <summary>The one-line verdict.</summary>
    public string Verdict { get; set; }

    /// <summary>True when every rule passed - warnings do not count against it, exactly as the tool has it.</summary>
    public bool Passed => Ran && Report != null && Report.Passes;

    /// <summary>The rules that did not pass, for a report that only wants the bad news.</summary>
    /// <returns>Every rule line marked FAIL.</returns>
    public IEnumerable<string> FailedRules()
    {
        foreach (string rule in Rules)
        {
            if (rule.Contains("[FAIL]")) yield return rule;
        }
    }
}

/// <summary>
/// Judges a finished file against the streamable profile.
/// </summary>
/// <remarks>
/// <para>
/// This USED to start <c>dotnet</c>, run this repository's <c>cbvinfo</c> verb as a child process and parse
/// what it printed. It no longer does: the rules live in the playback library as
/// <see cref="StreamableProfile" />, and both this generator and <c>cbvinfo</c> call the same code. So the
/// check no longer needs the tools project to have been built, it costs no process, and there is no output
/// text between the rule and the answer to go wrong.
/// </para>
/// <para>
/// It reads both container flavours and decodes nothing, so it runs with no codec package installed.
/// </para>
/// </remarks>
public static class ProfileCheckRunner
{
    /// <summary>Checks one file against the streamable profile.</summary>
    /// <param name="path">The file to check.</param>
    /// <returns>The verdict, or a result saying why there is none.</returns>
    public static ProfileCheckResult Run(string path)
    {
        ProfileCheckResult result = new ProfileCheckResult();

        try
        {
            StreamableProfileReport report = StreamableProfile.EvaluateFile(path);
            result.Ran = true;
            result.Report = report;
            result.Verdict = report.Verdict;

            foreach (StreamableProfileRule rule in report.Rules) result.Rules.Add(rule.ToString());
        }
        catch (Exception ex)
        {
            result.Ran = false;
            result.Unavailable = "the file could not be read: " + ex.Message;
        }

        return result;
    }

    /// <summary>Records a check that was deliberately not made.</summary>
    /// <param name="reason">Why it was skipped.</param>
    /// <returns>A result saying so.</returns>
    public static ProfileCheckResult Skipped(string reason) =>
        new ProfileCheckResult { Ran = false, Unavailable = reason };

    /// <summary>Wraps a report the authoring library already produced, so the file is not read twice.</summary>
    /// <param name="report">The report, or null when the library did not make one.</param>
    /// <returns>The verdict in this tool's shape.</returns>
    public static ProfileCheckResult From(StreamableProfileReport report)
    {
        if (report == null) return Skipped("the authoring library was asked not to validate the profile");

        ProfileCheckResult result = new ProfileCheckResult
        {
            Ran = true,
            Report = report,
            Verdict = report.Verdict,
        };

        foreach (StreamableProfileRule rule in report.Rules) result.Rules.Add(rule.ToString());

        return result;
    }
}
