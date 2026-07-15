using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Indexing;
using RepoContext.Core.Outline;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Context;

/// <summary>
/// The context pipeline (spec chapter 6): query analysis, candidate generation
/// (FTS, symbols, path), 1-hop graph expansion, config-weighted scoring,
/// vendor/generated penalty, directory diversity and token budgeting. Every
/// result carries machine-readable reasons. Deterministic.
/// </summary>
public sealed class ContextEngine
{
    private const double GraphDecay = 0.5;
    private const double VendorPenalty = 0.3;
    private const double DiversityFactor = 0.9;
    private const int MaxHops = 2;
    private const int MaxOutlineSymbols = 12;
    private const int MaxSliceLines = 80;
    private const int PreambleFallbackLines = 20;
    private const int ItemEnvelopeOverhead = 40;
    private const int SymbolFramingTokens = 26;

    private readonly IndexStore _store;
    private readonly RepoctxConfig _config;
    private readonly TokenScale _scale;

    public ContextEngine(IndexStore store, RepoctxConfig config)
    {
        _store = store;
        _config = config;
        _scale = TokenScale.From(config);
    }

    public ContextResult Run(string query, ContextOptions options)
    {
        AnalyzedQuery analyzed = QueryAnalyzer.Analyze(query, _config);
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        IReadOnlyList<FileRow> files = _store.GetFiles();
        var fileByPath = files.ToDictionary(f => f.Path, f => f, StringComparer.Ordinal);

        GenerateCandidates(analyzed, files, candidates);
        ExpandGraph(candidates, fileByPath);
        ScoreCandidates(candidates);

        List<Candidate> ordered = ApplyDiversity(candidates.Values);
        List<ContextItem> items = Budget(ordered, options, fileByPath);
        int scored = ordered.Count(c => c.Score > 0);

        return new ContextResult
        {
            Query = query,
            Terms = analyzed.Terms,
            State = Hashes.Short(_store.GetMeta(MetaKeys.StateHash) ?? string.Empty),
            Detail = options.Detail,
            TokenProfile = _scale.Label,
            Items = items,
            TotalCandidates = candidates.Count,
            Omitted = scored - items.Count,
            EstimatedTokens = items.Sum(i => i.EstimatedTokens),
        };
    }

    private void GenerateCandidates(
        AnalyzedQuery analyzed, IReadOnlyList<FileRow> files, Dictionary<string, Candidate> candidates)
    {
        if (analyzed.FtsMatch is not null)
        {
            foreach (SearchHit hit in _store.Search(analyzed.FtsMatch, 500))
            {
                Candidate c = GetOrAdd(candidates, hit.Path);
                c.Fts = Math.Max(c.Fts, hit.Score);
                c.BestChunkStart = hit.StartLine;
                c.BestChunkEnd = hit.EndLine;
                c.Reasons.Add("fts");
            }

            foreach (SearchHit hit in _store.Search(analyzed.FtsMatch, 500, symbolsOnly: true))
            {
                Candidate c = GetOrAdd(candidates, hit.Path);
                if (hit.Score > c.Symbol)
                {
                    c.Symbol = hit.Score;
                    c.BestSymbolStart = hit.StartLine;
                    c.BestSymbolEnd = hit.EndLine;
                }

                if (hit.Heading is { Length: > 0 } name)
                {
                    c.Reasons.Add($"symbol:{name}");
                }
            }
        }

        foreach (FileRow file in files)
        {
            string lower = file.Path.ToLowerInvariant();
            int matches = analyzed.Terms.Count(t => lower.Contains(t, StringComparison.Ordinal));

            // A file whose name stem is exactly a query term (login.ts for "login")
            // is a strong signal - stronger than an incidental substring.
            string stem = System.IO.Path.GetFileNameWithoutExtension(lower);
            bool exactStem = analyzed.Terms.Contains(stem);

            if (matches > 0 || exactStem)
            {
                Candidate c = GetOrAdd(candidates, file.Path);
                c.PathScore = matches + (exactStem ? 2 : 0);
                c.Reasons.Add(exactStem ? "path-name-match" : "path-match");
            }
        }
    }

    /// <summary>
    /// Bounded breadth-first expansion over the graph. The spec calls for
    /// "1-hop"; we expand up to <see cref="MaxHops"/> = 2 with per-hop decay so a
    /// dependency of a dependency (e.g. middleware → session ← login) still
    /// surfaces, ranked well below the direct match. See ADR 0006.
    /// </summary>
    private void ExpandGraph(Dictionary<string, Candidate> candidates, Dictionary<string, FileRow> fileByPath)
    {
        List<Candidate> seeds = candidates.Values
            .Where(c => c.Fts > 0 || c.Symbol > 0 || c.PathScore > 0)
            .ToList();
        double ftsMax = Max(seeds.Select(s => s.Fts));

        // Frontier: path -> strength carried into the next hop.
        var frontier = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Candidate seed in seeds)
        {
            double strength = ftsMax > 0 && seed.Fts > 0 ? seed.Fts / ftsMax
                : seed.Symbol > 0 ? 0.8
                : 0.5;
            frontier[seed.Path] = Math.Max(frontier.GetValueOrDefault(seed.Path), strength);
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        for (int hop = 0; hop < MaxHops && frontier.Count > 0; hop++)
        {
            var next = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach ((string path, double strength) in frontier)
            {
                if (!expanded.Add(path) || !fileByPath.TryGetValue(path, out FileRow row))
                {
                    continue;
                }

                LinkAll(candidates, row.Id, path, strength, next);
            }

            frontier = next;
        }
    }

