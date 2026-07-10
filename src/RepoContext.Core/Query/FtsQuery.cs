using System.Text;

namespace RepoContext.Core.Query;

/// <summary>Builds safe FTS5 MATCH expressions from free-text user queries.</summary>
public static class FtsQuery
{
    /// <summary>
    /// Extracts alphanumeric terms from <paramref name="query"/>, quotes each
    /// (so no FTS operator is interpreted), and OR-joins them for recall.
    /// Returns <c>null</c> when the query has no usable terms.
    /// </summary>
    public static string? Build(string query)
    {
        List<string> terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < terms.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('"').Append(terms[i].Replace("\"", "\"\"")).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>Splits text into lowercase alphanumeric terms.</summary>
    public static List<string> Tokenize(string text)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }
}
