using RepoContext.Core.Context;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>The measured outcome of running one labelled task (Q0).</summary>
public sealed record TaskMetrics
{
    public required string TaskId { get; init; }

    public required TaskClass Class { get; init; }

    public required LanguageStratum Language { get; init; }

    public required double RecallAt1 { get; init; }

    public required double RecallAt3 { get; init; }

    public required double RecallAt8 { get; init; }

    public required double NdcgAt8 { get; init; }

    /// <summary>Fraction of must-find symbols actually delivered.</summary>
    public required double SymbolRecall { get; init; }

    /// <summary>Fraction of labelled required ranges fully covered by delivered spans.</summary>
    public required double SpanRecall { get; init; }

    /// <summary>Delivered evidence lines that fall inside a labelled required range.</summary>
    public required double RelevantLineDensity { get; init; }

    /// <summary>Whether a forbidden (sensitive/negative) path appeared anywhere.</summary>
    public required bool LeakedForbiddenPath { get; init; }

    /// <summary>Exact tokens of the rendered core result document.</summary>
    public required int CoreDocumentTokens { get; init; }

    /// <summary>Embedded evidence tokens reported by the engine.</summary>
    public required int ContentTokens { get; init; }

    /// <summary>Projected downstream full-file read tokens.</summary>
    public required int ProjectedReadTokens { get; init; }

    /// <summary>Whether a full-file read is still required to see the labelled evidence.</summary>
    public required bool FullReadStillNeeded { get; init; }
}

/// <summary>
/// Deterministic relevance and cost metrics over a labelled task. These are
/// static evidence proxies for downstream task success: they prove the evidence
/// was delivered, not that a given model will use it correctly.
/// </summary>
public static class EvalMetrics
{
    public static TaskMetrics Measure(EvalTask task, ContextResult result, int coreDocumentTokens)
    {
        List<string> ranked = [.. result.Items.Select(i => i.Path)];

        return new TaskMetrics
        {
            TaskId = task.Id,
            Class = task.Class,
            Language = task.Language,
            RecallAt1 = RecallAt(task.MustFindFiles, ranked, 1),
            RecallAt3 = RecallAt(task.MustFindFiles, ranked, 3),
            RecallAt8 = RecallAt(task.MustFindFiles, ranked, 8),
            NdcgAt8 = Ndcg(task.MustFindFiles, ranked, 8),
            SymbolRecall = SymbolRecall(task, result),
            SpanRecall = SpanRecall(task, result),
            RelevantLineDensity = RelevantLineDensity(task, result),
            LeakedForbiddenPath = Leaked(task, result),
            CoreDocumentTokens = coreDocumentTokens,
            ContentTokens = result.ContentTokens,
            ProjectedReadTokens = result.ProjectedReadTokens,
            FullReadStillNeeded = FullReadStillNeeded(task, result),
        };
    }

    /// <summary>Fraction of must-find files present in the first <paramref name="k"/> results.</summary>
    private static double RecallAt(IReadOnlyList<string> expected, IReadOnlyList<string> ranked, int k)
    {
        if (expected.Count == 0)
        {
            return 1.0;
        }

        HashSet<string> top = [.. ranked.Take(k)];
        return (double)expected.Count(top.Contains) / expected.Count;
    }

    /// <summary>
    /// Normalised discounted cumulative gain over the label order: an earlier
    /// must-find file is worth more, and the ideal ordering is the label order.
    /// </summary>
    private static double Ndcg(IReadOnlyList<string> expected, IReadOnlyList<string> ranked, int k)
    {
        if (expected.Count == 0)
        {
            return 1.0;
        }

        double dcg = 0;
        for (int i = 0; i < Math.Min(k, ranked.Count); i++)
        {
            int label = expected.ToList().IndexOf(ranked[i]);
            if (label < 0)
            {
                continue;
            }

            // Graded gain: earlier-labelled files are more relevant.
            double gain = expected.Count - label;
            dcg += gain / Math.Log2(i + 2);
        }

        double ideal = 0;
        for (int i = 0; i < Math.Min(k, expected.Count); i++)
        {
            ideal += (double)(expected.Count - i) / Math.Log2(i + 2);
        }

        return ideal == 0 ? 1.0 : dcg / ideal;
    }

    private static double SymbolRecall(EvalTask task, ContextResult result)
    {
        if (task.MustFindSymbols.Count == 0)
        {
            return 1.0;
        }

        HashSet<string> delivered =
        [
            .. result.Items
                .SelectMany(i => i.Symbols ?? [])
                .Select(s => s.Name),
            .. result.Items
                .SelectMany(i => i.Spans ?? [])
                .Select(s => s.Symbol)
                .OfType<string>(),
        ];

        return (double)task.MustFindSymbols.Count(delivered.Contains) / task.MustFindSymbols.Count;
    }

    private static double SpanRecall(EvalTask task, ContextResult result)
    {
        if (task.MustCoverSpans.Count == 0)
        {
            return 1.0;
        }

        int covered = task.MustCoverSpans.Count(required => result.Items
            .Where(i => i.Path == required.Path)
            .SelectMany(i => i.Spans ?? [])
            .Any(s => required.CoveredBy(s.StartLine, s.EndLine)));

        return (double)covered / task.MustCoverSpans.Count;
    }

    /// <summary>
    /// Delivered source lines that fall inside a labelled range, over all
    /// delivered source lines. Only meaningful with labelled spans, and never
    /// accepted on its own — the gates require unchanged span recall alongside.
    /// </summary>
    private static double RelevantLineDensity(EvalTask task, ContextResult result)
    {
        if (task.MustCoverSpans.Count == 0)
        {
            return 1.0;
        }

        int total = 0;
        int relevant = 0;
        foreach (ContextItem item in result.Items)
        {
            foreach (ContextSpan span in item.Spans ?? [])
            {
                for (int line = span.StartLine; line <= span.EndLine; line++)
                {
                    total++;
                    if (task.MustCoverSpans.Any(r =>
                        r.Path == item.Path && line >= r.StartLine && line <= r.EndLine))
                    {
                        relevant++;
                    }
                }
            }
        }

        return total == 0 ? 0 : (double)relevant / total;
    }

    private static bool Leaked(EvalTask task, ContextResult result) =>
        result.Items.Any(i => task.ForbiddenPaths.Any(f =>
            i.Path.Contains(f, StringComparison.Ordinal)))
        || result.Reused.Any(r => task.ForbiddenPaths.Any(f =>
            r.Path.Contains(f, StringComparison.Ordinal)));

    /// <summary>
    /// Whether the labelled evidence is still missing after this single call, so
    /// the simulated agent would have to fall back to a full-file read.
    /// </summary>
    private static bool FullReadStillNeeded(EvalTask task, ContextResult result) =>
        SpanRecall(task, result) < 1.0 || SymbolRecall(task, result) < 1.0;
}
