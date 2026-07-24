using System.Text.Json;
using RepoContext.Cli.Mcp;
using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// The immutable accounting layers a simulated session pays for (Q0). Keeping
/// them separate is what stops a saving in one layer from being claimed twice,
/// and what makes "wire tokens" and "model-visible tokens" comparable across
/// changes.
/// </summary>
public sealed record WorkflowCost
{
    /// <summary>Rendered core result documents, without CLI newline or transport wrapper.</summary>
    public int CoreDocumentTokens { get; init; }

    /// <summary>Exact CLI stdout, including each response's trailing newline.</summary>
    public int CliStdoutTokens { get; init; }

    /// <summary>The model-visible MCP text content blocks.</summary>
    public int McpContentTokens { get; init; }

    /// <summary>
    /// Canonical MCP JSON-RPC response-envelope model, excluding the volatile
    /// request ID. This is not a captured SDK wire transcript.
    /// </summary>
    public int McpTransportTokens { get; init; }

    /// <summary>Server instructions and tool definitions, counted once per session.</summary>
    public int SessionOverheadTokens { get; init; }

    /// <summary>Call arguments the client sends.</summary>
    public int CallArgumentTokens { get; init; }

    /// <summary>Full-file reads the policy was forced to perform.</summary>
    public int FullReadTokens { get; init; }

    public int Calls { get; init; }

    public int FullFileReads { get; init; }

    /// <summary>
    /// What the model actually sees over a CLI session: results plus the
    /// arguments it wrote, plus any file it had to read itself.
    /// </summary>
    public int ModelVisibleCliTotal =>
        CliStdoutTokens + CallArgumentTokens + FullReadTokens;

    /// <summary>The same session driven over MCP, including transport and session overhead.</summary>
    public int WireMcpTotal =>
        McpContentTokens + McpTransportTokens + SessionOverheadTokens
        + CallArgumentTokens + FullReadTokens;
}

/// <summary>One recorded step of a simulated workflow.</summary>
public sealed record WorkflowStep(string Tool, string Arguments, int CoreTokens);

/// <summary>The outcome of simulating one labelled task.</summary>
public sealed record WorkflowRun
{
    public required string TaskId { get; init; }

    public required IReadOnlyList<WorkflowStep> Steps { get; init; }

    public required WorkflowCost Cost { get; init; }

    /// <summary>Whether every labelled piece of evidence was present when the policy stopped.</summary>
    public required bool EvidenceComplete { get; init; }
}

/// <summary>
/// A frozen agent policy (Q0). It is deliberately mechanical: savings must come
/// from the product getting cheaper, never from quietly making the simulated
/// agent ask for less.
/// </summary>
/// <remarks>
/// The policy issues one declared context request, then escalates only on a
/// concrete gap — symbol search when a must-find file is missing, an outline
/// when the file is known but a required symbol is absent, <c>related</c> when a
/// dependency edge is missing, and a full-file read only when a labelled range
/// was never delivered. It stops as soon as the evidence gap is empty, or after
/// <see cref="MaxSteps"/>.
/// </remarks>
public sealed class WorkflowSimulator
{
    /// <summary>Hard stop so a policy bug cannot produce an unbounded session.</summary>
    public const int MaxSteps = 5;

    /// <summary>
    /// Amortised MCP session overhead: server instructions plus the five tool
    /// definitions, counted once per session.
    /// </summary>
    private static readonly Lazy<int> SessionOverhead = new(() =>
        Tokens.Count(McpSessionFixture.InstructionsAndToolSchemas));

    private readonly EvalRepo _repo;

    public WorkflowSimulator(EvalRepo repo) => _repo = repo;

