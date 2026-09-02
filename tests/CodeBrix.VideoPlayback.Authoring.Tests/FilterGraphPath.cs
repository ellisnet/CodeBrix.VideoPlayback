using System.Text;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Writes a file path the way it appears INSIDE an FFmpeg filtergraph, so that a test can assert on the
/// path a rendered command actually names.
/// </summary>
/// <remarks>
/// <para>
/// A filtergraph is parsed twice on its way to a filter's own options, so a character that means something
/// to either parser is escaped for both. The wrapper does that escaping; this repeats it, because a test
/// that wants to say "the command names THIS file" has to say it in the form the command carries.
/// </para>
/// <para>
/// It matters only off POSIX. A Linux or macOS path has neither of the two characters that need escaping,
/// so this hands back exactly what it was given and those assertions read as they always did. A Windows
/// path has both - a separator on every segment and a colon after the drive letter - and an expectation
/// written without them passes on the machines that never see one and fails on the machines that do.
/// </para>
/// <para>
/// The rule itself is pinned independently, on a fixed path and against a literal, by
/// <c>CbvAuthorTests.RenderCommands_escapes_a_lookup_table_path_carrying_a_colon_and_a_space</c>.
/// </para>
/// </remarks>
internal static class FilterGraphPath
{
    /// <summary>Escapes a path for a filtergraph, exactly as the rendered command spells it.</summary>
    /// <param name="path">The path as the caller stated it.</param>
    /// <returns>The path as the command line carries it.</returns>
    internal static string Escape(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        StringBuilder escaped = new StringBuilder(path.Length);

        foreach (char character in path)
        {
            switch (character)
            {
                // A backslash survives both unescape passes only by being doubled for each of them.
                case '\\':
                    escaped.Append("\\\\\\\\");
                    break;

                // A colon ends an option in the filter's own parser, so it is escaped there and that
                // escape is itself carried through the filtergraph parser - three backslashes, not four.
                case ':':
                    escaped.Append("\\\\\\:");
                    break;

                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }
}
