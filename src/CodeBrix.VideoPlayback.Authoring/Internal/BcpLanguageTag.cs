namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// Checks that a language tag is WELL FORMED - the shape BCP 47 defines, not the registry it points at.
/// </summary>
/// <remarks>
/// A tag is a primary subtag of two to eight letters, followed by any number of subtags of one to eight
/// letters or digits, separated by hyphens: <c>en</c>, <c>en-GB</c>, <c>zh-Hant-TW</c>, <c>de-1901</c>. The
/// registry itself is not consulted, and deliberately: a container carries whatever tag the author wrote,
/// and refusing a real language because a table in this library had not heard of it would be worse than
/// useless. What IS caught is the common mistake - an underscore instead of a hyphen, a bare letter, a
/// whole word where a tag belongs.
/// </remarks>
internal static class BcpLanguageTag
{
    /// <summary>Says whether a tag is well formed.</summary>
    /// <param name="tag">The tag to check.</param>
    /// <returns>True when it is; false for null, blank or malformed.</returns>
    internal static bool IsWellFormed(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;

        int subtagLength = 0;
        bool firstSubtag = true;
        bool firstSubtagIsAllLetters = true;

        for (int i = 0; i <= tag.Length; i++)
        {
            if (i == tag.Length || tag[i] == '-')
            {
                if (subtagLength < 1 || subtagLength > 8) return false;

                if (firstSubtag)
                {
                    if (subtagLength < 2 || !firstSubtagIsAllLetters) return false;
                    firstSubtag = false;
                }

                subtagLength = 0;
                continue;
            }

            char c = tag[i];
            bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            bool digit = c >= '0' && c <= '9';
            if (!letter && !digit) return false;

            if (firstSubtag && !letter) firstSubtagIsAllLetters = false;
            subtagLength++;
        }

        return true;
    }
}
