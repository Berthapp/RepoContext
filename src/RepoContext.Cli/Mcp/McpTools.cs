using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;
using RepoContext.Core.Memory;
using RepoContext.Core.Query;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Mcp;

/// <summary>
/// The MCP tools exposed by <c>repoctx mcp</c> (M5 + M6 + M9, product doc §11,
/// ADR 0008/0010/0013). Each tool is a thin wrapper over the same
/// deterministic engines the CLI uses and returns the identical JSON contract
/// as <c>--format json</c> (same <c>schema_version</c>, same fields). Tools
/// never mutate the index; <c>memory_add</c> appends to the separate agent
/// memory store (curation via <c>memory rm</c> stays CLI-only, under human
/// supervision).
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
                    "Find indexed files or symbols by term; returns ranked paths, lines, "
                    + "scores, kinds, and reasons.")),
            McpServerTool.Create(
                (Func<string, int, int?, int?, int?, string, string[]?, string[]?, string?, bool, bool,
                    CallToolResult>)GetContext,
                Describe("repoctx.get_context",
                    "Primary context tool. Ranks task-relevant files under response/read budgets. "
                    + "detail: paths=locations, outline=symbols, slices=source spans. Reuse evidence "
                    + "via seen receipts or session; known=path@hash requires a full-file read. "
                    + "stripComments is lossy; matching memories are included by default.")),
            McpServerTool.Create(
                (Func<string, CallToolResult>)GetRelatedFiles,
                Describe("repoctx.get_related_files",
                    "Return imports, dependents, and linked tests for a file, with reasons.")),
            McpServerTool.Create(
                (Func<string, CallToolResult>)GetOutline,
                Describe("repoctx.get_outline",
                    "Return a file's symbols, signatures, lines, docs, hash, and full-read "
                    + "token cost.")),
            McpServerTool.Create(
                (Func<bool, CallToolResult>)GetChanges,
                Describe("repoctx.get_changes",
                    "Return added/modified/deleted files and affected importers/tests. patch=true "
                    + "includes delta hunks. Use after edits; re-index if stale.")),
            McpServerTool.Create(
                (Func<string, string, string[]?, string[]?, string?, CallToolResult>)MemoryAdd,
                Describe("repoctx.memory_add",
                    "Store a durable 1-2 sentence insight. Link affected files so changed hashes "
                    + "mark it stale; kinds: note, decision, constraint.")),
            McpServerTool.Create(
                (Func<string?, int, string?, string?, string?, bool, CallToolResult>)MemorySearch,
                Describe("repoctx.memory_search",
                    "Search local memories by term/tag/path with reasons and stale flags; omit "
                    + "query to list. get_context already includes matches.")),
        };
    }

    private static CallToolResult Search(
        [Description("Text to find.")] string query,
        [Description("Maximum results.")] int top = 10,
        [Description("Search symbols only.")] bool symbols = false)
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

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        IReadOnlyList<SearchHit> hits = store.Search(match, top, symbols);
        string rendered = SearchOutput.Render(query ?? string.Empty, hits, OutputFormat.Json);
        UsageRecorder.Record(layout, "search", UsageSources.Mcp, rendered,
            scale: TokenScale.From(config));
        return Ok(rendered);
    }

    private static CallToolResult GetContext(
        [Description("Task to gather context for.")] string task,
        [Description("Maximum new files; reused evidence consumes no slot.")]
        int top = 8,
        [Description("Compatibility charged-work token cap.")]
        int? budgetTokens = null,
        [Description("Hard exact-response token cap.")]
        int? responseBudgetTokens = null,
        [Description("Hard projected full-read token cap.")]
        int? projectedReadBudgetTokens = null,
        [Description("Detail: paths, outline, or slices.")] string detail = "paths",
        [Description("Whole files held as path@hash; never use a slice/outline hash.")]
        string[]? known = null,
        [Description("Exact evidence receipts already held.")]
        string[]? seen = null,
        [Description("Local reuse-state name (1-64 characters: A-Za-z0-9._-).")]
        string? session = null,
        [Description("Lossy removal of full-line comments and blank runs from slices.")]
        bool stripComments = false,
        [Description("Include relevant local memories.")]
        bool includeMemory = true)
    {
        if (top <= 0)
        {
            return Fail("top must be greater than zero.");
        }

        if (budgetTokens is <= 0)
        {
            return Fail("budgetTokens must be greater than zero.");
        }

        if (responseBudgetTokens is <= 0)
        {
            return Fail("responseBudgetTokens must be greater than zero.");
        }

        if (projectedReadBudgetTokens is <= 0)
        {
            return Fail("projectedReadBudgetTokens must be greater than zero.");
        }

        string[] seenReceipts = seen ?? [];
        if (Array.Find(seenReceipts, r => !Receipt.IsWellFormed(r)) is { } malformed)
        {
            return Fail($"Invalid seen receipt '{malformed}'. Pass a receipt exactly as returned.");
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
            SessionState state = SessionStore.LoadState(layout, session);
            var merged = new Dictionary<string, string>(
                state.Known, StringComparer.Ordinal);
            foreach ((string path, string hash) in knownMap ?? new Dictionary<string, string>())
            {
                merged[path] = hash;
            }

            knownMap = merged;
            seenReceipts = state.Seen.Concat(seenReceipts)
                .Where(Receipt.IsWellFormed)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(receipt => receipt, StringComparer.Ordinal)
                .ToArray();
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        TokenScale scale = TokenScale.From(config);
        var costModel = ContextCostModel.ForMcpText(scale);
        var engine = new ContextEngine(store, config);
        ContextResult result = engine.Run(task ?? string.Empty, new ContextOptions
        {
            Top = top,
            BudgetTokens = budgetTokens,
            ResponseBudgetTokens = responseBudgetTokens,
            ProjectedReadBudgetTokens = projectedReadBudgetTokens,
            Detail = detailLevel.Value,
            Known = knownMap,
            Seen = seenReceipts,
            StripComments = stripComments,
            SerializedCharging = true,
            Memories = includeMemory
                ? Commands.ContextCommand.VisibleMemories(layout, session)
                : null,
        }, responseBudgetTokens is null ? null : costModel);

        if (result.Shortfall is { } shortfall)
        {
            // Error channel: the requested success-payload budget does not apply
            // here, and no partial result is emitted alongside it.
            return Fail(
                $"responseBudgetTokens {shortfall.RequestedBudgetTokens} cannot fit the smallest "
                + $"useful response. retry_budget_tokens={shortfall.RetryBudgetTokens}");
        }

        string rendered = ContextOutput.Render(result, OutputFormat.Json, Surfaces.McpText);
        UsageRecorder.Record(layout, "context", UsageSources.Mcp, rendered,
            UsageMeter.ReplacedTokens(result,
                path => store.FindFile(path) is { } f ? scale.Apply(f.TokenCount) : null),
            files: result.Items.Count,
            unchanged: result.ReusedFilesCount,
            scale: scale);
        if (session is not null)
        {
            SessionStore.Save(layout, session, result, knownMap, seenReceipts);
        }

        return Ok(rendered);
    }

    private static CallToolResult GetRelatedFiles(
        [Description("Repository file path.")] string file)
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

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
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
            scale: TokenScale.From(config));
        return Ok(rendered);
    }

    private static CallToolResult GetOutline(
        [Description("Repository file path.")] string file)
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

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        TokenScale scale = TokenScale.From(config);
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

    private static CallToolResult GetChanges(
        [Description("Include working-tree delta hunks.")]
        bool patch = false)
    {
        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        TokenScale scale = TokenScale.From(config);
        ChangedResult result = ChangeDetector.Run(layout, config, store, patch, scale);
        string rendered = ChangedOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "changed", UsageSources.Mcp, rendered,
            replacedTokens: UsageMeter.PatchReplacedTokens(result),
            scale: scale);
        return Ok(rendered);
    }

    private static CallToolResult MemoryAdd(
        [Description("Distilled insight (1-2 sentences, maximum 2,000 characters).")]
        string text,
        [Description("Kind: note, decision, or constraint.")]
        string kind = "note",
        [Description("Linked repository files for stale detection.")]
        string[]? files = null,
        [Description("Lowercase recall tags.")] string[]? tags = null,
        [Description("Optional short-term session scope.")]
        string? session = null)
    {
        if (!MemoryKinds.IsValid(kind))
        {
            return Fail("kind must be 'note', 'decision' or 'constraint'.");
        }

        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Fail("text must not be empty.");
        }

        if (trimmed.Length > MemoryStore.MaxTextLength)
        {
            return Fail($"text exceeds {MemoryStore.MaxTextLength} characters — distill it.");
        }

        if (session is not null && !SessionStore.IsValidName(session))
        {
            return Fail("Invalid session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
        }

        var tagList = new List<string>();
        foreach (string input in tags ?? [])
        {
            string t = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length is 0 or > 32 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return Fail($"Invalid tag '{input}'. Use 1-32 characters from a-z, 0-9, '-', '_'.");
            }

            if (!tagList.Contains(t, StringComparer.Ordinal))
            {
                tagList.Add(t);
            }
        }

        if (tagList.Count > MemoryStore.MaxTags)
        {
            return Fail($"At most {MemoryStore.MaxTags} tags per memory.");
        }

        if (files is { Length: > MemoryStore.MaxFiles })
        {
            return Fail($"At most {MemoryStore.MaxFiles} file links per memory.");
        }

        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        var links = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string input in files ?? [])
        {
            string? relative = layout.ToRelativePath(input ?? string.Empty, Directory.GetCurrentDirectory());
            if (relative is null)
            {
                return Fail($"Path is outside the repository: {input}");
            }

            if (store.FindFile(relative) is not { } row)
            {
                return Fail($"File not found in index: {relative}");
            }

            links[relative] = Hashes.Short(row.ContentHash);
        }

        var entry = new MemoryEntry
        {
            Id = MemoryEntry.ComputeId(kind, trimmed, session, links.Keys),
            Kind = kind,
            Text = trimmed,
            Files = links,
            Tags = tagList,
            Session = session,
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };

        bool updated;
        try
        {
            updated = MemoryStore.Add(layout, entry);
        }
        catch (Exception e) when (
            e is InvalidOperationException or IOException
                or UnauthorizedAccessException or TimeoutException)
        {
            return Fail(e.Message);
        }

        string rendered = MemoryOutput.RenderAdd(
            entry, updated, MemoryStore.Load(layout).Count, OutputFormat.Json);
        UsageRecorder.Record(layout, "memory", UsageSources.Mcp, rendered,
            scale: TokenScale.From(config));
        return Ok(rendered);
    }

    private static CallToolResult MemorySearch(
        [Description("Recall text; omit to list.")] string? query = null,
        [Description("Maximum entries.")] int top = 10,
        [Description("Filter: note, decision, or constraint.")] string? kind = null,
        [Description("Filter by linked repository file.")] string? file = null,
        [Description("Include this session's short-term memories.")] string? session = null,
        [Description("Return stale entries only.")]
        bool stale = false)
    {
        if (top <= 0)
        {
            return Fail("top must be greater than zero.");
        }

        if (kind is not null && !MemoryKinds.IsValid(kind))
        {
            return Fail("kind must be 'note', 'decision' or 'constraint'.");
        }

        if (session is not null && !SessionStore.IsValidName(session))
        {
            return Fail("Invalid session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
        }

        if (Locate() is not { } layout)
        {
            return NoIndex();
        }

        string? fileFilter = null;
        if (file is { Length: > 0 })
        {
            fileFilter = layout.ToRelativePath(file, Directory.GetCurrentDirectory());
            if (fileFilter is null)
            {
                return Fail("Path is outside the repository.");
            }
        }

        RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedIndex(store, config) is { } outdated)
        {
            return outdated;
        }

        MemoryQueryResult result = MemoryEngine.Search(MemoryStore.Load(layout), new MemoryQueryOptions
        {
            Query = query,
            Top = top,
            Kind = kind,
            File = fileFilter,
            Session = session,
            StaleOnly = stale,
        }, config, store);

        string rendered = MemoryOutput.RenderSearch(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "memory", UsageSources.Mcp, rendered,
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

    /// <summary>
    /// Rejects both an outdated on-disk schema and an index produced by different
    /// analysis versions (Q4). Serving evidence — or honouring a receipt — from
    /// stale stored analysis is exactly the failure this fails closed on.
    /// </summary>
    private static CallToolResult? OutdatedIndex(IndexStore store, RepoctxConfig config)
    {
        if (OutdatedSchema(store) is { } outdated)
        {
            return outdated;
        }

        if (!store.IsProducerCurrent)
        {
            return Fail(
                "Index was produced by a different analysis version. Run 'repoctx index' to rebuild it.");
        }

        if (!store.HasValidStateHash)
        {
            return Fail("Index state metadata is missing or invalid. Run 'repoctx index' to rebuild it.");
        }

        return store.IsIndexConfigCurrent(config)
            ? null
            : Fail("Index was built with different indexing settings. Run 'repoctx index' to refresh it.");
    }

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