    private void LinkAll(
        Dictionary<string, Candidate> candidates, long fileId, string fromPath,
        double strength, Dictionary<string, double> next)
    {
        Link(candidates, fileId, EdgeKind.Import, outgoing: true, strength, "imported-by:" + fromPath, next);
        Link(candidates, fileId, EdgeKind.Import, outgoing: false, strength, "imports:" + fromPath, next);
        Link(candidates, fileId, EdgeKind.Test, outgoing: true, strength, "tested-by:" + fromPath, next);
        Link(candidates, fileId, EdgeKind.Test, outgoing: false, strength, "test-of:" + fromPath, next);
    }

    private void Link(
        Dictionary<string, Candidate> candidates, long fileId, string kind, bool outgoing,
        double strength, string reason, Dictionary<string, double> next)
    {
        double contribution = strength * GraphDecay;
        foreach (string path in _store.GetNeighbors(fileId, kind, outgoing))
        {
            Candidate c = GetOrAdd(candidates, path);
            c.Graph += contribution;
            c.Reasons.Add(reason);
            next[path] = Math.Max(next.GetValueOrDefault(path), contribution);
        }
    }

    private void ScoreCandidates(Dictionary<string, Candidate> candidates)
    {
        double ftsMax = Max(candidates.Values.Select(c => c.Fts));
        double symMax = Max(candidates.Values.Select(c => c.Symbol));
        double graphMax = Max(candidates.Values.Select(c => c.Graph));
        double pathMax = Max(candidates.Values.Select(c => (double)c.PathScore));
        RankingWeights w = _config.Ranking.Weights;

        foreach (Candidate c in candidates.Values)
        {
            double score =
                w.Fts * Normalize(c.Fts, ftsMax) +
                w.Symbol * Normalize(c.Symbol, symMax) +
                w.Graph * Normalize(c.Graph, graphMax) +
                w.Path * Normalize(c.PathScore, pathMax);

            if (IsVendorOrGenerated(c.Path))
            {
                score *= VendorPenalty;
                c.Reasons.Add("penalty:vendor-or-generated");
            }

            c.Score = score;
        }
    }

    private static List<Candidate> ApplyDiversity(IEnumerable<Candidate> candidates)
    {
        // Deterministic base order, then demote repeated directories.
        List<Candidate> ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        var perDir = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Candidate c in ordered)
        {
            string dir = Directory(c.Path);
            int prior = perDir.GetValueOrDefault(dir);
            c.AdjustedScore = c.Score * Math.Pow(DiversityFactor, prior);
            perDir[dir] = prior + 1;
        }

        return ordered
            .OrderByDescending(c => c.AdjustedScore)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Packs ranked candidates into the bundle. The charge per item is what
    /// the agent will actually spend consuming it at the chosen detail level:
    /// the real full-file token count for <see cref="ContextDetail.Paths"/>,
    /// the included outline or slice otherwise, and zero for a file the
    /// caller already has (<see cref="ContextOptions.Known"/>). A candidate
    /// that does not fit the remaining budget is skipped, not a stop signal —
    /// smaller files further down may still fit. The first item is always
    /// admitted so a tight budget never yields an empty bundle.
    /// </summary>
    private List<ContextItem> Budget(
        List<Candidate> ordered, ContextOptions options, Dictionary<string, FileRow> fileByPath)
    {
        var items = new List<ContextItem>();
        int usedTokens = 0;

        foreach (Candidate c in ordered)
        {
            if (items.Count >= options.Top)
            {
                break;
            }

            if (options.BudgetTokens is { } spent && usedTokens >= spent)
            {
                break;
            }

            if (c.Score <= 0 || !fileByPath.TryGetValue(c.Path, out FileRow row))
            {
                continue;
            }

            ContextItem item = BuildItem(c, row, options);
            if (options.BudgetTokens is { } budget && items.Count > 0
                && usedTokens + item.EstimatedTokens > budget)
            {
                continue;
            }

            items.Add(item);
            usedTokens += item.EstimatedTokens;
        }

        return items;
    }

