using System.Globalization;
using System.Text;
using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Renders the evaluation corpus results as a deterministic, reviewable report
/// (Q0). The report doubles as the checked-in baseline snapshot: any relevance
/// or cost movement shows up as a reviewable diff rather than as a number in a
/// commit message.
/// </summary>
/// <remarks>
/// Deterministic I/O/operation counters are included. Wall-clock timing is
/// deliberately excluded because it varies between machines.
/// </remarks>
public static class EvalReport
{
    public static string Render(EvalRepo repo)
    {
        var sb = new StringBuilder();
        sb.Append("# RepoContext Release 1 candidate evaluation baseline\n\n");
        sb.Append("Deterministic metrics over the candidate `eval-repo` corpus.\n");
        sb.Append("Token counts are exact `o200k_base` BPE counts of the rendered surface.\n\n");

        sb.Append("## Metric formulas\n\n");
        sb.Append("- `recall@k` = |must-find files in first k results| / |must-find files|\n");
        sb.Append("- `ndcg@8` = DCG over graded label position / ideal DCG, gain = |labels| - label index\n");
        sb.Append("- `symbol_recall` = |must-find symbols delivered| / |must-find symbols|\n");
        sb.Append("- `span_recall` = |labelled ranges fully covered by a delivered span| / |labelled ranges|\n");
        sb.Append("- `density` = delivered source lines inside a labelled range / all delivered source lines\n");
        sb.Append("- `core_tokens` = tokens of the rendered core document (no CLI newline, no transport)\n");
        sb.Append("- `content_tokens` / `read_tokens` = embedded evidence / projected full-file reads\n");
        sb.Append("- `gap` = whether any labelled symbol or span is still missing after one call.\n");
        sb.Append("  `none` on a pointer task means nothing labelled is missing, not that the agent\n");
        sb.Append("  avoided a read. Per-task `read_tokens` sums every delivered pointer's projected\n");
        sb.Append("  cost; the workflow table records only reads required by the frozen policy.\n\n");

        sb.Append("## Deterministic index operation counters\n\n");
        sb.Append("Wall-clock time is exposed by `IndexStats`/`repoctx index` but excluded ");
        sb.Append("from this machine-independent golden.\n\n");
        sb.Append("| scenario | bytes read | files parsed | graph files analyzed | edges recomputed |\n");
        sb.Append("| --- | ---: | ---: | ---: | ---: |\n");
        foreach (IndexOperationCounters counters in EvalArtifacts.MeasureIndexOperations(repo))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {counters.Scenario} | {counters.BytesRead} | {counters.FilesParsed} ");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {counters.GraphFilesAnalyzed} | {counters.EdgesRecomputed} |\n");
        }

        sb.Append('\n');

        sb.Append("## Per-task metrics\n\n");
        sb.Append("| task | class | lang | r@1 | r@3 | r@8 | ndcg@8 | sym | span | density | core | content | read | gap |\n");
        sb.Append("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |\n");

        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            var options = new ContextOptions
            {
                Top = 8,
                Detail = task.Detail,
                ResponseBudgetTokens = task.ResponseBudgetTokens,
            };
            ContextResult result = new ContextEngine(repo.Store, repo.Config).Run(
                task.Query,
                options,
                task.ResponseBudgetTokens is null
                    ? null
                    : ContextCostModel.ForCli(OutputFormat.Json));

            int coreTokens = Tokens.Count(ContextOutput.Render(result, OutputFormat.Json));
            TaskMetrics m = EvalMetrics.Measure(task, result, coreTokens);

            sb.Append(CultureInfo.InvariantCulture, $"| {m.TaskId} | {m.Class} | {m.Language} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {m.RecallAt1:F2} | {m.RecallAt3:F2} | {m.RecallAt8:F2} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {m.NdcgAt8:F3} | {m.SymbolRecall:F2} | {m.SpanRecall:F2} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {m.RelevantLineDensity:F3} | {m.CoreDocumentTokens} ");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {m.ContentTokens} | {m.ProjectedReadTokens} | {(m.FullReadStillNeeded ? "open" : "none")} |\n");
        }

        sb.Append("\n## Simulated workflow accounting\n\n");
        sb.Append("Layers are reported separately so a saving in one cannot be counted twice.\n\n");
        sb.Append("| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |\n");
        sb.Append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");

        var simulator = new WorkflowSimulator(repo);
        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            WorkflowRun run = simulator.Run(task);
            WorkflowCost c = run.Cost;
            sb.Append(CultureInfo.InvariantCulture,
                $"| {run.TaskId} | {c.Calls} | {c.CoreDocumentTokens} | {c.CliStdoutTokens} ");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {c.McpContentTokens} | {c.McpTransportTokens} | {c.SessionOverheadTokens} ");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {c.CallArgumentTokens} | {c.FullFileReads} | {c.FullReadTokens} | {c.ModelVisibleCliTotal} | {c.WireMcpTotal} |\n");
        }

        sb.Append("\n## Reuse economics\n\n");
        sb.Append(RenderReuse(repo));
        return sb.ToString();
    }

    /// <summary>
    /// Measures the repeat-query saving that receipts buy: the same request,
    /// with every delivered unit acknowledged.
    /// </summary>
    private static string RenderReuse(EvalRepo repo)
    {
        var engine = new ContextEngine(repo.Store, repo.Config);
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        const string query = "change budget packing";

        ContextResult first = engine.Run(query, options);
        string firstJson = ContextOutput.Render(first, OutputFormat.Json);
        string[] receipts = [.. first.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt)];

        ContextResult second = engine.Run(query, options with { Seen = receipts });
        string secondJson = ContextOutput.Render(second, OutputFormat.Json);

        int before = Tokens.Count(firstJson);
        int after = Tokens.Count(secondJson);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Query: `{query}` at slices detail, top 3.\n\n");
        sb.Append("| | core tokens | content tokens | results | reused |\n| --- | ---: | ---: | ---: | ---: |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| first call | {before} | {first.ContentTokens} | {first.Items.Count} | {first.ReusedCount} |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| repeat with receipts | {after} | {second.ContentTokens} | {second.Items.Count} | {second.ReusedCount} |\n");
        return sb.ToString();
    }
}
