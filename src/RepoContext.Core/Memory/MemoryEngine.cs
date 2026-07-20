using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Memory;

/// <summary>Options for a memory recall (<c>repoctx memory search</c>).</summary>
public sealed record MemoryQueryOptions
{
    /// <summary>Free-text query; null lists entries instead of ranking them.</summary>
    public string? Query { get; init; }

    public int Top { get; init; } = 10;

    /// <summary>Restrict to one of <see cref="MemoryKinds"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>Restrict to entries linking this repo-relative path.</summary>
    public string? File { get; init; }

    /// <summary>
    /// Active session: its short-term entries become visible in addition to
    /// the long-term ones. Without it only long-term entries are considered.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>Return only stale entries (curation helper).</summary>
    public bool StaleOnly { get; init; }
}

/// <summary>A recalled memory with its explanation, staleness and token cost.</summary>
public sealed record MemoryHit
{
    public required MemoryEntry Entry { get; init; }

    /// <summary>Relevance in [0,1]; 0 in list mode (no query).</summary>
    public required double Score { get; init; }

    /// <summary>Machine-readable match reasons (<c>term:</c>, <c>tag:</c>, <c>file:</c>).</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    /// True when a linked file's indexed content hash no longer matches the
    /// hash recorded at write time (or the file left the index): the knowledge
    /// predates the current code and should be verified before trusting it.
    /// </summary>
    public required bool Stale { get; init; }

    /// <summary>The linked paths that drifted (present only when stale).</summary>
    public IReadOnlyList<string> StaleFiles { get; init; } = [];

    /// <summary>What consuming this memory costs (its text, calibrated).</summary>
    public required int EstimatedTokens { get; init; }
}

/// <summary>The result of <c>repoctx memory search</c>.</summary>
public sealed record MemoryQueryResult
{
    public string? Query { get; init; }

    public required IReadOnlyList<string> Terms { get; init; }

    public required IReadOnlyList<MemoryHit> Hits { get; init; }

    /// <summary>Live entries visible in the queried scope, before filters.</summary>
    public required int TotalEntries { get; init; }

    public required int EstimatedTokens { get; init; }
}

/// <summary>
/// Deterministic recall over the memory store (ADR 0013). Scoring is a plain
/// term overlap — text and tag matches count full, path matches half — with
/// stable ordering (score DESC, id ASC; creation date DESC in list mode), so
/// identical store + index + query ⇒ identical output, and every hit carries
/// reasons. No clock is consulted: staleness comes from content hashes, never
/// from age.
/// </summary>
public static class MemoryEngine
{
    private const double PathMatchWeight = 0.5;

    /// <summary>Runs a recall over <paramref name="entries"/>.</summary>
    public static MemoryQueryResult Search(
        IReadOnlyList<MemoryEntry> entries, MemoryQueryOptions options,
        RepoctxConfig config, IndexStore store)
    {
        TokenScale scale = TokenScale.From(config);
        AnalyzedQuery? analyzed = options.Query is { Length: > 0 } q
            ? QueryAnalyzer.Analyze(q, config)
            : null;

        List<MemoryEntry> visible = entries
            .Where(e => e.Session is null || (options.Session is not null && e.Session == options.Session))
            .ToList();

        IEnumerable<MemoryEntry> filtered = visible;
        if (MemoryKinds.IsValid(options.Kind))
        {
            filtered = filtered.Where(e => e.Kind == options.Kind);
        }

        if (options.File is { Length: > 0 } file)
        {
            filtered = filtered.Where(e => e.Files.ContainsKey(file));
        }

        var hits = new List<MemoryHit>();
        foreach (MemoryEntry entry in filtered)
        {
            (double score, List<string> reasons) = analyzed is null
                ? (0d, new List<string>())
                : Match(entry, analyzed.Terms);
            if (analyzed is not null && score <= 0)
            {
                continue;
            }

            (bool stale, IReadOnlyList<string> staleFiles) = Staleness(entry, store);
            if (options.StaleOnly && !stale)
            {
                continue;
            }

            hits.Add(new MemoryHit
            {
                Entry = entry,
                Score = Math.Round(score, 4, MidpointRounding.AwayFromZero),
                Reasons = reasons,
                Stale = stale,
                StaleFiles = staleFiles,
                EstimatedTokens = scale.Apply(Tokens.Count(entry.Text)),
            });
        }

        List<MemoryHit> ordered = (analyzed is null
                ? hits.OrderByDescending(h => h.Entry.Created, StringComparer.Ordinal)
                : hits.OrderByDescending(h => h.Score)
                      .ThenByDescending(h => h.Entry.Created, StringComparer.Ordinal))
            .ThenBy(h => h.Entry.Id, StringComparer.Ordinal)
            .Take(options.Top)
            .ToList();

        return new MemoryQueryResult
        {
            Query = options.Query,
            Terms = analyzed?.Terms ?? [],
            Hits = ordered,
            TotalEntries = visible.Count,
            EstimatedTokens = ordered.Sum(h => h.EstimatedTokens),
        };
    }

    /// <summary>
    /// Scores one entry against the query terms: the matched fraction, where a
    /// term found in the text or tags counts 1 and one merely present in a
    /// linked path counts <see cref="PathMatchWeight"/>. Reasons record every
    /// match the score is made of.
    /// </summary>
    internal static (double Score, List<string> Reasons) Match(
        MemoryEntry entry, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return (0, []);
        }

        var textTerms = new HashSet<string>(FtsQuery.Tokenize(entry.Text), StringComparer.Ordinal);
        var reasons = new List<string>();
        double matched = 0;
        foreach (string term in terms)
        {
            if (textTerms.Contains(term))
            {
                matched += 1;
                reasons.Add("term:" + term);
                continue;
            }

            if (entry.Tags.Contains(term, StringComparer.Ordinal))
            {
                matched += 1;
                reasons.Add("tag:" + term);
                continue;
            }

            string? pathHit = entry.Files.Keys.FirstOrDefault(
                p => p.ToLowerInvariant().Contains(term, StringComparison.Ordinal));
            if (pathHit is not null)
            {
                matched += PathMatchWeight;
                reasons.Add("file:" + pathHit);
            }
        }

        return (matched / terms.Count, reasons);
    }

    /// <summary>
    /// A memory is stale when any linked file's current indexed hash no longer
    /// starts with the short hash recorded at write time, or the file is gone
    /// from the index. Deterministic: a pure function of store + index.
    /// </summary>
    internal static (bool Stale, IReadOnlyList<string> StaleFiles) Staleness(
        MemoryEntry entry, IndexStore store)
    {
        List<string> drifted = [];
        foreach ((string path, string hash) in entry.Files)
        {
            FileRow? row = store.FindFile(path);
            if (row is null || !Hashes.Matches(row.Value.ContentHash, hash))
            {
                drifted.Add(path);
            }
        }

        return (drifted.Count > 0, drifted);
    }
}
