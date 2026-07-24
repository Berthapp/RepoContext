using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;
using RepoContext.Core.Outline;
using RepoContext.Core.Parsing;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Context;

/// <summary>
/// The context pipeline (spec chapter 6): query analysis, candidate generation
/// (FTS, symbols, path), bounded graph expansion, config-weighted scoring,
/// vendor/generated penalty, directory diversity, evidence selection and
/// budgeting. Every result carries machine-readable reasons. Deterministic.
/// </summary>
/// <remarks>
/// Release 1 reshaped the tail of this pipeline (ADR 0015/0016): a candidate now
/// retains several pieces of evidence rather than one best range, evidence is
/// emitted as independently reusable units each carrying its own receipt, reused
/// units are acknowledged without consuming a result slot, and budgets are
/// enforced against the exact rendered response instead of an allowance.
/// </remarks>
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
    private const int MaxContextMemories = 3;
    private const int MemoryBudgetDivisor = 5;
    private const int MemoryEnvelopeOverhead = 24;
    private const double MemoryTermWeight = 0.7;
    private const double MemoryOverlapWeight = 0.3;

    /// <summary>Per-file, per-channel evidence cap; stops one repetitive file starving the rest.</summary>
    private const int MaxHitsPerFilePerChannel = 8;

    private const int MaxEvidenceHits = 500;

    private readonly IndexStore _store;
    private readonly RepoctxConfig _config;
    private readonly TokenScale _scale;

    public ContextEngine(IndexStore store, RepoctxConfig config)
    {
        _store = store;
        _config = config;
        _scale = TokenScale.From(config);
    }

    /// <summary>
    /// Runs the pipeline. <paramref name="cost"/> supplies the exact rendered
    /// cost of a tentative response and is required only when
    /// <see cref="ContextOptions.ResponseBudgetTokens"/> is set; the CLI and the
    /// MCP server pass models backed by the same renderer.
    /// </summary>
    public ContextResult Run(string query, ContextOptions options, IResponseCostModel? cost = null)
    {
        if (!_store.IsSchemaCurrent
            || !_store.IsProducerCurrent
            || !_store.HasValidStateHash
            || !_store.IsIndexConfigCurrent(_config))
        {
            throw new InvalidOperationException(
                "The repository index is stale or missing required identity metadata; re-index before querying.");
        }

        AnalyzedQuery analyzed = QueryAnalyzer.Analyze(query, _config);
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        IReadOnlyList<FileRow> files = _store.GetFiles();
        var fileByPath = files.ToDictionary(f => f.Path, f => f, StringComparer.Ordinal);

        GenerateCandidates(analyzed, files, candidates);
        ExpandGraph(candidates, fileByPath);
        ScoreCandidates(candidates);

        List<Candidate> ordered = ApplyDiversity(candidates.Values);
        List<Memory.MemoryHit> memoryCandidates = SelectMemories(analyzed, ordered, options);
        return Pack(
            query, analyzed, ordered, options, fileByPath, candidates.Count,
            memoryCandidates, cost);
    }

    private void GenerateCandidates(
        AnalyzedQuery analyzed, IReadOnlyList<FileRow> files, Dictionary<string, Candidate> candidates)
    {
        if (analyzed.FtsMatch is not null)
        {
            foreach (SearchHit hit in _store.SearchEvidence(
                analyzed.FtsMatch, MaxHitsPerFilePerChannel, MaxEvidenceHits))
            {
                Candidate c = GetOrAdd(candidates, hit.Path);
                // Max, never sum: exact duplicate evidence must not double-weight.
                c.Fts = Math.Max(c.Fts, hit.Score);
                c.AddChunkHit(hit);
                c.Reasons.Add("fts");
            }

            foreach (SearchHit hit in _store.SearchEvidence(
                analyzed.FtsMatch, MaxHitsPerFilePerChannel, MaxEvidenceHits, symbolsOnly: true))
            {
                Candidate c = GetOrAdd(candidates, hit.Path);
                c.Symbol = Math.Max(
                    c.Symbol,
                    hit.Score * (1.0 + HeadingSpecificity(hit.Heading, analyzed.Terms)));
                c.AddSymbolHit(hit);
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
    /// Prefer a concise exact symbol name over a longer test/helper name that
    /// merely contains the same terms. This deterministic specificity bonus
    /// replaces the accidental pre-v3 boost from counting symbol chunks in both
    /// the general and symbol channels.
    /// </summary>
    private static double HeadingSpecificity(
        string? heading, IReadOnlyList<string> queryTerms)
    {
        if (string.IsNullOrEmpty(heading))
        {
            return 0;
        }

        IReadOnlyList<string> headingTerms = Identifiers.Split(heading);
        if (headingTerms.Count == 0)
        {
            return 0;
        }

        int matched = headingTerms.Count(term => queryTerms.Contains(term));
        return (double)matched / headingTerms.Count;
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
    /// Packs ranked candidates into the bundle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order of operations matters. Reuse is resolved <i>before</i> the top cap so
    /// acknowledged units never consume a slot meant for new context — the audited
    /// failure where eight echoed files produced eight markers and no new evidence.
    /// </para>
    /// <para>
    /// Every active budget is then a real constraint with no first-item exception.
    /// Packing stays best-fit: an oversized high-ranked item is skipped rather than
    /// ending the pass, so smaller relevant items further down still land.
    /// </para>
    /// </remarks>
    private ContextResult Pack(
        string query, AnalyzedQuery analyzed, List<Candidate> ordered, ContextOptions options,
        Dictionary<string, FileRow> fileByPath, int totalCandidates,
        List<Memory.MemoryHit> memoryCandidates, IResponseCostModel? cost)
    {
        if (options.ResponseBudgetTokens is not null && cost is null)
        {
            throw new ArgumentException(
                "A response cost model is required when ResponseBudgetTokens is set.",
                nameof(cost));
        }

        HashSet<string> seen = CollectSeen(options.Seen);

        var reused = new List<ReusedUnit>();
        var prepared = new List<PreparedCandidate>();
        int nonpositive = 0;

        foreach (Candidate c in ordered)
        {
            if (c.Score <= 0 || !fileByPath.TryGetValue(c.Path, out FileRow row))
            {
                if (c.Score <= 0)
                {
                    nonpositive++;
                }

                continue;
            }

            // An explicit whole-file possession claim wins over any per-unit
            // receipt for the same file, and is acknowledged rather than delivered.
            if (options.Known is { } known
                && known.TryGetValue(c.Path, out string? givenHash)
                && Hashes.Matches(row.ContentHash, givenHash))
            {
                reused.Add(new ReusedUnit
                {
                    Path = c.Path,
                    Receipt = FileReceipt(row, options.Detail),
                    AvoidedReadTokens = _scale.Apply(row.TokenCount),
                });
                continue;
            }

            ContextItem? item = BuildItem(c, row, options, seen, reused);
            if (item is not null)
            {
                prepared.Add(new PreparedCandidate(prepared.Count, item, row.ContentHash));
            }
        }

        // Memory is deliberately admitted before file evidence: it is distilled
        // knowledge at a fraction of a rediscovery's cost. Unlike the old legacy
        // reserve alone, each admission is measured as part of the complete
        // model-visible document, so optional memories can never overrun a hard
        // response ceiling.
        var memories = new List<Memory.MemoryHit>();
        var rejectedMemories = new List<Memory.MemoryHit>();
        OmissionTally emptyOmissions = ClassifyOmissions(
            prepared, [], options, nonpositive, acknowledgedOrdinals: null);
        foreach (Memory.MemoryHit memory in memoryCandidates)
        {
            var proposed = new List<Memory.MemoryHit>(memories.Count + 1);
            proposed.AddRange(memories);
            proposed.Add(memory);
            if (TryComposeWithinResponseBudget(
                    query, analyzed, options, [], reused, emptyOmissions,
                    totalCandidates, proposed, cost, out _))
            {
                memories.Add(memory);
            }
            else
            {
                rejectedMemories.Add(memory);
            }
        }

        int memoryTokens = memories.Sum(memory => memory.EstimatedTokens);
        ContextOptions packingOptions = options.BudgetTokens is { } legacyBudget
            ? options with { BudgetTokens = Math.Max(legacyBudget - memoryTokens, 0) }
            : options;

        List<PreparedCandidate> selected = SelectCandidates(
            query, analyzed, options, packingOptions, prepared, reused, nonpositive,
            totalCandidates, memories, seen, cost, out HashSet<int> acknowledgedOrdinals);

        // Memories are optional acceleration, never a substitute for all source
        // evidence. If their response/legacy reserve crowded every otherwise
        // deliverable file out, drop them and repack once. This preserves main's
        // cheap-memory preference whenever at least one source item still fits,
        // while preventing a stale note from turning a code query into a
        // memory-only false success.
        bool sourceFitsNonResponseBudgets = options.Top > 0 && prepared.Any(candidate =>
            CandidateVariants(candidate.Item, options)
                .Any(item => FitsNonResponseBudgets(item, options)));
        while (selected.Count == 0 && memories.Count > 0 && sourceFitsNonResponseBudgets)
        {
            Memory.MemoryHit dropped = memories[^1];
            memories.RemoveAt(memories.Count - 1);
            rejectedMemories.Add(dropped);
            int remainingMemoryTokens = memories.Sum(memory => memory.EstimatedTokens);
            packingOptions = options.BudgetTokens is { } originalLegacyBudget
                ? options with
                {
                    BudgetTokens = Math.Max(originalLegacyBudget - remainingMemoryTokens, 0),
                }
                : options;
            selected = SelectCandidates(
                query, analyzed, options, packingOptions, prepared, reused, nonpositive,
                totalCandidates, memories, seen, cost, out acknowledgedOrdinals);
        }

        OmissionTally omissions = ClassifyOmissions(
            prepared, selected, packingOptions, nonpositive, acknowledgedOrdinals);
        bool finalFits = TryComposeWithinResponseBudget(
            query, analyzed, options, selected, reused, omissions,
            totalCandidates, memories, cost, out ContextResult result);

        if (options.ResponseBudgetTokens is { } budget)
        {
            IReadOnlyList<PreparedCandidate> independentlyDeliverable = prepared
                .Where(c => !acknowledgedOrdinals.Contains(c.Ordinal))
                .Select(c => (
                    c.Ordinal,
                    c.ContentHash,
                    Item: CandidateVariants(c.Item, options)
                        .Where(item => FitsNonResponseBudgets(item, packingOptions))
                        .OrderBy(ResponseSizeEstimate)
                        .ThenBy(item => item.StartLine)
                        .ThenBy(item => item.EndLine)
                        .FirstOrDefault()))
                .Where(c => c.Item is not null)
                .Select(c => new PreparedCandidate(c.Ordinal, c.Item!, c.ContentHash))
                .ToList();

            // Empty/no-match and reuse-only documents are still successful
            // payloads and must obey the hard ceiling. If new evidence exists but
            // none fitted, returning an empty bundle would be a silent false
            // success; report a bounded, actionable retry budget for a compact
            // useful singleton instead.
            bool usefulEvidenceWasRejected =
                selected.Count == 0
                && ((independentlyDeliverable.Count > 0 && options.Top > 0)
                    || (memories.Count == 0 && rejectedMemories.Count > 0));
            if (!finalFits || usefulEvidenceWasRejected)
            {
                int retryBudget = SuccessfulRetryBudget(
                    query, analyzed, options, packingOptions, prepared,
                    independentlyDeliverable, reused, nonpositive, totalCandidates,
                    memories, rejectedMemories, acknowledgedOrdinals, cost!);
                ContextResult errorBasis = finalFits
                    ? result
                    : Compose(
                        query, analyzed, options, selected.Select(c => c.Item).ToList(),
                        reused, omissions, totalCandidates, reusedListLimit: 0, memories);

                return errorBasis with
                {
                    Shortfall = new BudgetShortfall
                    {
                        RequestedBudgetTokens = budget,
                        RetryBudgetTokens = retryBudget,
                    },
                };
            }

            int finalCost = cost!.Measure(result);
            if (finalCost > budget)
            {
                throw new InvalidOperationException(
                    $"Context packer accepted a {finalCost}-token response under a {budget}-token budget.");
            }
        }

        return result;
    }

    private List<PreparedCandidate> SelectCandidates(
        string query,
        AnalyzedQuery analyzed,
        ContextOptions responseOptions,
        ContextOptions packingOptions,
        IReadOnlyList<PreparedCandidate> prepared,
        List<ReusedUnit> reused,
        int nonpositive,
        int totalCandidates,
        IReadOnlyList<Memory.MemoryHit> memories,
        HashSet<string> seen,
        IResponseCostModel? cost,
        out HashSet<int> acknowledgedOrdinals)
    {
        var selected = new List<PreparedCandidate>();
        acknowledgedOrdinals = [];
        int usedLegacy = 0;
        int usedProjected = 0;

        // Greedy best-fit in ranked order. Response admission measures the whole
        // prospective final document; later candidates cannot grow unmeasured
        // metadata around an already accepted item.
        foreach (PreparedCandidate candidate in prepared)
        {
            ContextItem? duplicate = DuplicatePointer(candidate, selected, responseOptions);
            if (duplicate?.Receipt is { } duplicateReceipt && seen.Contains(duplicateReceipt))
            {
                reused.Add(new ReusedUnit
                {
                    Path = duplicate.Path,
                    Receipt = duplicateReceipt,
                });
                acknowledgedOrdinals.Add(candidate.Ordinal);
                continue;
            }

            if (selected.Count >= responseOptions.Top)
            {
                continue;
            }

            IEnumerable<ContextItem> variants = duplicate is null
                ? CandidateVariants(candidate.Item, responseOptions)
                : [duplicate];
            foreach (ContextItem item in variants)
            {
                if (packingOptions.BudgetTokens is { } fileBudget
                    && usedLegacy + item.EstimatedTokens > fileBudget)
                {
                    continue;
                }

                if (packingOptions.ProjectedReadBudgetTokens is { } readBudget
                    && usedProjected + item.ProjectedReadTokens > readBudget)
                {
                    continue;
                }

                var chosen = candidate with { Item = item };
                var proposed = new List<PreparedCandidate>(selected.Count + 1);
                proposed.AddRange(selected);
                proposed.Add(chosen);
                OmissionTally proposedOmissions = ClassifyOmissions(
                    prepared, proposed, packingOptions, nonpositive, acknowledgedOrdinals);
                if (!TryComposeWithinResponseBudget(
                        query, analyzed, responseOptions, proposed, reused, proposedOmissions,
                        totalCandidates, memories, cost, out _))
                {
                    continue;
                }

                selected.Add(chosen);
                usedLegacy += item.EstimatedTokens;
                usedProjected += item.ProjectedReadTokens;
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// Renders the complete prospective result and, when necessary, trims only
    /// the bounded reuse listing. The aggregate reuse count remains exact.
    /// </summary>
    private bool TryComposeWithinResponseBudget(
        string query, AnalyzedQuery analyzed, ContextOptions options,
        IReadOnlyList<PreparedCandidate> selected, List<ReusedUnit> reused,
        OmissionTally omissions, int totalCandidates,
        IReadOnlyList<Memory.MemoryHit> memories, IResponseCostModel? cost,
        out ContextResult result)
    {
        int maxListed = Math.Min(Math.Max(options.MaxReusedListed, 0), reused.Count);
        for (int listed = maxListed; listed >= 0; listed--)
        {
            result = Compose(
                query, analyzed, options, selected.Select(c => c.Item).ToList(),
                reused, omissions, totalCandidates, listed, memories);
            if (options.ResponseBudgetTokens is not { } budget
                || cost!.Measure(result) <= budget)
            {
                return true;
            }
        }

        result = Compose(
            query, analyzed, options, selected.Select(c => c.Item).ToList(),
            reused, omissions, totalCandidates, reusedListLimit: 0, memories);
        return false;
    }

    /// <summary>
    /// Computes omission metadata from a complete proposed selection. This makes
    /// response-cost measurement independent of when a candidate was visited.
    /// </summary>
    private OmissionTally ClassifyOmissions(
        IReadOnlyList<PreparedCandidate> prepared,
        IReadOnlyList<PreparedCandidate> selected,
        ContextOptions options,
        int nonpositive,
        IReadOnlySet<int>? acknowledgedOrdinals)
    {
        var selectedOrdinals = selected.Select(c => c.Ordinal).ToHashSet();
        var omissions = new OmissionTally { NonpositiveScore = nonpositive };
        int lastSelected = selected.Count == 0 ? -1 : selected.Max(c => c.Ordinal);

        foreach (PreparedCandidate candidate in prepared)
        {
            if (selectedOrdinals.Contains(candidate.Ordinal)
                || acknowledgedOrdinals?.Contains(candidate.Ordinal) == true)
            {
                continue;
            }

            // Once a proposal has filled Top, only candidates ranked after its
            // last selected entry are top omissions. Higher-ranked candidates
            // that were skipped before a lower-ranked item fitted retain the
            // budget reason that actually blocked them.
            if (options.Top <= 0
                || (selected.Count >= options.Top && candidate.Ordinal > lastSelected))
            {
                omissions.Top++;
            }
            else
            {
                int legacyBefore = selected
                    .Where(c => c.Ordinal < candidate.Ordinal)
                    .Sum(c => c.Item.EstimatedTokens);
                int projectedBefore = selected
                    .Where(c => c.Ordinal < candidate.Ordinal)
                    .Sum(c => c.Item.ProjectedReadTokens);
                IReadOnlyList<ContextItem> variants =
                    CandidateVariantsFor(candidate, selected, options).ToList();

                bool anyLegacyFit = variants.Any(item =>
                    options.BudgetTokens is not { } legacyBudget
                    || legacyBefore + item.EstimatedTokens <= legacyBudget);
                if (!anyLegacyFit)
                {
                    omissions.BudgetTokens++;
                    continue;
                }

                bool anyReadFit = variants.Any(item =>
                    (options.BudgetTokens is not { } legacyBudget
                        || legacyBefore + item.EstimatedTokens <= legacyBudget)
                    && (options.ProjectedReadBudgetTokens is not { } readBudget
                        || projectedBefore + item.ProjectedReadTokens <= readBudget));
                if (!anyReadFit)
                {
                    omissions.ProjectedReadBudget++;
                }
                else if (options.ResponseBudgetTokens is not null)
                {
                    omissions.ResponseBudget++;
                }
                else
                {
                    // Defensive fallback for invalid direct-core options.
                    // Public CLI/MCP validation rejects those values.
                    omissions.Top++;
                }
            }
        }

        return omissions;
    }

    private static bool FitsNonResponseBudgets(ContextItem item, ContextOptions options) =>
        (options.BudgetTokens is not { } legacy || item.EstimatedTokens <= legacy)
        && (options.ProjectedReadBudgetTokens is not { } read
            || item.ProjectedReadTokens <= read);

    /// <summary>
    /// Cheap deterministic proxy used only to choose a compact retry payload.
    /// Full-read token counts are deliberately excluded because pointer metadata
    /// is small even when the file it names is large.
    /// </summary>
    private static long ResponseSizeEstimate(ContextItem item)
    {
        long size = 256L + item.Path.Length + item.Reasons.Sum(reason => reason.Length);
        size += item.Spans?.Sum(span =>
            (long)span.Text.Length + span.Receipt.Length + (span.Symbol?.Length ?? 0) + 32) ?? 0;
        size += item.Symbols?.Sum(symbol =>
            (long)symbol.Name.Length + symbol.Signature.Length + (symbol.Doc?.Length ?? 0)
            + (symbol.Receipt?.Length ?? 0) + 48) ?? 0;
        return size;
    }

    /// <summary>
    /// Largest-to-smallest variants of one item. Multi-span evidence degrades by
    /// marginal relevance rather than dropping the whole relevant file when a
    /// budget can still fit its best span. Every emitted variant remains source
    /// ordered and independently receipted.
    /// </summary>
    private IEnumerable<ContextItem> CandidateVariantsFor(
        PreparedCandidate candidate,
        IReadOnlyList<PreparedCandidate> selected,
        ContextOptions options)
    {
        ContextItem? duplicate = DuplicatePointer(candidate, selected, options);
        return duplicate is null
            ? CandidateVariants(candidate.Item, options)
            : [duplicate];
    }

    /// <summary>
    /// When byte-identical content is already represented by an admitted item,
    /// emit a tiny relationship pointer instead of paying for the same evidence
    /// twice. The receipt is path-bound and also hashes the delivered
    /// <c>duplicate_of</c> target; it never borrows the first path's receipt.
    /// </summary>
    private static ContextItem? DuplicatePointer(
        PreparedCandidate candidate,
        IReadOnlyList<PreparedCandidate> selected,
        ContextOptions options)
    {
        PreparedCandidate? original = selected.FirstOrDefault(item =>
            string.Equals(item.ContentHash, candidate.ContentHash, StringComparison.Ordinal));
        if (original is null)
        {
            return null;
        }

        ContextItem item = candidate.Item;
        string receipt = Receipt.For(
            item.Path,
            candidate.ContentHash,
            DetailLabel(options.Detail),
            EvidenceUnitKind.Pointer,
            0,
            0,
            string.Empty,
            Canonical.JoinRecords(["duplicate_of", Canonical.NormalizePath(original.Item.Path)]));
        return item with
        {
            DuplicateOf = original.Item.Path,
            Symbols = null,
            SymbolsOmitted = null,
            Spans = null,
            SpansOmitted = null,
            Snippet = null,
            Receipt = receipt,
            Stripped = false,
            ContentTokens = 0,
            ProjectedReadTokens = 0,
            EstimatedTokens = 0,
        };
    }

    private IEnumerable<ContextItem> CandidateVariants(ContextItem item, ContextOptions options)
    {
        yield return item;
        if (item.Spans is { Count: > 1 } spans)
        {
            for (int count = spans.Count - 1; count >= 2; count--)
            {
                List<ContextSpan> selected = spans
                    .OrderBy(span => span.SelectionRank)
                    .Take(count)
                    .OrderBy(span => span.StartLine)
                    .ThenBy(span => span.EndLine)
                    .ToList();
                yield return SpanVariant(item, spans, selected, options);
            }

            // Prefixes preserve marginal relevance when several spans fit. Also
            // try every individual unit so one unusually large top-ranked span
            // cannot hide a smaller still-relevant span from a tight budget.
            foreach (ContextSpan span in spans
                .OrderBy(span => span.SelectionRank)
                .ThenBy(span => span.StartLine)
                .ThenBy(span => span.EndLine))
            {
                yield return SpanVariant(item, spans, [span], options);
            }

            yield break;
        }

        if (item.Symbols is not { Count: > 1 } symbols)
        {
            yield break;
        }

        for (int count = symbols.Count - 1; count >= 2; count--)
        {
            List<OutlineSymbol> selected = symbols
                .OrderBy(symbol => OutlinePriority(symbol.Role))
                .ThenBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.EndLine)
                .Take(count)
                .OrderBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.EndLine)
                .ToList();
            yield return SymbolVariant(item, symbols, selected);
        }

        foreach (OutlineSymbol symbol in symbols
            .OrderBy(symbol => OutlinePriority(symbol.Role))
            .ThenBy(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.EndLine))
        {
            yield return SymbolVariant(item, symbols, [symbol]);
        }
    }

    private ContextItem SpanVariant(
        ContextItem item,
        IReadOnlyList<ContextSpan> all,
        List<ContextSpan> selected,
        ContextOptions options)
    {
        int rawContentTokens = selected.Sum(span => options.SerializedCharging
            ? JsonTextTokens(span.Text)
            : Tokens.Count(span.Text));
        int contentTokens = _scale.Apply(rawContentTokens);
        bool single = selected.Count == 1;
        return item with
        {
            StartLine = single ? selected[0].StartLine : item.StartLine,
            EndLine = single ? selected[0].EndLine : item.EndLine,
            Spans = selected,
            SpansOmitted = (item.SpansOmitted ?? 0) + all.Count - selected.Count,
            Snippet = single ? selected[0].Text : null,
            Receipt = single ? selected[0].Receipt : null,
            Stripped = selected.Any(span => span.Stripped),
            ContentTokens = contentTokens,
            EstimatedTokens = _scale.Apply(rawContentTokens + EnvelopeTokens(item)),
        };
    }

    private ContextItem SymbolVariant(
        ContextItem item, IReadOnlyList<OutlineSymbol> all, List<OutlineSymbol> selected)
    {
        int rawContentTokens = OutlineTokens(selected);
        int contentTokens = _scale.Apply(rawContentTokens);
        bool single = selected.Count == 1;
        return item with
        {
            Symbols = selected,
            SymbolsOmitted = (item.SymbolsOmitted ?? 0) + all.Count - selected.Count,
            Receipt = single ? selected[0].Receipt : null,
            ContentTokens = contentTokens,
            EstimatedTokens = _scale.Apply(
                rawContentTokens + (selected.Count * SymbolFramingTokens)
                + EnvelopeTokens(item)),
        };
    }

    private static int OutlinePriority(string? role) => role switch
    {
        OutlineRole.Match => 0,
        OutlineRole.Container => 1,
        _ => 2,
    };

    /// <summary>
    /// Finds a deterministic retry value that really succeeds. The budget value
    /// participates in rendered identity/options, so measuring once under the
    /// rejected value is not actionable. This bounded increasing fixed point
    /// re-renders a compact useful selection until its exact surface cost fits.
    /// It intentionally returns a sufficient retry rather than exhaustively
    /// testing every lower integer, which would let a tiny malformed request
    /// amplify CPU by candidate-count × response-size.
    /// </summary>
    private int SuccessfulRetryBudget(
        string query, AnalyzedQuery analyzed, ContextOptions options,
        ContextOptions packingOptions,
        IReadOnlyList<PreparedCandidate> all,
        IReadOnlyList<PreparedCandidate> independentlyDeliverable,
        List<ReusedUnit> reused, int nonpositive, int totalCandidates,
        IReadOnlyList<Memory.MemoryHit> memories,
        IReadOnlyList<Memory.MemoryHit> rejectedMemories,
        IReadOnlySet<int> acknowledgedOrdinals,
        IResponseCostModel cost)
    {
        IReadOnlyList<PreparedCandidate> selection =
            independentlyDeliverable.Count > 0 && options.Top > 0
                ? [independentlyDeliverable
                    .OrderBy(candidate => ResponseSizeEstimate(candidate.Item))
                    .ThenBy(candidate => candidate.Ordinal)
                    .First()]
                : [];

        IReadOnlyList<Memory.MemoryHit> retryMemories = selection.Count > 0
            ? []
            : memories.Count > 0
                ? memories
                : rejectedMemories
                .OrderBy(memory => memory.EstimatedTokens)
                .ThenBy(memory => memory.Entry.Id, StringComparer.Ordinal)
                .Take(1)
                .ToList();
        OmissionTally omissions = ClassifyOmissions(
            all, selection, packingOptions, nonpositive, acknowledgedOrdinals);
        int proposed = Math.Max(options.ResponseBudgetTokens ?? 1, 1);

        // Measured cost normally converges in one or two steps. Keep proposals
        // strictly increasing and verify the postcondition before returning.
        for (int attempt = 0; attempt < 64; attempt++)
        {
            ContextOptions retryOptions = options with { ResponseBudgetTokens = proposed };
            ContextResult candidate = Compose(
                query, analyzed, retryOptions, selection.Select(c => c.Item).ToList(),
                reused, omissions, totalCandidates, reusedListLimit: 0, retryMemories);
            int measured = cost.Measure(candidate);
            if (measured <= proposed)
            {
                return proposed;
            }

            proposed = measured;
        }

        // Defensive convergence fallback. Exponential growth prevents a custom
        // cost model with an adversarial slowly increasing measure from keeping
        // the error path busy indefinitely.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            if (proposed > int.MaxValue / 2)
            {
                break;
            }

            proposed *= 2;
            ContextOptions retryOptions = options with { ResponseBudgetTokens = proposed };
            ContextResult candidate = Compose(
                query, analyzed, retryOptions, selection.Select(c => c.Item).ToList(),
                reused, omissions, totalCandidates, reusedListLimit: 0, retryMemories);
            if (cost.Measure(candidate) <= proposed)
            {
                return proposed;
            }
        }

        throw new InvalidOperationException("Could not compute a fitting response retry budget.");
    }

    /// <summary>Assembles the final document and its Q4 identities from a packed set.</summary>
    private ContextResult Compose(
        string query, AnalyzedQuery analyzed, ContextOptions options, List<ContextItem> items,
        List<ReusedUnit> reused, OmissionTally omissions, int totalCandidates,
        int reusedListLimit, IReadOnlyList<Memory.MemoryHit> memories)
    {
        List<ReusedUnit> orderedReused = reused
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.StartLine ?? 0)
            .ThenBy(r => r.Receipt, StringComparer.Ordinal)
            .ToList();
        List<ReusedUnit> listed = orderedReused
            .Take(Math.Max(reusedListLimit, 0))
            .ToList();

        string contentState = _store.GetMeta(MetaKeys.StateHash) ?? string.Empty;
        string producer = _store.GetMeta(MetaKeys.AnalysisProducerVersion)
            ?? ProducerVersions.AnalysisProducerVersion;
        string analysisState = Fingerprints.AnalysisState(
            contentState, producer, IndexSchema.Version, ConfigStore.ComputeHash(_config));
        string evidenceId = Fingerprints.EvidenceId(
            analysisState,
            CanonicalQuery(query, analyzed),
            options.CanonicalForm(),
            EvidenceRecords(items, orderedReused, memories));

        return new ContextResult
        {
            Query = query,
            Terms = analyzed.Terms,
            State = Hashes.Short(contentState),
            ContentState = Hashes.Short(contentState),
            FullContentState = contentState,
            AnalysisState = Hashes.Short(analysisState),
            FullAnalysisState = analysisState,
            EvidenceId = Hashes.Short(evidenceId),
            FullEvidenceId = evidenceId,
            Detail = options.Detail,
            TokenProfile = _scale.Label,
            Top = options.Top,
            BudgetTokens = options.BudgetTokens,
            ResponseBudgetTokens = options.ResponseBudgetTokens,
            ProjectedReadBudgetTokens = options.ProjectedReadBudgetTokens,
            Items = items,
            Memories = memories,
            Reused = listed,
            ReusedCount = reused.Count,
            ReusedFilesCount = reused.Select(r => r.Path).Distinct(StringComparer.Ordinal).Count(),
            ReusedReadTokens = reused
                .GroupBy(r => r.Path, StringComparer.Ordinal)
                .Sum(g => g.Max(r => r.AvoidedReadTokens)),
            TotalCandidates = totalCandidates,
            Omitted = omissions.Deliverable,
            Omissions = omissions.ToReasons(),
            EstimatedTokens = items.Sum(i => i.EstimatedTokens)
                + memories.Sum(memory => memory.EstimatedTokens),
            ContentTokens = items.Sum(i => i.ContentTokens),
            ProjectedReadTokens = items.Sum(i => i.ProjectedReadTokens),
        };
    }

    /// <summary>
    /// The canonical query identity: the analysed term sequence plus the exact
    /// original text, because the original is echoed in output and therefore
    /// affects the result.
    /// </summary>
    private static string CanonicalQuery(string query, AnalyzedQuery analyzed) =>
        Canonical.Hash(
            "query.v1",
            Canonical.JoinRecords(analyzed.Terms),
            query);

    private static IEnumerable<string> EvidenceRecords(
        IReadOnlyList<ContextItem> items,
        IReadOnlyList<ReusedUnit> reused,
        IReadOnlyList<Memory.MemoryHit> memories)
    {
        foreach (ContextItem item in items)
        {
            yield return Canonical.Hash(
                "item.v2",
                Canonical.NormalizePath(item.Path),
                Canonical.JoinRecords(item.Reasons),
                item.DuplicateOf is null ? string.Empty : Canonical.NormalizePath(item.DuplicateOf),
                item.Stripped ? "stripped" : "verbatim",
                Canonical.JoinRecords(UnitReceipts(item)));
        }

        foreach (ReusedUnit unit in reused)
        {
            yield return Canonical.Hash(
                "reused.v1",
                Canonical.NormalizePath(unit.Path),
                unit.Receipt);
        }

        foreach (Memory.MemoryHit memory in memories)
        {
            yield return Canonical.Hash(
                "memory.v1",
                memory.Entry.Id,
                memory.Entry.Kind,
                memory.Entry.Text,
                memory.Entry.Session ?? string.Empty,
                memory.Entry.Created,
                Canonical.JoinRecords(memory.Entry.Files
                    .OrderBy(file => file.Key, StringComparer.Ordinal)
                    .Select(file => Canonical.JoinRecords(
                    [
                        Canonical.NormalizePath(file.Key),
                        file.Value,
                    ]))),
                Canonical.JoinRecords(memory.Entry.Tags.Order(StringComparer.Ordinal)),
                memory.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Canonical.JoinRecords(memory.Reasons),
                memory.Stale ? "stale" : "current",
                Canonical.JoinRecords(memory.StaleFiles.Order(StringComparer.Ordinal)));
        }
    }

    private static IEnumerable<string> UnitReceipts(ContextItem item)
    {
        if (item.Spans is { Count: > 0 } spans)
        {
            foreach (ContextSpan span in spans)
            {
                yield return span.Receipt;
            }
        }
        else if (item.Symbols is { Count: > 0 } symbols)
        {
            foreach (OutlineSymbol symbol in symbols)
            {
                if (symbol.Receipt is { } receipt)
                {
                    yield return receipt;
                }
            }
        }
        else if (item.Receipt is { } only)
        {
            yield return only;
        }
    }

    /// <summary>
    /// Keeps only well-formed receipts. Malformed input is dropped here rather
    /// than compared, so a typo can never coincidentally suppress evidence.
    /// </summary>
    private static HashSet<string> CollectSeen(IReadOnlyList<string>? supplied)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (supplied is null)
        {
            return set;
        }

        foreach (string value in supplied)
        {
            if (Receipt.IsWellFormed(value))
            {
                set.Add(value);
            }
        }

        return set;
    }

    /// <summary>
    /// Builds one item, moving any unit the caller already holds into
    /// <paramref name="reused"/>. Returns null when every unit was reused, which
    /// is what lets the freed slot go to the next new candidate.
    /// </summary>
    private ContextItem? BuildItem(
        Candidate c, FileRow row, ContextOptions options,
        HashSet<string> seen, List<ReusedUnit> reused)
    {
        ContextItem shell = Shell(c, row);

        return options.Detail switch
        {
            ContextDetail.Outline => BuildOutlineItem(c, row, shell, options, seen, reused),
            ContextDetail.Slices => BuildSlicesItem(c, row, shell, options, seen, reused),
            _ => BuildPointerItem(row, shell, options, seen, reused),
        };
    }

    private ContextItem Shell(Candidate c, FileRow row)
    {
        (int start, int end) = c.PrimaryRange(row.LineCount, PreambleFallbackLines);
        return new ContextItem
        {
            Path = c.Path,
            Kind = row.Kind,
            Score = Math.Round(c.AdjustedScore, 4, MidpointRounding.AwayFromZero),
            StartLine = start,
            EndLine = end,
            Reasons = ReasonCompression.Compress(c.Reasons),
            Hash = Hashes.Short(row.ContentHash),
        };
    }

    private ContextItem? BuildPointerItem(
        FileRow row, ContextItem shell, ContextOptions options,
        HashSet<string> seen, List<ReusedUnit> reused)
    {
        string receipt = FileReceipt(row, options.Detail);
        if (seen.Contains(receipt))
        {
            reused.Add(new ReusedUnit { Path = row.Path, Receipt = receipt });
            return null;
        }

        return shell with
        {
            Receipt = receipt,
            EstimatedTokens = _scale.Apply(row.TokenCount),
            ProjectedReadTokens = _scale.Apply(row.TokenCount),
            ContentTokens = 0,
        };
    }

    private ContextItem? BuildOutlineItem(
        Candidate c, FileRow row, ContextItem shell, ContextOptions options,
        HashSet<string> seen, List<ReusedUnit> reused)
    {
        (IReadOnlyList<OutlineSymbol> selected, int cut) = Core.Outline.Outline.Symbols(
            _store, row.Id, MaxOutlineSymbols, c.SymbolMatches());

        // An empty skeleton carries no reusable symbol unit. Fall back to an
        // explicit pointer so repeated calls can acknowledge it with a receipt
        // and its downstream full-read cost remains honest.
        if (selected.Count == 0)
        {
            return BuildPointerItem(row, shell, options, seen, reused);
        }

        var delivered = new List<OutlineSymbol>();
        int reusedHere = 0;
        foreach (OutlineSymbol symbol in selected)
        {
            string receipt = Receipt.For(
                row.Path, row.ContentHash, DetailLabel(options.Detail), EvidenceUnitKind.Symbol,
                symbol.StartLine, symbol.EndLine, symbol.Name,
                Core.Outline.Outline.DeliveredEvidence(symbol));

            if (seen.Contains(receipt))
            {
                reused.Add(new ReusedUnit
                {
                    Path = row.Path,
                    Receipt = receipt,
                    StartLine = symbol.StartLine,
                    EndLine = symbol.EndLine,
                    Symbol = symbol.Name,
                });
                reusedHere++;
                continue;
            }

            delivered.Add(symbol with { Receipt = receipt });
        }

        if (delivered.Count == 0 && reusedHere > 0)
        {
            return null;
        }

        int rawContentTokens = OutlineTokens(delivered);
        return shell with
        {
            Symbols = delivered,
            SymbolsOmitted = cut > 0 ? cut : null,
            Receipt = delivered.Count == 1 ? delivered[0].Receipt : null,
            ContentTokens = _scale.Apply(rawContentTokens),
            ProjectedReadTokens = 0,
            EstimatedTokens = _scale.Apply(
                rawContentTokens + (delivered.Count * SymbolFramingTokens)
                + EnvelopeTokens(shell)),
            FileTokens = _scale.Apply(row.TokenCount),
        };
    }

    private ContextItem? BuildSlicesItem(
        Candidate c, FileRow row, ContextItem shell, ContextOptions options,
        HashSet<string> seen, List<ReusedUnit> reused)
    {
        List<ContextSpan> candidateSpans = SelectSpans(c, row, options);
        if (candidateSpans.Count == 0)
        {
            // No reconstructable source: fall back to a pointer so the file is
            // still discoverable rather than silently dropped.
            return BuildPointerItem(row, shell, options, seen, reused);
        }

        var delivered = new List<ContextSpan>();
        int reusedHere = 0;
        foreach (ContextSpan span in candidateSpans)
        {
            if (seen.Contains(span.Receipt))
            {
                reused.Add(new ReusedUnit
                {
                    Path = row.Path,
                    Receipt = span.Receipt,
                    StartLine = span.StartLine,
                    EndLine = span.EndLine,
                    Symbol = span.Symbol,
                });
                reusedHere++;
                continue;
            }

            delivered.Add(span);
        }

        if (delivered.Count == 0 && reusedHere > 0)
        {
            return null;
        }

        int rawContentTokens = delivered.Sum(span => options.SerializedCharging
            ? JsonTextTokens(span.Text)
            : Tokens.Count(span.Text));
        int contentTokens = _scale.Apply(rawContentTokens);
        bool single = delivered.Count == 1;

        return shell with
        {
            // Legacy single-span fields stay populated for exactly one span; a
            // multi-span item omits them rather than inventing an enclosing range.
            StartLine = single ? delivered[0].StartLine : shell.StartLine,
            EndLine = single ? delivered[0].EndLine : shell.EndLine,
            Snippet = single ? delivered[0].Text : null,
            Spans = delivered,
            Receipt = single ? delivered[0].Receipt : null,
            Stripped = delivered.Any(span => span.Stripped),
            ContentTokens = contentTokens,
            ProjectedReadTokens = 0,
            EstimatedTokens = _scale.Apply(rawContentTokens + EnvelopeTokens(shell)),
            FileTokens = _scale.Apply(row.TokenCount),
        };
    }

    /// <summary>
    /// Chooses up to <see cref="ContextOptions.MaxSpans"/> non-overlapping,
    /// symbol-aligned spans in relevance order, then emits them in source order.
    /// Symbol hits come first because a method range answers "change X" far more
    /// precisely than the surrounding class or an arbitrary FTS chunk.
    /// </summary>
    private List<ContextSpan> SelectSpans(Candidate c, FileRow row, ContextOptions options)
    {
        var accepted = new List<ContextSpan>();
        int limit = Math.Max(options.MaxSpans, 1);

        foreach ((int start, int end, string? symbol) in c.RankedRanges(row.LineCount, PreambleFallbackLines))
        {
            if (accepted.Count >= limit)
            {
                break;
            }

            int cappedEnd = Math.Min(end, start + MaxSliceLines - 1);
            if (accepted.Any(a => a.StartLine <= cappedEnd && start <= a.EndLine))
            {
                continue; // Overlaps an already-accepted span.
            }

            if (_store.GetSourceSlice(row.Path, start, cappedEnd) is not { } slice)
            {
                continue;
            }

            (string text, bool stripped) = options.StripComments
                ? CommentStripper.Strip(slice.Text)
                : (slice.Text, false);
            accepted.Add(new ContextSpan
            {
                StartLine = slice.StartLine,
                EndLine = slice.EndLine,
                Text = text,
                Symbol = symbol,
                Stripped = stripped,
                SelectionRank = accepted.Count,
                Receipt = Receipt.For(
                    row.Path, row.ContentHash, DetailLabel(options.Detail), EvidenceUnitKind.Span,
                    slice.StartLine, slice.EndLine, symbol ?? string.Empty, text),
            });
        }

        return accepted
            .OrderBy(s => s.StartLine)
            .ThenBy(s => s.EndLine)
            .ToList();
    }

    /// <summary>The whole-file possession receipt used by pointers and <c>--known</c>.</summary>
    private static string FileReceipt(FileRow row, ContextDetail detail) =>
        Receipt.For(
            row.Path, row.ContentHash, DetailLabel(detail), EvidenceUnitKind.Pointer,
            0, 0, string.Empty, string.Empty);

    private static string DetailLabel(ContextDetail detail) =>
        detail.ToString().ToLowerInvariant();

    /// <summary>
    /// Picks the agent memories worth folding into this bundle (ADR 0013):
    /// scored 70 % on query-term overlap (via <see cref="Memory.MemoryEngine.Match"/>)
    /// and 30 % on linkage to the bundle's own candidates, capped at
    /// <see cref="MaxContextMemories"/> items inside a reserve of at most a
    /// fifth of the legacy charged-work budget. Exact response-budget admission
    /// is performed later against the fully rendered document.
    /// </summary>
    private List<Memory.MemoryHit> SelectMemories(
        AnalyzedQuery analyzed, List<Candidate> ordered, ContextOptions options)
    {
        if (options.Memories is not { Count: > 0 } entries)
        {
            return [];
        }

        double maxAdjusted = Max(ordered.Select(c => c.AdjustedScore));
        var scoredMemories = new List<(Memory.MemoryEntry Entry, double Score, List<string> Reasons)>();
        foreach (Memory.MemoryEntry entry in entries)
        {
            (double termScore, List<string> reasons) = Memory.MemoryEngine.Match(entry, analyzed.Terms);

            double overlap = 0;
            string? overlapPath = null;
            foreach (Candidate c in ordered)
            {
                if (c.AdjustedScore > 0 && entry.Files.ContainsKey(c.Path))
                {
                    double strength = maxAdjusted > 0 ? c.AdjustedScore / maxAdjusted : 0;
                    if (strength > overlap)
                    {
                        overlap = strength;
                        overlapPath = c.Path;
                    }
                }
            }

            double score = MemoryTermWeight * termScore + MemoryOverlapWeight * overlap;
            if (score <= 0)
            {
                continue;
            }

            if (overlapPath is not null)
            {
                reasons.Add("linked:" + overlapPath);
            }

            scoredMemories.Add((entry, score, reasons));
        }

        int reserve = options.BudgetTokens is { } budget ? budget / MemoryBudgetDivisor : int.MaxValue;
        var picked = new List<Memory.MemoryHit>();
        int used = 0;
        foreach ((Memory.MemoryEntry entry, double score, List<string> reasons) in scoredMemories
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Id, StringComparer.Ordinal))
        {
            if (picked.Count >= MaxContextMemories)
            {
                break;
            }

            int contentTokens = options.SerializedCharging
                ? JsonTextTokens(entry.Text)
                : Tokens.Count(entry.Text);
            int cost = _scale.Apply(contentTokens + MemoryEnvelopeTokens(entry, reasons));
            if (used + cost > reserve)
            {
                continue;
            }

            (bool stale, IReadOnlyList<string> staleFiles) = Memory.MemoryEngine.Staleness(entry, _store);
            picked.Add(new Memory.MemoryHit
            {
                Entry = entry,
                Score = Math.Round(score, 4, MidpointRounding.AwayFromZero),
                Reasons = reasons,
                Stale = stale,
                StaleFiles = staleFiles,
                EstimatedTokens = cost,
            });
            used += cost;
        }

        return picked;
    }

    /// <summary>
    /// The framing a memory item adds to the response: id, kind, reasons and
    /// the linked path@hash pairs, plus a flat allowance for field names.
    /// </summary>
    private static int MemoryEnvelopeTokens(Memory.MemoryEntry entry, IReadOnlyList<string> reasons) =>
        MemoryEnvelopeOverhead
        + Tokens.Count(string.Join(',', reasons))
        + entry.Files.Keys.Sum(p => Tokens.Count(p) + 8);

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
    /// and the hash. This is the <i>legacy</i> cost basis retained for
    /// <c>--budget-tokens</c>; <c>--response-budget-tokens</c> measures the real
    /// rendered document instead of estimating it.
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

    /// <summary>Running tally of why candidates did not make the bundle.</summary>
    private sealed class OmissionTally
    {
        public int Top { get; set; }

        public int ResponseBudget { get; set; }

        public int ProjectedReadBudget { get; set; }

        public int BudgetTokens { get; set; }

        public int NonpositiveScore { get; set; }

        /// <summary>Omitted candidates that were actually deliverable (excludes unscored).</summary>
        public int Deliverable => Top + ResponseBudget + ProjectedReadBudget + BudgetTokens;

        public OmissionReasons ToReasons() => new()
        {
            Top = Top,
            ResponseBudget = ResponseBudget,
            ProjectedReadBudget = ProjectedReadBudget,
            BudgetTokens = BudgetTokens,
            NonpositiveScore = NonpositiveScore,
        };
    }

    /// <summary>A ranked, fully materialized candidate with a stable pass-local identity.</summary>
    private sealed record PreparedCandidate(int Ordinal, ContextItem Item, string ContentHash);

    private sealed class Candidate
    {
        private readonly List<SearchHit> _chunkHits = [];
        private readonly List<SearchHit> _symbolHits = [];
        private readonly HashSet<string> _hitKeys = new(StringComparer.Ordinal);

        public required string Path { get; init; }

        public double Fts { get; set; }

        public double Symbol { get; set; }

        public double Graph { get; set; }

        public int PathScore { get; set; }

        public double Score { get; set; }

        public double AdjustedScore { get; set; }

        public List<string> Reasons { get; } = [];

        public void AddChunkHit(SearchHit hit)
        {
            if (Register("c", hit))
            {
                _chunkHits.Add(hit);
            }
        }

        public void AddSymbolHit(SearchHit hit)
        {
            if (Register("s", hit))
            {
                _symbolHits.Add(hit);
            }
        }

        /// <summary>
        /// Evidence identity is the channel plus the exact range and heading —
        /// never a SQLite row ID, which is not stable across rebuilds.
        /// </summary>
        private bool Register(string channel, SearchHit hit) =>
            _hitKeys.Add($"{channel}:{hit.StartLine}:{hit.EndLine}:{hit.Heading}");

        /// <summary>Strict source-range containment between two symbol hits.</summary>
        private static bool Contains(SearchHit outer, SearchHit inner) =>
            outer.StartLine <= inner.StartLine
            && outer.EndLine >= inner.EndLine
            && (outer.StartLine < inner.StartLine || outer.EndLine > inner.EndLine);

        /// <summary>The symbols this query matched, used to pin query-aware outlines.</summary>
        public IReadOnlyList<SymbolMatch> SymbolMatches() => _symbolHits
            .Where(h => h.Heading is { Length: > 0 })
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.StartLine)
            .Select(h => new SymbolMatch(h.Heading!, h.StartLine, h.EndLine))
            .ToList();

        /// <summary>The single best range, kept for the deprecated single-range fields.</summary>
        public (int Start, int End) PrimaryRange(int lineCount, int preambleLines)
        {
            foreach ((int start, int end, string? _) in RankedRanges(lineCount, preambleLines))
            {
                return (start, end);
            }

            return (1, Math.Min(preambleLines, Math.Max(lineCount, 1)));
        }

        /// <summary>
        /// Candidate ranges in relevance order: symbol hits first (a method range
        /// is the precise answer to "change X"), then FTS chunks, then the file
        /// preamble as a last resort.
        /// </summary>
        public IEnumerable<(int Start, int End, string? Symbol)> RankedRanges(
            int lineCount, int preambleLines)
        {
            // A symbol whose range contains another matched symbol is scaffolding,
            // not the answer: "change Budget" wants the Budget method, not the
            // whole class that happens to contain it. Embedding the container's
            // body would spend hundreds of tokens to restate what the contained
            // span already says, so containers contribute signature-level
            // scaffolding to outlines (role `container`) and are not offered as
            // source spans. A class that matched on its own — containing no other
            // matched symbol — is not a container and is still delivered.
            List<SearchHit> specific = [.. _symbolHits
                .Where(h => !_symbolHits.Any(other => !ReferenceEquals(other, h) && Contains(h, other)))
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.StartLine)
                .ThenBy(h => h.EndLine)];

            foreach (SearchHit hit in specific)
            {
                yield return (hit.StartLine, hit.EndLine, hit.Heading);
            }

            foreach (SearchHit hit in _chunkHits
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.StartLine)
                .ThenBy(h => h.EndLine))
            {
                yield return (hit.StartLine, hit.EndLine, null);
            }

            if (_symbolHits.Count == 0 && _chunkHits.Count == 0)
            {
                yield return (1, Math.Min(preambleLines, Math.Max(lineCount, 1)), null);
            }
        }
    }
}
