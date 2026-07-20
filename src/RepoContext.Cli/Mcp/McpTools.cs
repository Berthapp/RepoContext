using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
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
                    "Full-text (BM25) search across the indexed repository. Returns a JSON "
                    + "document (schema_version, results[] with path, score, kind, lines and "
                    + "machine-readable reasons). Use for finding files or symbols by term.")),
            McpServerTool.Create(
                (Func<string, int, int?, string, string[]?, string?, bool, bool, CallToolResult>)GetContext,
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
                (Func<bool, CallToolResult>)GetChanges,
                Describe("repoctx.get_changes",
                    "Diff the working tree against the index: added/modified/deleted files plus the "
                    + "indexed files that import or test them. With patch=true, modified files carry "
                    + "delta hunks so an edit costs a patch instead of a full re-read. Use after "
                    + "editing to learn what is stale (then re-run 'repoctx index') instead of "
                    + "re-reading everything.")),
            McpServerTool.Create(
                (Func<string, string, string[]?, string[]?, string?, CallToolResult>)MemoryAdd,
                Describe("repoctx.memory_add",
                    "Store one distilled insight in the repository's agent memory so the next "
                    + "session pays a ~50-token recall instead of a re-discovery. Kinds: 'note' "
                    + "(knowledge), 'decision' (a why), 'constraint' (a warning). Link the files "
                    + "the insight is about — their content hashes are recorded and the memory is "
                    + "flagged stale when they change. Keep it to 1-2 sentences. Use after "
                    + "completing a task, for anything that took real work to figure out.")),
            McpServerTool.Create(
                (Func<string?, int, string?, string?, string?, bool, CallToolResult>)MemorySearch,
                Describe("repoctx.memory_search",
                    "Recall stored agent memories deterministically: term/tag/path scoring with "
                    + "machine-readable reasons and hash-based stale flags. Query before exploring "
                    + "a topic — a stale-free hit replaces a whole re-discovery. Omit the query to "
                    + "list entries. get_context already folds matching memories into its bundle.")),
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
        string? session = null,
        [Description("Lossy: drop full-line comments and blank runs from embedded slices "
            + "(outline docs already summarize them). Line ranges become approximate.")]
        bool stripComments = false,
        [Description("Fold relevant stored agent memories into the bundle (ADR 0013).")]
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
            StripComments = stripComments,
            Memories = includeMemory
                ? Commands.ContextCommand.VisibleMemories(layout, session)
                : null,
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

    private static CallToolResult GetChanges(
        [Description("Include delta hunks (working tree vs indexed content) for modified files.")]
        bool patch = false)
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

        TokenScale scale = TokenScale.From(config);
        ChangedResult result = ChangeDetector.Run(layout, config, store, patch, scale);
        string rendered = ChangedOutput.Render(result, OutputFormat.Json);
        UsageRecorder.Record(layout, "changed", UsageSources.Mcp, rendered,
            replacedTokens: UsageMeter.PatchReplacedTokens(result),
            scale: scale);
        return Ok(rendered);
    }

    private static CallToolResult MemoryAdd(
        [Description("The distilled insight to remember (1-2 sentences, max 2000 characters).")]
        string text,
        [Description("Memory kind: 'note' (knowledge), 'decision' (a why) or 'constraint' (a warning).")]
        string kind = "note",
        [Description("Repo-relative files this memory is about; their hashes make staleness detectable.")]
        string[]? files = null,
        [Description("Lowercase tags for recall.")] string[]? tags = null,
        [Description("Session name to scope the memory to (short-term); omit for long-term.")]
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

        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        if (OutdatedSchema(store) is { } outdated)
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
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }

        string rendered = MemoryOutput.RenderAdd(
            entry, updated, MemoryStore.Load(layout).Count, OutputFormat.Json);
        UsageRecorder.Record(layout, "memory", UsageSources.Mcp, rendered,
            scale: Commands.CommandSupport.ScaleFor(layout));
        return Ok(rendered);
    }

    private static CallToolResult MemorySearch(
        [Description("Free-text recall query; omit to list entries.")] string? query = null,
        [Description("Maximum number of entries (default 10).")] int top = 10,
        [Description("Restrict to one kind: 'note', 'decision' or 'constraint'.")] string? kind = null,
        [Description("Restrict to memories linked to this repo-relative file.")] string? file = null,
        [Description("Also include this session's short-term memories.")] string? session = null,
        [Description("Return only stale entries (linked files changed since they were written).")]
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
        if (OutdatedSchema(store) is { } outdated)
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
            scale: Commands.CommandSupport.ScaleFor(layout));
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