    public WorkflowRun Run(EvalTask task)
    {
        var steps = new List<WorkflowStep>();
        int coreTokens = 0;
        int cliTokens = 0;
        int mcpContent = 0;
        int mcpTransport = 0;
        int argumentTokens = 0;
        int fullReadTokens = 0;
        int fullReads = 0;

        // Step 1: one declared context request.
        var options = new ContextOptions
        {
            Top = 8,
            Detail = task.Detail,
            ResponseBudgetTokens = task.ResponseBudgetTokens,
        };
        string arguments = McpSessionFixture.Arguments(
            ("task", task.Query),
            ("top", 8),
            ("detail", task.Detail.ToString().ToLowerInvariant()),
            ("responseBudgetTokens", task.ResponseBudgetTokens));
        ContextResult result = new ContextEngine(_repo.Store, _repo.Config)
            .Run(task.Query, options, ContextCostModel.ForCli(OutputFormat.Json));
        ContextResult mcpResult = new ContextEngine(_repo.Store, _repo.Config)
            .Run(task.Query, options, ContextCostModel.ForMcpText());

        Account(result, mcpResult, arguments, "repoctx.get_context");

        // Step 2: symbol search only when a must-find file is still absent.
        var found = new HashSet<string>(result.Items.Select(i => i.Path), StringComparer.Ordinal);
        if (task.MustFindFiles.Any(f => !found.Contains(f)))
        {
            string term = task.MustFindSymbols.FirstOrDefault() ?? task.Query;
            string searchArgs = McpSessionFixture.Arguments(
                ("query", term), ("top", 10), ("symbols", true));
            if (FtsQuery.Build(term) is { } match)
            {
                IReadOnlyList<SearchHit> hits = _repo.Store.Search(match, 10, symbolsOnly: true);
                AccountRendered(SearchOutput.Render(term, hits, OutputFormat.Json), searchArgs, "repoctx.search");
                found.UnionWith(hits.Select(h => h.Path));
            }
        }

        // Step 3: a focused outline only when the file is known but a required
        // symbol was not delivered.
        double symbolRecall = SymbolRecallOf(task, result);
        if (symbolRecall < 1.0 && task.MustFindFiles.Count > 0)
        {
            string path = task.MustFindFiles[0];
            if (Core.Outline.Outline.Query(_repo.Store, path) is { } outline)
            {
                AccountRendered(
                    OutlineOutput.Render(outline, OutputFormat.Json),
                    McpSessionFixture.Arguments(("file", path)),
                    "repoctx.get_outline");
                symbolRecall = task.MustFindSymbols.Count == 0
                    ? 1.0
                    : (double)task.MustFindSymbols.Count(s => outline.Symbols.Any(o => o.Name == s))
                        / task.MustFindSymbols.Count;
            }
        }

        // Step 4: related only for a dependency/impact question whose edges are absent.
        if (task.Class == TaskClass.Impact && task.MustFindFiles.Any(f => !found.Contains(f)))
        {
            string path = task.MustFindFiles[0];
            if (Related.Query(_repo.Store, path) is { } related)
            {
                AccountRendered(
                    RelatedOutput.Render(related, OutputFormat.Json),
                    McpSessionFixture.Arguments(("file", path)),
                    "repoctx.get_related_files");
                found.UnionWith(related.Entries.Select(entry => entry.Path));
            }
        }

        // Step 5: full-file reads declared by the frozen task policy, plus a
        // fallback read for any labelled range the context response missed.
        double spanRecall = SpanRecallOf(task, result);
        var readPaths = new HashSet<string>(StringComparer.Ordinal);
        if (task.FullReadExpected)
        {
            readPaths.UnionWith(task.MustFindFiles);
        }

        if (spanRecall < 1.0)
        {
            readPaths.UnionWith(task.MustCoverSpans.Select(span => span.Path));
        }

        foreach (string path in readPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (_repo.Store.FindFile(path) is { } row)
            {
                fullReadTokens += row.TokenCount;
                fullReads++;
            }
        }

        bool spansComplete = task.MustCoverSpans.All(required =>
            readPaths.Contains(required.Path)
            || result.Items
                .Where(item => item.Path == required.Path)
                .SelectMany(item => item.Spans ?? [])
                .Any(span => required.CoveredBy(span.StartLine, span.EndLine)));
        bool expectedReadsComplete =
            !task.FullReadExpected || task.MustFindFiles.All(readPaths.Contains);
        bool complete = spansComplete && expectedReadsComplete && symbolRecall >= 1.0
            && task.MustFindFiles.All(found.Contains);

        return new WorkflowRun
        {
            TaskId = task.Id,
            Steps = steps,
            EvidenceComplete = complete,
            Cost = new WorkflowCost
            {
                CoreDocumentTokens = coreTokens,
                CliStdoutTokens = cliTokens,
                McpContentTokens = mcpContent,
                McpTransportTokens = mcpTransport,
                SessionOverheadTokens = SessionOverhead.Value,
                CallArgumentTokens = argumentTokens,
                FullReadTokens = fullReadTokens,
                Calls = steps.Count,
                FullFileReads = fullReads,
            },
        };

        void Account(
            ContextResult cliResult, ContextResult mcpResultForCall,
            string args, string tool)
        {
            string core = ContextOutput.Render(
                cliResult, OutputFormat.Json, Surfaces.Core);
            string cli = ContextOutput.Render(
                cliResult, OutputFormat.Json, Surfaces.Cli);
            string mcp = ContextOutput.Render(
                mcpResultForCall, OutputFormat.Json, Surfaces.McpText);
            AccountSurfaces(core, cli, mcp, args, tool);
        }

        void AccountRendered(string rendered, string args, string tool)
            => AccountSurfaces(rendered, rendered, rendered, args, tool);

        void AccountSurfaces(
            string coreRendered, string cliRendered, string mcpRendered,
            string args, string tool)
        {
            int core = Tokens.Count(coreRendered);
            coreTokens += core;

            // CLI stdout carries exactly one trailing newline (CommandSupport).
            cliTokens += Tokens.Count(
                cliRendered.EndsWith('\n') ? cliRendered : cliRendered + "\n");

            // The MCP model-visible block is the same document; the JSON-RPC
            // envelope around it costs extra because the payload is re-escaped.
            int mcp = Tokens.Count(mcpRendered);
            mcpContent += mcp;
            mcpTransport += Tokens.Count(McpSessionFixture.Envelope(mcpRendered)) - mcp;

            argumentTokens += Tokens.Count(args);
            steps.Add(new WorkflowStep(tool, args, core));
        }
    }

