namespace CodeBrix.VideoPlayback.Containers;

/// <summary>What one streamable-profile rule made of the file it was applied to.</summary>
public enum StreamableProfileOutcome
{
    /// <summary>The file satisfies the rule.</summary>
    Pass = 0,

    /// <summary>
    /// The file does not satisfy a RECOMMENDATION. The profile still passes: a warning describes a choice
    /// that costs a player work, not a file a player cannot open.
    /// </summary>
    Warn = 1,

    /// <summary>The file does not satisfy a REQUIREMENT, so it is not a streamable-profile file.</summary>
    Fail = 2,
}
