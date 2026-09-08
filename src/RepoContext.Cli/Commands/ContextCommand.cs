using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx context</c> command (spec F5, pipeline chapter 6).</summary>
public static class ContextCommand
{
    public static Command Build()
    {
        var task = new Argument<string>("task")
        {
            Description = "A natural-language description of what you want to do.",
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of new files. Reused units never consume a slot.",
            DefaultValueFactory = _ => 8,
        };
        var budget = new Option<int?>("--budget-tokens")
        {
            Description = "Compatibility 'charged work' cap (active token-profile counts): projected reads for "
                          + "paths, embedded content otherwise. For a hard ceiling on the response "
                          + "itself use --response-budget-tokens.",
        };
        var responseBudget = new Option<int?>("--response-budget-tokens")
        {
            Description = "Hard ceiling on the active token-profile cost of the exact serialized "
                          + "response, including its trailing newline.",
        };
        var readBudget = new Option<int?>("--projected-read-budget-tokens")
        {
            Description = "Hard ceiling on the full-file reads implied by delivered pointers.",
        };
        var detail = new Option<string>("--detail")
        {
            Description = "Per-file detail: paths (pointers), outline (symbol skeletons), slices "
                          + "(source content), or auto to pick slices for change tasks and outline "
                          + "for survey questions.",
            DefaultValueFactory = _ => "paths",
        };
        var snippets = new Option<bool>("--snippets")
        {
            Description = "Alias for --detail slices.",
        };
        var known = new Option<string[]>("--known")
        {
            Description = "A file you hold IN FULL, as <path>@<hash> (repeatable). Never derive this "
                          + "from a slice or outline - use --seen for delivered ranges.",
        };
        var seen = new Option<string[]>("--seen")
        {
            Description = "A receipt from evidence you already received (repeatable). Only that exact "
                          + "span or symbol is suppressed; unseen parts of the same file still arrive.",
        };
        var session = new Option<string?>("--session")
        {
            Description = "Track whole-file claims and exact evidence receipts locally under " +
                          "this session name (.repoctx/sessions/), so they need not be echoed. " +
                          "Explicit --known and --seen entries are merged in.",
        };
        var stripComments = new Option<bool>("--strip-comments")
        {
            Description = "Lossy: drop full-line comments and blank runs from embedded slices " +
                          "(outline docs already summarize them). Line ranges become approximate.",
        };
        var noMemory = new Option<bool>("--no-memory")
        {
            Description = "Exclude stored agent memories from the bundle.",
        };
        var path = CommandSupport.PathScopeOption();
        var ensureFresh = new Option<bool>("--ensure-fresh")
        { Description = "Refresh the local index before querying, including after edits or branch changes." };
        var compact = new Option<bool>("--compact")
        { Description = "Use context JSON schema v5, omitting deprecated fields and duplicated source text. Requires --format json." };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("context",
            "Return a compact, explained context bundle for a natural-language task.")
        {
            task,
            top,
            budget,
            responseBudget,
            readBudget,
            detail,
            snippets,
            known,
            seen,
            session,
            stripComments,
            noMemory,
            path,
            ensureFresh,
            compact,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
                return ExitCode.InvalidArguments;
            }

            int topN = parseResult.GetValue(top);
            bool compactJson = parseResult.GetValue(compact);
            if (compactJson && outputFormat != OutputFormat.Json)
            {
                Console.Error.WriteLine("--compact requires --format json.");
                return ExitCode.InvalidArguments;
            }

            if (topN <= 0)
            {
                Console.Error.WriteLine("--top must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            if (!Positive(parseResult.GetValue(budget), "--budget-tokens", out int? budgetTokens)
                || !Positive(parseResult.GetValue(responseBudget), "--response-budget-tokens",
                    out int? responseBudgetTokens)
                || !Positive(parseResult.GetValue(readBudget), "--projected-read-budget-tokens",
                    out int? readBudgetTokens))
            {
                return ExitCode.InvalidArguments;
            }

            // A null level means 'auto': it cannot be resolved until the query has
            // been analyzed against this repository's configuration.
            if (!TryParseDetail(parseResult.GetValue(detail), out ContextDetail? requestedDetail))
            {
                Console.Error.WriteLine("Invalid --detail. Use 'auto', 'paths', 'outline' or 'slices'.");
                return ExitCode.InvalidArguments;
            }

            // --snippets is the pre-M6 spelling of slice detail; an explicit
            // --detail wins.
            if (parseResult.GetValue(snippets) && parseResult.GetResult(detail) is null or { Implicit: true })
            {
                requestedDetail = ContextDetail.Slices;
            }

            if (!TryParseKnown(parseResult.GetValue(known), out Dictionary<string, string>? knownMap))
            {
                Console.Error.WriteLine("Invalid --known. Use <path>@<hash> (hash as printed by context/outline).");
                return ExitCode.InvalidArguments;
            }

            string[] seenReceipts = parseResult.GetValue(seen) ?? [];
            if (Array.Find(seenReceipts, r => !Receipt.IsWellFormed(r)) is { } malformed)
            {
                Console.Error.WriteLine(
                    $"Invalid --seen receipt '{malformed}'. Pass a receipt exactly as returned in "
                    + "a previous response.");
                return ExitCode.InvalidArguments;
            }

            // An explicit --session always wins; REPOCTX_SESSION only fills the gap,
            // so reuse can be a property of how the agent was launched instead of
            // something it has to remember on every call.
            string? sessionName = parseResult.GetValue(session);
            bool sessionFromEnvironment = false;
            if (sessionName is null && AmbientSession.TryGetName(out string? ambient))
            {
                sessionName = ambient;
                sessionFromEnvironment = true;
            }

            if (sessionName is not null && !SessionStore.IsValidName(sessionName))
            {
                Console.Error.WriteLine(sessionFromEnvironment
                    ? $"Invalid {AmbientSession.VariableName} '{sessionName}'. Use 1-64 characters "
                      + "from A-Z, a-z, 0-9, '.', '_', '-'."
                    : "Invalid --session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || (!layout.HasIndex && !parseResult.GetValue(ensureFresh)))
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            if (parseResult.GetValue(ensureFresh))
            {
                IndexStats refreshed = new Indexer(layout, config, CliInfo.Version).Run(full: false);
                if (refreshed.Unreadable > 0 || refreshed.UnreadableIgnoreFiles.Count > 0)
                {
                    Console.Error.WriteLine("Could not verify freshness: files or ignore rules were unreadable. Run 'repoctx index' for details.");
                    return ExitCode.NoIndex;
                }
            }

            if (sessionName is not null)
            {
                SessionState state = SessionStore.LoadState(layout, sessionName);
                knownMap = MergeKnown(state.Known, knownMap);
                seenReceipts = MergeSeen(state.Seen, seenReceipts);
            }

            string query = parseResult.GetValue(task) ?? string.Empty;

            // 'auto' is resolved here, before the engine runs, so the response
            // reports the concrete level exactly as if the caller had named it —
            // no extra wire field, and the cost oracle measures the same bytes.
            ContextDetail detailLevel = requestedDetail
                ?? DetailPolicy.Resolve(QueryAnalyzer.Analyze(query, config).Terms).Detail;

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            TokenScale scale = TokenScale.From(config);
            var costModel = ContextCostModel.ForCli(outputFormat, scale, compactJson);
            var engine = new ContextEngine(store, config);
            ContextResult result = engine.Run(query, new ContextOptions
            {
                Top = topN,
                BudgetTokens = budgetTokens,
                ResponseBudgetTokens = responseBudgetTokens,
                ProjectedReadBudgetTokens = readBudgetTokens,
                Detail = detailLevel,
                Known = knownMap,
                Seen = seenReceipts,
                StripComments = parseResult.GetValue(stripComments),
                // Text/md deliver raw slice text; only JSON pays the escape tax,
                // so charge in serialized form only when JSON is the output (ADR 0012).
                SerializedCharging = outputFormat == OutputFormat.Json,
                Memories = parseResult.GetValue(noMemory)
                    ? null
                    : VisibleMemories(layout, sessionName),
                Scope = Core.Query.PathScope.From(parseResult.GetValue(path)),
            }, responseBudgetTokens is null ? null : costModel);

            if (result.Shortfall is { } shortfall)
            {
                // No partial success payload is emitted on this channel.
                Console.Error.WriteLine(
                    $"--response-budget-tokens {shortfall.RequestedBudgetTokens} cannot fit the smallest "
                    + $"useful response; retry_budget_tokens={shortfall.RetryBudgetTokens}.");
                return ExitCode.InvalidArguments;
            }

            string rendered = ContextOutput.Render(result, outputFormat, Surfaces.Cli, compactJson);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "context", UsageSources.Cli, costModel.SurfaceText(result),
                UsageMeter.ReplacedTokens(result, path => store.FindFile(path)?.TokenCount),
                files: result.Items.Count,
                unchanged: result.ReusedFilesCount,
                scale: scale);
            if (sessionName is not null)
            {
                SessionStore.Save(layout, sessionName, result, knownMap, seenReceipts);
            }

            return ExitCode.Success;
        });

        return command;
    }