    private ContextItem BuildItem(Candidate c, FileRow row, ContextOptions options)
    {
        // Symbol hits give the most precise range; fall back to the best FTS
        // chunk, then to the file preamble.
        int start = c.BestSymbolStart > 0 ? c.BestSymbolStart
            : c.BestChunkStart > 0 ? c.BestChunkStart : 1;
        int end = c.BestSymbolStart > 0 ? c.BestSymbolEnd
            : c.BestChunkEnd > 0 ? c.BestChunkEnd
            : Math.Min(PreambleFallbackLines, Math.Max(row.LineCount, 1));

        var item = new ContextItem
        {
            Path = c.Path,
            Kind = row.Kind,
            Score = Math.Round(c.AdjustedScore, 4, MidpointRounding.AwayFromZero),
            StartLine = start,
            EndLine = end,
            Reasons = ReasonCompression.Compress(c.Reasons),
            Hash = Hashes.Short(row.ContentHash),
            EstimatedTokens = _scale.Apply(row.TokenCount),
        };

        bool unchanged = options.Known is { } known
            && known.TryGetValue(c.Path, out string? givenHash)
            && Hashes.Matches(row.ContentHash, givenHash);
        if (unchanged)
        {
            // The caller already has this file; the pointer costs (almost) nothing.
            return item with { Unchanged = true, EstimatedTokens = 0 };
        }

        switch (options.Detail)
        {
            case ContextDetail.Outline:
                (IReadOnlyList<OutlineSymbol> symbols, int cut) =
                    Core.Outline.Outline.Symbols(_store, row.Id, MaxOutlineSymbols);
                return item with
                {
                    Symbols = symbols,
                    SymbolsOmitted = cut > 0 ? cut : null,
                    EstimatedTokens = _scale.Apply(
                        OutlineTokens(symbols) + symbols.Count * SymbolFramingTokens
                        + EnvelopeTokens(item)),
                    FileTokens = _scale.Apply(row.TokenCount),
                };

            case ContextDetail.Slices:
                int cappedEnd = Math.Min(end, start + MaxSliceLines - 1);
                if (_store.GetSourceSlice(c.Path, start, cappedEnd) is { } slice)
                {
                    return item with
                    {
                        StartLine = slice.StartLine,
                        EndLine = slice.EndLine,
                        Snippet = slice.Text,
                        EstimatedTokens = _scale.Apply(JsonTextTokens(slice.Text) + EnvelopeTokens(item)),
                        FileTokens = _scale.Apply(row.TokenCount),
                    };
                }

                return item;

            default:
                return item;
        }
    }

    /// <summary>The response cost of an outline: its signatures and doc lines.</summary>
    private static int OutlineTokens(IReadOnlyList<OutlineSymbol> symbols) =>
        symbols.Count == 0
            ? 0
            : Tokens.Count(string.Join('\n',
                symbols.Select(s => s.Doc is null ? s.Signature : s.Signature + " " + s.Doc)));

    /// <summary>
    /// Embedded content is billed in its serialized form: JSON escaping (\n,
    /// \", \\) is part of what the agent's context window receives, and on
    /// source code it adds a solid 10-20 % over the raw text.
    /// </summary>
    private static int JsonTextTokens(string text) =>
        Tokens.Count(System.Text.Json.JsonEncodedText.Encode(text).Value);

    /// <summary>
    /// The per-item JSON framing an agent also pays for when content is
    /// embedded: path, reasons and a flat allowance for field names, numbers
    /// and the hash. Charged so a slices/outline budget tracks the real
    /// response size instead of only the content inside it. Paths detail
    /// deliberately charges the full-read cost alone — there the follow-up
    /// read dominates, not the response.
    /// </summary>
    private static int EnvelopeTokens(ContextItem item) =>
        ItemEnvelopeOverhead
        + Tokens.Count(item.Path)
        + Tokens.Count(string.Join(',', item.Reasons));

    private static Candidate GetOrAdd(Dictionary<string, Candidate> candidates, string path)
    {
        if (!candidates.TryGetValue(path, out Candidate? c))
        {
            c = new Candidate { Path = path };
            candidates[path] = c;
        }

        return c;
    }

    private static double Normalize(double value, double max) => max > 0 ? value / max : 0;

    private static double Max(IEnumerable<double> values)
    {
        double max = 0;
        foreach (double v in values)
        {
            if (v > max)
            {
                max = v;
            }
        }

        return max;
    }

    private static bool IsVendorOrGenerated(string path)
    {
        string p = path.ToLowerInvariant();
        return p.Contains(".min.", StringComparison.Ordinal)
            || p.Contains(".generated.", StringComparison.Ordinal)
            || p.Contains("/vendor/", StringComparison.Ordinal)
            || p.Contains("/dist/", StringComparison.Ordinal)
            || p.StartsWith("vendor/", StringComparison.Ordinal);
    }

    private static string Directory(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private sealed class Candidate
    {
        public required string Path { get; init; }

        public double Fts { get; set; }

        public double Symbol { get; set; }

        public double Graph { get; set; }

        public int PathScore { get; set; }

        public double Score { get; set; }

        public double AdjustedScore { get; set; }

        public int BestChunkStart { get; set; }

        public int BestChunkEnd { get; set; }

        public int BestSymbolStart { get; set; }

        public int BestSymbolEnd { get; set; }

        public List<string> Reasons { get; } = [];
    }
}
