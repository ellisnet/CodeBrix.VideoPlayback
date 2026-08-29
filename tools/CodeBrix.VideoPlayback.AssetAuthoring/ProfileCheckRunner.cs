using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.AssetAuthoring;

/// <summary>The streamable-profile verdict this repository's own <c>cbvinfo</c> verb returned for one file.</summary>
public sealed class ProfileCheckResult
{
    /// <summary>True when the check could be run at all.</summary>
    public bool Ran { get; set; }

    /// <summary>Why the check could not be run, when <see cref="Ran" /> is false.</summary>
    public string Unavailable { get; set; }

    /// <summary><c>cbvinfo</c>'s exit code: 0 when the file passes the profile, 1 when it does not.</summary>
    public int ExitCode { get; set; }

    /// <summary>One line per rule, exactly as <c>cbvinfo</c> printed it.</summary>
    public List<string> Rules { get; } = new List<string>();

    /// <summary>The tool's own one-line verdict.</summary>
    public string Verdict { get; set; }

    /// <summary>True when every rule passed - warnings do not count against it, exactly as the tool has it.</summary>
    public bool Passed => Ran && ExitCode == 0;

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
/// Runs this repository's <c>cbvinfo</c> verb over a finished file and keeps its profile report.
/// </summary>
/// <remarks>
/// The tool is invoked as an already-built assembly rather than through <c>dotnet run</c>, so that generating
/// the corpus never triggers a build of the project that is meant to be JUDGING the corpus. Build the
/// solution first; when the tool is not there the generator says so and carries on, because a missing
/// diagnostics build is not a reason to lose an hour of encoding.
/// </remarks>
public static class ProfileCheckRunner
{
    /// <summary>Finds the built <c>cbvinfo</c> tool, preferring a Release build.</summary>
    /// <param name="repositoryRoot">The repository's root folder.</param>
    /// <returns>The full path of the tool's assembly, or null when it has not been built.</returns>
    public static string FindTool(string repositoryRoot)
    {
        foreach (string configuration in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(
                repositoryRoot,
                "tools",
                "CodeBrix.VideoPlayback.Tools",
                "bin",
                configuration,
                "net10.0",
                "CodeBrix.VideoPlayback.Tools.dll");

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Runs <c>cbvinfo</c> over one file and keeps the profile section of what it printed.</summary>
    /// <param name="toolAssembly">The tool's assembly, from <see cref="FindTool" />; null skips the check.</param>
    /// <param name="path">The file to check.</param>
    /// <returns>The verdict, or a result saying why there is none.</returns>
    public static ProfileCheckResult Run(string toolAssembly, string path)
    {
        ProfileCheckResult result = new ProfileCheckResult();

        if (string.IsNullOrEmpty(toolAssembly))
        {
            result.Unavailable = "the cbvinfo tool has not been built (dotnet build CodeBrix.VideoPlayback.slnx -c Release)";
            return result;
        }

        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(toolAssembly);
        start.ArgumentList.Add("cbvinfo");
        start.ArgumentList.Add(path);

        StringBuilder output = new StringBuilder();

        using (Process process = Process.Start(start))
        {
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());
            process.WaitForExit();
            result.ExitCode = process.ExitCode;
        }

        result.Ran = true;

        bool inProfile = false;
        foreach (string line in output.ToString().Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("streamable profile", StringComparison.Ordinal))
            {
                inProfile = true;
                continue;
            }

            if (!inProfile) continue;

            if (trimmed.StartsWith("result", StringComparison.Ordinal))
            {
                result.Verdict = trimmed.Substring("result".Length).Trim();
                break;
            }

            string rule = trimmed.Trim();
            if (rule.Length > 0) result.Rules.Add(rule);
        }

        if (result.Rules.Count == 0 && result.Verdict == null)
        {
            result.Ran = false;
            result.Unavailable = "cbvinfo printed no profile report (exit code " + result.ExitCode + ")";
        }

        return result;
    }
}
