using System.Text;

namespace RepoContext.Core.Parsing;

/// <summary>Splits identifiers into lowercase word tokens for search.</summary>
public static class Identifiers
{
    /// <summary>
    /// Splits <paramref name="identifier"/> on camelCase, snake_case, kebab-case
    /// and letter/digit boundaries. E.g. <c>loginUser</c> → [login, user],
    /// <c>APIController</c> → [api, controller], <c>create_session</c> →
    /// [create, session]. Returns lowercase tokens (duplicates removed, order kept).
    /// </summary>
    public static IReadOnlyList<string> Split(string identifier)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
        }

        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];
            if (c is '_' or '-' or ' ' or '.')
            {
                Flush();
                continue;
            }

            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                char prev = current[^1];
                bool lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                bool digitBoundary = char.IsDigit(prev) != char.IsDigit(c);
                // Acronym boundary: "APIController" -> API | Controller.
                bool acronymEnd = char.IsUpper(prev) && char.IsUpper(c)
                    && i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);

                if (lowerToUpper || digitBoundary || acronymEnd)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();

        // Preserve order, drop duplicates.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string t in tokens)
        {
            if (seen.Add(t))
            {
                result.Add(t);
            }
        }

        return result;
    }

    /// <summary>The split tokens joined by spaces (for FTS content).</summary>
    public static string SplitJoined(string identifier) => string.Join(' ', Split(identifier));
}
