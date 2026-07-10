using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Mcp;

/// <summary>
/// The MCP tools exposed by <c>repoctx mcp</c> (M5, product doc §11). Each tool
/// is a thin, read-only wrapper over the same deterministic query engines the
/// CLI uses and returns the identical JSON contract as <c>--format json</c>
/// (same <c>schema_version</c>, same fields). Tools never mutate the index.
/// </summary>
/// <remarks>
/// Tool handlers resolve the index per call from the current working directory
/// (as the CLI does), so a re-index is picked up without restarting the server.
/// Failures (no index, bad arguments) are returned as tool errors with a
/// human-readable message rather than thrown, because the SDK masks thrown
/// exception details.
/// </remarks>
public static class McpTools
{
    /// <summary>Builds the tool collection served over stdio.</summary>
    public static McpServerPrimitiveCollection<McpServerTool> Build()
    {
        return new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(
                (Func<string, int, bool, CallToolResult>)Search,
                Describe("repoctx.search",
                    "Full-text (BM25) search across the indexed repository. Returns a JSON "
                    + "document (schema_version, results[] with path, score, kind, lines and "
                    + "machine-readable reasons). Use for finding files or symbols by term.")),
            McpServerTool.Create(
                (Func<string, int, int?, bool, CallToolResult>)GetContext,
                Describe("repoctx.get_context",
                    "Return a compact, ranked and explained context bundle for a natural-language "
                    + "task, within a token budget. Prefer this over reading many files. Returns a "
                    + "JSON document (schema_version, results[] with path, score, reasons, lines and "
                    + "estimated tokens).")),
            McpServerTool.Create(
                (Func<string, CallToolResult>)GetRelatedFiles,
                Describe("repoctx.get_related_files",
                    "List the files related to a given file: its imports, dependents and linked "
                    + "tests. Returns a JSON document (schema_version, results[] with path, relation "
                    + "and reasons).")),
        };
    }

    private static CallToolResult Search(
        [Description("The text to search for.")] string query,
        [Description("Maximum number of results (default 10).")] int top = 10,
        [Description("Restrict the search to symbols (classes, functions, ...).")] bool symbols = false)
    {
        if (top <= 0)
        {
            return Fail("top must be greater than zero.");
        }

        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        string? match = FtsQuery.Build(query ?? string.Empty);
        if (match is null)
        {
            return Fail("Query has no searchable terms.");
        }

        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        IReadOnlyList<SearchHit> hits = store.Search(match, top, symbols);
        return Ok(SearchOutput.Render(query ?? string.Empty, hits, OutputFormat.Json));
    }

    private static CallToolResult GetContext(
        [Description("A natural-language description of what you want to do.")] string task,
        [Description("Maximum number of files (default 8).")] int top = 8,
        [Description("Approximate token budget for the returned files.")] int? budgetTokens = null,
        [Description("Include a code snippet for each file.")] bool snippets = false)
    {
        if (top <= 0)
        {
            return Fail("top must be greater than zero.");
        }

        if (budgetTokens is <= 0)
        {
            return Fail("budgetTokens must be greater than zero.");
        }

        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        var engine = new ContextEngine(store, config);
        ContextResult result = engine.Run(task ?? string.Empty, new ContextOptions
        {
            Top = top,
            BudgetTokens = budgetTokens,
            Snippets = snippets,
        });

        return Ok(ContextOutput.Render(result, OutputFormat.Json));
    }

    private static CallToolResult GetRelatedFiles(
        [Description("The file (repo-relative or absolute path) to find related files for.")] string file)
    {
        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        string? relative = layout.ToRelativePath(file ?? string.Empty, Directory.GetCurrentDirectory());
        if (relative is null)
        {
            return Fail("Path is outside the repository.");
        }

        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        RelatedResult? result = Related.Query(store, relative);
        if (result is null)
        {
            return Fail($"File not found in index: {relative}");
        }

        return Ok(RelatedOutput.Render(result, OutputFormat.Json));
    }

    /// <summary>Resolves an initialized, indexed repository from the working directory.</summary>
    private static RepoLayout? Locate()
    {
        RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
        return layout is not null && layout.HasIndex ? layout : null;
    }

    private static McpServerToolCreateOptions Describe(string name, string description) => new()
    {
        Name = name,
        Description = description,
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false,
    };

    private static CallToolResult Ok(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    private static CallToolResult Fail(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static CallToolResult NoIndex() =>
        Fail("No index found. Run 'repoctx init' and 'repoctx index' first.");
}
