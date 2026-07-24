using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// The Release 1 quality and cost gates (Q0). Relevance gates take precedence
/// over every savings target: a cheaper response that drops labelled evidence
/// fails here.
/// </summary>
public sealed class EvaluationTests
{
    private static TaskMetrics Measure(EvalRepo repo, EvalTask task)
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
        return EvalMetrics.Measure(task, result, coreTokens);
    }

    private static IReadOnlyList<TaskMetrics> MeasureAll(EvalRepo repo) =>
        [.. EvalCorpus.Tasks.Select(t => Measure(repo, t))];

    /// <summary>No must-find file may disappear: zero critical-item misses.</summary>
    [Fact]
    public void EveryMustFindFile_IsDelivered()
    {
        using var repo = new EvalRepo();

        foreach (TaskMetrics m in MeasureAll(repo))
        {
            Assert.True(
                m.RecallAt8 >= 1.0,
                $"{m.TaskId}: Recall@8 was {m.RecallAt8:F3}; a must-find file is missing");
        }
    }

    /// <summary>Every must-find symbol and labelled span is delivered.</summary>
    [Fact]
    public void EveryMustFindSymbolAndSpan_IsDelivered()
    {
        using var repo = new EvalRepo();

        foreach (TaskMetrics m in MeasureAll(repo))
        {
            Assert.True(m.SymbolRecall >= 1.0, $"{m.TaskId}: symbol recall {m.SymbolRecall:F3}");
            Assert.True(m.SpanRecall >= 1.0, $"{m.TaskId}: span recall {m.SpanRecall:F3}");
        }
    }

    /// <summary>
    /// Macro-averaged relevance per language and per task class, so a gain in one
    /// stratum cannot hide a regression in another.
    /// </summary>
    [Fact]
    public void RelevanceGates_HoldPerLanguageAndTaskClass()
    {
        using var repo = new EvalRepo();
        IReadOnlyList<TaskMetrics> all = MeasureAll(repo);

        foreach (IGrouping<LanguageStratum, TaskMetrics> stratum in all.GroupBy(m => m.Language))
        {
            Assert.True(
                stratum.Average(m => m.RecallAt8) >= 1.0,
                $"{stratum.Key}: macro Recall@8 {stratum.Average(m => m.RecallAt8):F3}");
        }

        foreach (IGrouping<TaskClass, TaskMetrics> stratum in all.GroupBy(m => m.Class))
        {
            Assert.True(
                stratum.Average(m => m.NdcgAt8) >= 0.5,
                $"{stratum.Key}: macro nDCG@8 {stratum.Average(m => m.NdcgAt8):F3}");
        }

        // Micro-average across the whole corpus.
        Assert.True(all.Average(m => m.RecallAt8) >= 1.0);
    }

    /// <summary>Sensitive-file leakage is always zero. This gate has no waiver.</summary>
    [Fact]
    public void SensitiveLeakage_IsAlwaysZero()
    {
        using var repo = new EvalRepo();

        Assert.All(MeasureAll(repo), m =>
            Assert.False(m.LeakedForbiddenPath, $"{m.TaskId} leaked a forbidden path"));
    }

    /// <summary>
    /// Slice tasks must answer without a follow-up full-file read, and must spend
    /// most of their delivered lines on the labelled range.
    /// </summary>
    [Fact]
    public void SliceTasks_RemoveTheFullReadAndKeepLinesRelevant()
    {
        using var repo = new EvalRepo();

        foreach (TaskMetrics m in MeasureAll(repo)
            .Where(m => EvalCorpus.Tasks.Single(t => t.Id == m.TaskId).Detail == ContextDetail.Slices))
        {
            Assert.False(m.FullReadStillNeeded, $"{m.TaskId} still needs a full-file read");

            // Density is only ever accepted alongside unchanged span recall.
            Assert.True(m.SpanRecall >= 1.0);
            Assert.True(
                m.RelevantLineDensity > 0.15,
                $"{m.TaskId}: relevant-line density {m.RelevantLineDensity:F3} is too low");
        }
    }

    /// <summary>
    /// Two identical runs must produce byte-identical output and identical result
    /// identities, including across separately built indexes of the same tree.
    /// </summary>
    [Fact]
    public void Results_AreByteIdentical_AcrossRunsAndRebuilds()
    {
        using var first = new EvalRepo();
        using var second = new EvalRepo();

        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            string a = Render(first, task);
            string b = Render(first, task);
            string c = Render(second, task);

            Assert.Equal(a, b);

            // A second repository rooted at a different absolute directory must
            // produce the same identities and the same bytes.
            Assert.Equal(a, c);
        }

        static string Render(EvalRepo repo, EvalTask task)
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
            return ContextOutput.Render(result, OutputFormat.Json);
        }
    }

    /// <summary>
    /// The frozen policies complete every task's labelled evidence, and the
    /// accounting layers are reported separately rather than blended.
    /// </summary>
    [Fact]
    public void SimulatedWorkflows_CompleteEvidence_AndReportLayersSeparately()
    {
        using var repo = new EvalRepo();
        var simulator = new WorkflowSimulator(repo);

        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            WorkflowRun run = simulator.Run(task);

            Assert.True(run.Steps.Count <= WorkflowSimulator.MaxSteps, $"{task.Id} exceeded the step cap");
            Assert.True(run.EvidenceComplete, $"{task.Id}: labelled evidence incomplete");

            // Layers are distinct and none is silently zero.
            Assert.True(run.Cost.CoreDocumentTokens > 0);
            Assert.True(run.Cost.CliStdoutTokens > 0);
            Assert.True(run.Cost.McpContentTokens > 0);
            Assert.True(run.Cost.McpTransportTokens > 0, "the JSON-RPC envelope is never free");
            Assert.True(run.Cost.SessionOverheadTokens > 0);

            // A slices task must not need a full-file read at all.
            if (task.Detail == ContextDetail.Slices)
            {
                Assert.Equal(0, run.Cost.FullFileReads);
            }
        }
    }

    [Fact]
    public void EveryTask_UsesAndRespectsItsDeclaredResponseBudget()
    {
        using var repo = new EvalRepo();

        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            int budget = Assert.IsType<int>(task.ResponseBudgetTokens);
            var options = new ContextOptions
            {
                Top = 8,
                Detail = task.Detail,
                ResponseBudgetTokens = budget,
            };
            ContextResult cli = new ContextEngine(repo.Store, repo.Config).Run(
                task.Query, options, ContextCostModel.ForCli(OutputFormat.Json));
            ContextResult mcp = new ContextEngine(repo.Store, repo.Config).Run(
                task.Query, options, ContextCostModel.ForMcpText());

            Assert.Null(cli.Shortfall);
            Assert.Null(mcp.Shortfall);
            Assert.True(ContextCostModel.ForCli(OutputFormat.Json).Measure(cli) <= budget);
            Assert.True(ContextCostModel.ForMcpText().Measure(mcp) <= budget);
            Assert.Equal(
                cli.Items.Select(item => item.Path),
                mcp.Items.Select(item => item.Path));
        }
    }

    /// <summary>
    /// Reuse must actually pay: echoing the receipts from a first call cuts the
    /// repeated payload substantially rather than resending the same evidence.
    /// </summary>
    [Fact]
    public void EchoingReceipts_CutsRepeatedWorkflowTokens()
    {
        using var repo = new EvalRepo();
        var engine = new ContextEngine(repo.Store, repo.Config);
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        const string query = "change budget packing";

        ContextResult first = engine.Run(query, options);
        int firstContent = first.ContentTokens;
        int firstTokens = Tokens.Count(ContextOutput.Render(first, OutputFormat.Json));
        Assert.True(firstContent > 0);

        string[] receipts = [.. first.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt)];
        ContextResult second = engine.Run(query, options with { Seen = receipts });
        int secondTokens = Tokens.Count(ContextOutput.Render(second, OutputFormat.Json));

        Assert.Equal(receipts.Length, second.ReusedCount);

        // Every acknowledged unit is withheld, so the repeated content collapses.
        Assert.All(
            second.Items.SelectMany(i => i.Spans ?? []),
            span => Assert.DoesNotContain(span.Receipt, receipts));
        Assert.True(
            second.ContentTokens < firstContent,
            $"repeat cost {second.ContentTokens} should fall below {firstContent}");
        Assert.True(
            secondTokens * 2 <= firstTokens,
            $"repeat response {secondTokens} must save at least 50% of {firstTokens}");
    }

    /// <summary>
    /// Reuse metadata is bounded: many matching receipts cannot make the response
    /// grow without limit.
    /// </summary>
    [Fact]
    public void ReusedCollection_IsBounded_AndReportsTheRemainder()
    {
        using var repo = new EvalRepo();
        var engine = new ContextEngine(repo.Store, repo.Config);
        var options = new ContextOptions { Top = 20, Detail = ContextDetail.Slices, MaxSpans = 4 };

        ContextResult first = engine.Run("budget packing tokens session login", options);
        string[] receipts = [.. first.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt)];
        Assert.True(receipts.Length >= 3, "the corpus yields several reusable units");

        ContextResult bounded = engine.Run(
            "budget packing tokens session login",
            options with { Seen = receipts, MaxReusedListed = 2 });

        Assert.Equal(receipts.Length, bounded.ReusedCount);
        Assert.Equal(2, bounded.Reused.Count);
        Assert.Equal(receipts.Length - 2, bounded.ReusedOmitted);
    }
}