    private static bool Positive(int? value, string option, out int? parsed)
    {
        parsed = value;
        if (value is <= 0)
        {
            Console.Error.WriteLine($"{option} must be greater than zero.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses <c>--detail</c>. A parsed value of <c>null</c> means <c>auto</c>:
    /// valid, but not resolvable until the query has been analyzed.
    /// </summary>
    private static bool TryParseDetail(string? value, out ContextDetail? detail)
    {
        switch (value?.ToLowerInvariant())
        {
            case "auto":
                detail = null;
                return true;
            case null or "" or "paths":
                detail = ContextDetail.Paths;
                return true;
            case "outline":
                detail = ContextDetail.Outline;
                return true;
            case "slices":
                detail = ContextDetail.Slices;
                return true;
            default:
                detail = ContextDetail.Paths;
                return false;
        }
    }

    /// <summary>
    /// The memories visible to this call: long-term entries always, a
    /// session's short-term entries only when that session is active (ADR
    /// 0013). Loading here keeps the engine free of file I/O.
    /// </summary>
    internal static IReadOnlyList<Core.Memory.MemoryEntry> VisibleMemories(
        RepoLayout layout, string? sessionName) =>
        Core.Memory.MemoryStore.Load(layout)
            .Where(m => m.Session is null || (sessionName is not null && m.Session == sessionName))
            .ToList();

    /// <summary>Session entries seed the map; explicit <c>--known</c> entries win.</summary>
    private static Dictionary<string, string> MergeKnown(
        IReadOnlyDictionary<string, string> sessionKnown, Dictionary<string, string>? explicitKnown)
    {
        var merged = new Dictionary<string, string>(sessionKnown, StringComparer.Ordinal);
        foreach ((string path, string hash) in explicitKnown ?? new Dictionary<string, string>())
        {
            merged[path] = hash;
        }

        return merged;
    }

    /// <summary>
    /// Session receipts seed the reuse set; explicit <c>--seen</c> entries are
    /// unioned in. Sorting makes the persisted/request identity independent of
    /// argument order.
    /// </summary>
    private static string[] MergeSeen(
        IReadOnlyList<string> sessionSeen, IReadOnlyList<string> explicitSeen) =>
        sessionSeen.Concat(explicitSeen)
            .Where(Receipt.IsWellFormed)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(receipt => receipt, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Parses repeated <c>path@hash</c> entries; the last '@' separates.</summary>
    private static bool TryParseKnown(string[]? entries, out Dictionary<string, string>? known)
    {
        known = null;
        if (entries is not { Length: > 0 })
        {
            return true;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            int at = entry.LastIndexOf('@');
            if (at <= 0 || at == entry.Length - 1)
            {
                return false;
            }

            map[entry[..at]] = entry[(at + 1)..];
        }

        known = map;
        return true;
    }
}
