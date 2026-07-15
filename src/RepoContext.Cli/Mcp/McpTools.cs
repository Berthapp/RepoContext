using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Indexing;
using RepoContext.Core.Query;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Mcp;

/// <summary>
/// The MCP tools exposed by <c>repoctx mcp</c> (M5 + M6, product doc §11,
/// ADR 0008/0010). Each tool is a thin query wrapper over the same
/// deterministic query engines the CLI uses and returns the identical JSON
/// contract as <c>--format json</c> (same <c>schema_version</c>, same fields).
/// Tools never mutate the index.
/// </summary>
/// <remarks>
/// Tool handlers resolve the index per call from the current working directory
/// (as the CLI does), so a re-index is picked up without restarting the server.
/// Successful calls append to the local usage ledger, so they are non-destructive
/// but not read-only or idempotent at the protocol level. Failures (no index, bad
/// arguments) are returned as tool errors with a human-readable message rather
/// than thrown, because the SDK masks thrown exception details.
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
                (Func<string, int, int?, string, string[]?, string?, CallToolResult>)GetContext,
                Describe("repoctx.get_context",
                    "Ranked, explained context bundle for a natural-language task, packed into a "
                    + "real-BPE token budget. detail='paths' returns pointers with exact full-read "
                    + "costs; 'outline' adds symbol skeletons; 'slices' embeds the best source "
                    + "slice so no file read is needed. Pass a stable 'session' name and delivered "
                    + "slices are tracked server-side, so unchanged files return as zero-cost "
                    + "markers on later calls without echoing hashes. Pass path@hash as 'known' "
                    + "only for files whose content you already hold; do not echo hashes from "
                    + "pointer-only results. Prefer this over reading files.")),
            McpServerTool.Create(
                (Func<string, CallToolResult>)GetRelatedFiles,
                Describe("repoctx.get_related_files",
                    "List the files related to a given file: its imports, dependents and linked "
                    + "tests. Returns a JSON document (schema_version, results[] with path, relation "
                    + "and reasons).")),
            McpServerTool.Create(
                (Func<string, CallToolResult>)GetOutline,
                Describe("repoctx.get_outline",
                    "A file's skeleton: symbols with signatures, lines and doc summaries, plus its "
                    + "content hash and exact full-read token cost. Costs a fraction of reading the "
                    + "file - use it to decide whether (and which part of) a file is worth reading.")),
            McpServerTool.Create(
                (Func<CallToolResult>)GetChanges,
                Describe("repoctx.get_changes",
                    "Diff the working tree against the index: added/modified/deleted files plus the "
                    + "indexed files that import or test them. Use after editing to learn what is "
                    + "stale (then re-run 'repoctx index') instead of re-reading everything.")),
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
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        IReadOnlyList<SearchHit> hits = store.Search(match, top, symbols);
        string rendered = SearchOutput.Render(query ?? string.Empty, hits, OutputFormat.Json);
        UsageRecorder.Record(layout, "search", UsageSources.Mcp, rendered,
            scale: Commands.CommandSupport.ScaleFor(layout));
        return Ok(rendered);
    }

    private static CallToolResult GetContext(
        [Description("A natural-language description of what you want to do.")] string task,
        [Description("Maximum number of files (default 8).")] int top = 8,
        [Description("Token budget the bundle is packed into (real BPE counts).")] int? budgetTokens = null,
        [Description("Per-file detail: 'paths', 'outline' or 'slices'.")] string detail = "paths",
        [Description("Files whose full content you already hold, each as path@hash; "
            + "unchanged ones return as zero-cost markers.")]
        string[]? known = null,
        [Description("Session name (A-Za-z0-9._-): the known-file set is tracked server-side "
            + "under .repoctx/sessions/, so hashes need not be echoed back.")]
        string? session = null)
    {
        if (top <= 0)
        {
            return Fail("top must be greater than zero.");
        }

        if (budgetTokens is <= 0)
        {
            return Fail("budgetTokens must be greater than zero.");
        }

        ContextDetail? detailLevel = detail?.ToLowerInvariant() switch
        {
            null or "" or "paths" => ContextDetail.Paths,
            "outline" => ContextDetail.Outline,
            "slices" => ContextDetail.Slices,
            _ => null,
        };
        if (detailLevel is null)
        {
            return Fail("detail must be 'paths', 'outline' or 'slices'.");
        }

        Dictionary<string, string>? knownMap = null;
        if (known is { Length: > 0 })
        {
            knownMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string entry in known)
            {
                int at = entry?.LastIndexOf('@') ?? -1;
                if (at <= 0 || at == entry!.Length - 1)
                {
                    return Fail($"Invalid known entry '{entry}'. Use path@hash.");
                }

                knownMap[entry[..at]] = entry[(at + 1)..];
            }
        }

        if (session is not null && !SessionStore.IsValidName(session))
        {
            return Fail("Invalid session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
        }

        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        if (session is not null)
        {
            var merged = new Dictionary<string, string>(
                SessionStore.Load(layout, session), StringComparer.Ordinal);
            foreach ((string path, string hash) in knownMap ?? new Dictionary<string, string>())
            {
                merged[path] = hash;
            }

            knownMap = merged;
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        var engine = new ContextEngine(store, config);
        ContextResult result = engine.Run(task ?? string.Empty, new ContextOptions
        {
            Top = top,
            BudgetTokens = budgetTokens,
            Detail = detailLevel.Value,
            Known = knownMap,
        });

        TokenScale scale = TokenScale.From(config);
        string rendered = ContextOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "context", UsageSources.Mcp, rendered,
            UsageMeter.ReplacedTokens(result,
                path => store.FindFile(path) is { } f ? scale.Apply(f.TokenCount) : null),
            files: result.Items.Count,
            unchanged: result.Items.Count(i => i.Unchanged),
            scale: scale);
        if (session is not null)
        {
            SessionStore.Save(layout, session, result);
        }

        return Ok(rendered);
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
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        RelatedResult? result = Related.Query(store, relative);
        if (result is null)
        {
            return Fail($"File not found in index: {relative}");
        }

        string rendered = RelatedOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "related", UsageSources.Mcp, rendered,
            scale: Commands.CommandSupport.ScaleFor(layout));
        return Ok(rendered);
    }

    private static CallToolResult GetOutline(
        [Description("The file (repo-relative or absolute path) to outline.")] string file)
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
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        TokenScale scale = Commands.CommandSupport.ScaleFor(layout);
        Core.Outline.OutlineResult? result = Core.Outline.Outline.Query(store, relative, scale);
        if (result is null)
        {
            return Fail($"File not found in index: {relative}");
        }

        string rendered = OutlineOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "outline", UsageSources.Mcp, rendered,
            replacedTokens: UsageMeter.OutlineReplacedTokens(result), files: 1, scale: scale);
        return Ok(rendered);
    }

    private static CallToolResult GetChanges()
    {
        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        ChangedResult result = ChangeDetector.Run(layout, config, store);
        string rendered = ChangedOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "changed", UsageSources.Mcp, rendered,
            scale: TokenScale.From(config));
        return Ok(rendered);
    }

    /// <summary>Resolves an initialized, indexed repository from the working directory.</summary>
    private static RepoLayout? Locate()
    {
        RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
        return layout is not null && layout.HasIndex ? layout : null;
    }

    private static CallToolResult? OutdatedSchema(IndexStore store) =>
        store.IsSchemaCurrent
            ? null
            : Fail("Index schema is outdated. Run 'repoctx index' to rebuild it.");

    private static McpServerToolCreateOptions Describe(string name, string description) => new()
    {
        Name = name,
        Description = description,
        ReadOnly = false,
        Idempotent = false,
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
