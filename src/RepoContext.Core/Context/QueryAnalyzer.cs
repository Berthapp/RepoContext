using RepoContext.Core.Configuration;
using RepoContext.Core.Query;

namespace RepoContext.Core.Context;

/// <summary>A natural-language query reduced to effective search terms.</summary>
public sealed record AnalyzedQuery(string Original, IReadOnlyList<string> Terms, string? FtsMatch);

/// <summary>
/// Analyzes a context query: tokenize, drop DE/EN stop words, expand config
/// synonyms (spec chapter 6).
/// </summary>
public static class QueryAnalyzer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "a", "an", "the", "to", "of", "in", "on", "at", "for", "and", "or", "is",
        "are", "be", "i", "want", "wanna", "would", "like", "my", "me", "it",
        "this", "that", "with", "how", "do", "need", "please", "can", "should",
        // German
        "ich", "möchte", "moechte", "will", "der", "die", "das", "den", "dem",
        "ein", "eine", "einen", "und", "oder", "zu", "von", "mit", "für", "fuer",
        "wie", "soll", "möchten", "im", "am",
    };

    public static AnalyzedQuery Analyze(string query, RepoctxConfig config)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string term)
        {
            if (term.Length > 0 && seen.Add(term))
            {
                terms.Add(term);
            }
        }

        foreach (string token in FtsQuery.Tokenize(query))
        {
            if (StopWords.Contains(token))
            {
                continue;
            }

            AddTerm(token);
            if (config.Ranking.Synonyms.TryGetValue(token, out IReadOnlyList<string>? synonyms))
            {
                foreach (string synonym in synonyms)
                {
                    foreach (string s in FtsQuery.Tokenize(synonym))
                    {
                        AddTerm(s);
                    }
                }
            }
        }

        string? match = terms.Count == 0
            ? null
            : string.Join(" OR ", terms.Select(t => $"\"{t}\""));

        return new AnalyzedQuery(query, terms, match);
    }
}
