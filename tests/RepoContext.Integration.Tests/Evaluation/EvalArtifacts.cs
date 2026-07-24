using System.Text.Json;
using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Deterministic raw evaluation artifacts kept beside the aggregate report.
/// They make every token total auditable against the exact body/frame counted.
/// </summary>
public static class EvalArtifacts
{
    public static IReadOnlyDictionary<string, string> Render(EvalRepo repo)
    {
        IReadOnlyList<IndexOperationCounters> counters = MeasureIndexOperations(repo);
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["index-stats.json"] = JsonSerializer.Serialize(counters, OutputJson.Options),
            ["mcp-session-model.json"] = McpSessionFixture.InstructionsAndToolSchemas,
        };

        var simulator = new WorkflowSimulator(repo);
        foreach (EvalTask task in EvalCorpus.Tasks)
        {
            var options = new ContextOptions
            {
                Top = 8,
                Detail = task.Detail,
                ResponseBudgetTokens = task.ResponseBudgetTokens,
            };
            ContextResult cliResult = new ContextEngine(repo.Store, repo.Config).Run(
                task.Query,
                options,
                task.ResponseBudgetTokens is null
                    ? null
                    : ContextCostModel.ForCli(OutputFormat.Json));
            ContextResult mcpResult = task.ResponseBudgetTokens is null
                ? cliResult
                : new ContextEngine(repo.Store, repo.Config).Run(
                    task.Query, options, ContextCostModel.ForMcpText());

            string core = ContextOutput.Render(cliResult, OutputFormat.Json, Surfaces.Core);
            string cli = ContextCostModel.ForCli(OutputFormat.Json).SurfaceText(cliResult);
            string mcp = ContextOutput.Render(mcpResult, OutputFormat.Json, Surfaces.McpText);
            artifacts[$"{task.Id}.core.json"] = core;
            artifacts[$"{task.Id}.cli.txt"] = cli;
            artifacts[$"{task.Id}.mcp-content.json"] = mcp;
            artifacts[$"{task.Id}.mcp-envelope-model.json"] = McpSessionFixture.Envelope(mcp);
            artifacts[$"{task.Id}.workflow.json"] =
                JsonSerializer.Serialize(simulator.Run(task), OutputJson.Options);
        }

        AddReuseArtifacts(repo, artifacts);
        return artifacts;
    }

    /// <summary>
    /// Deterministic cold/no-op/one-file-change operation counters. Wall-clock
    /// timing remains outside golden artifacts because it is machine-dependent.
    /// </summary>
    public static IReadOnlyList<IndexOperationCounters> MeasureIndexOperations(EvalRepo cold)
    {
        using var noOpRepo = new EvalRepo();
        IndexStats noOp = noOpRepo.Reindex();

        using var changedRepo = new EvalRepo();
        const string changedPath = "src/auth/session.ts";
        string absolute = Path.Combine(changedRepo.Root, changedPath);
        changedRepo.Write(
            changedPath,
            File.ReadAllText(absolute) + "\n// deterministic evaluation change\n");
        IndexStats changed = changedRepo.Reindex();

        return
        [
            Snapshot("cold", cold.Stats),
            Snapshot("no-op", noOp),
            Snapshot("one-file-change", changed),
        ];
    }

    private static IndexOperationCounters Snapshot(string scenario, IndexStats stats) => new()
    {
        Scenario = scenario,
        BytesRead = stats.BytesRead,
        FilesParsed = stats.FilesParsed,
        GraphFilesAnalyzed = stats.GraphFilesAnalyzed,
        EdgesRecomputed = stats.EdgesRecomputed,
        TotalFiles = stats.TotalFiles,
        TotalChunks = stats.TotalChunks,
        TotalSymbols = stats.TotalSymbols,
        TotalEdges = stats.TotalEdges,
    };

    private static void AddReuseArtifacts(
        EvalRepo repo, IDictionary<string, string> artifacts)
    {
        var engine = new ContextEngine(repo.Store, repo.Config);
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        const string query = "change budget packing";
        ContextResult first = engine.Run(query, options);
        string[] receipts =
        [
            .. first.Items.SelectMany(item => item.Spans ?? []).Select(span => span.Receipt),
        ];
        ContextResult repeat = engine.Run(query, options with { Seen = receipts });

        artifacts["reuse-first.core.json"] =
            ContextOutput.Render(first, OutputFormat.Json, Surfaces.Core);
        artifacts["reuse-repeat.core.json"] =
            ContextOutput.Render(repeat, OutputFormat.Json, Surfaces.Core);
    }
}

/// <summary>Stable operation counters for one declared indexing scenario.</summary>
public sealed record IndexOperationCounters
{
    public required string Scenario { get; init; }

    public long BytesRead { get; init; }

    public int FilesParsed { get; init; }

    public int GraphFilesAnalyzed { get; init; }

    public int EdgesRecomputed { get; init; }

    public int TotalFiles { get; init; }

    public int TotalChunks { get; init; }

    public int TotalSymbols { get; init; }

    public int TotalEdges { get; init; }
}