    private static double SymbolRecallOf(EvalTask task, ContextResult result)
    {
        if (task.MustFindSymbols.Count == 0)
        {
            return 1.0;
        }

        HashSet<string> delivered =
        [
            .. result.Items.SelectMany(i => i.Symbols ?? []).Select(s => s.Name),
            .. result.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Symbol).OfType<string>(),
        ];

        return (double)task.MustFindSymbols.Count(delivered.Contains) / task.MustFindSymbols.Count;
    }

    private static double SpanRecallOf(EvalTask task, ContextResult result)
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
}

/// <summary>
/// Frozen canonical offline models for the MCP session surface, so transport
/// and session-overhead accounting is reproducible without starting a server.
/// They use production instructions/generated schemas but are not captured SDK
/// initialization or call frames.
/// </summary>
public static class McpSessionFixture
{
    /// <summary>
    /// Canonical serialization of the production server instructions and
    /// generated tool definitions, counted once per simulated session.
    /// </summary>
    public static string InstructionsAndToolSchemas { get; } =
        JsonSerializer.Serialize(
            new
            {
                instructions = McpServerRunner.Instructions,
                tools = McpTools.Build()
                    .Select(tool => tool.ProtocolTool)
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .ToArray(),
            },
            OutputJson.Options);

    /// <summary>Serializes the exact MCP arguments object, omitting absent options.</summary>
    public static string Arguments(params (string Name, object? Value)[] values)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, object? value) in values)
        {
            if (value is not null)
            {
                arguments[name] = value;
            }
        }

        return JsonSerializer.Serialize(arguments, OutputJson.Options);
    }

    /// <summary>
    /// Wraps a result in a canonical JSON-RPC response-envelope model. The
    /// volatile request ID is deliberately excluded so identical calls compare
    /// equal; the result is not asserted to be an SDK-captured wire frame.
    /// </summary>
    public static string Envelope(string resultJson) =>
        JsonSerializer.Serialize(
            new
            {
                jsonrpc = "2.0",
                result = new
                {
                    content = new[] { new { type = "text", text = resultJson } },
                    isError = false,
                },
            },
            OutputJson.Options);
}
